using System.Diagnostics;
using System.Runtime.InteropServices;
using IOHub.Profiles;

namespace IOHub.Integrations;

public enum WowListenState { Stopped, NoGame, Listening }

/// <summary>
/// World of Warcraft → "Rumble on fishing bite". Watches WoW's own sound (its audio sessions' peak meters, so music
/// or voice chat from other apps don't count), spots the bite splash with <see cref="SplashDetector"/>, and rumbles
/// every Xbox controller. WoW gives addons no event for the bite, which is why this goes by sound.
///
/// Runs on its own thread, only while the connection is on or the Integrations page is showing its meter. About
/// 40 peak reads a second while WoW is running; one look for WoW every 3 seconds while it isn't.
/// </summary>
public sealed class WowFishing : IDisposable
{
    readonly AppSettings _settings;
    readonly SplashDetector _detector = new();
    readonly object _gate = new();
    Thread? _thread;
    CancellationTokenSource? _cts;
    int _watchers;

    /// <summary>On or off changed. Raised on the thread that changed it.</summary>
    public event Action? Changed;
    /// <summary>A bite was felt (and rumbled). Raised on the listening thread.</summary>
    public event Action? Bite;

    public WowFishing(AppSettings settings)
    {
        _settings = settings;
        Update();
    }

    public bool IsOn => _settings.WowFishingRumble;

    public volatile WowListenState State;
    /// <summary>WoW's sound level right now, 0..1.</summary>
    public float Level => _detector.Level;
    public DateTime? LastBite { get; private set; }
    public int Bites { get; private set; }

    public static readonly (string Value, string Label, float Db)[] Sensitivities =
        [("low", "Low", 18), ("normal", "Normal", 12), ("high", "High", 8)];
    public static readonly (string Value, string Label, double Strength)[] Strengths =
        [("light", "Light", 0.35), ("medium", "Medium", 0.65), ("strong", "Strong", 1.0)];

    float ThresholdDb => Sensitivities.FirstOrDefault(s => s.Value == _settings.WowFishingSensitivity).Db is > 0 and var db ? db : 12;
    public double Strength => Strengths.FirstOrDefault(s => s.Value == _settings.WowRumbleStrength).Strength is > 0 and var s ? s : 0.65;

    /// <summary>Turns the connection on or off. Returns an error message, or null (the shape device features use).</summary>
    public string? Set(bool on)
    {
        if (_settings.WowFishingRumble != on)
        {
            _settings.WowFishingRumble = on;
            _settings.Save();
            SwitchLog.Write($"WoW fishing rumble {(on ? "on" : "off")}");
        }
        Update();
        Changed?.Invoke();
        return null;
    }

    /// <summary>The Integrations page shows the live level, so it keeps listening while open even when the connection is off.</summary>
    public void Watch(bool watching)
    {
        lock (_gate) _watchers = Math.Max(0, _watchers + (watching ? 1 : -1));
        Update();
    }

    public void TestRumble() => Rumble.Pulse(Strength);

    void Update()
    {
        bool want;
        lock (_gate) want = IsOn || _watchers > 0;
        lock (_gate)
        {
            if (want && _thread == null)
            {
                var cts = _cts = new CancellationTokenSource();
                _thread = new Thread(() => Listen(cts.Token)) { IsBackground = true, Name = "WoW fishing", Priority = ThreadPriority.AboveNormal };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
            else if (!want && _thread != null)
            {
                _cts!.Cancel();
                // Wait for it, so turning it straight back on never leaves two listeners sharing the detector.
                _thread.Join(1000);
                _thread = null;
                _cts = null;
            }
        }
    }

    // ---------------- listening thread ----------------

    void Listen(CancellationToken stop)
    {
        var sessions = new List<IAudioMeterInformation>();
        var isWow = new Dictionary<uint, bool>();
        IMMDeviceEnumerator? devices = null;
        try { devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
        catch { State = WowListenState.Stopped; return; }

        var clock = Stopwatch.StartNew();
        long nextScan = 0;
        _detector.Reset();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                long now = clock.ElapsedMilliseconds;
                if (now >= nextScan)
                {
                    Release(sessions);
                    try { Scan(devices, sessions, isWow); } catch { }
                    // WoW can move to another sound device when you change it in its settings, so keep looking.
                    nextScan = now + (sessions.Count == 0 ? 3000 : 5000);
                    if (sessions.Count == 0 && State != WowListenState.NoGame) _detector.Reset();
                    State = sessions.Count == 0 ? WowListenState.NoGame : WowListenState.Listening;
                }
                if (sessions.Count == 0) { stop.WaitHandle.WaitOne(500); continue; }

                float peak = 0;
                foreach (var meter in sessions)
                    if (meter.GetPeakValue(out var p) == 0 && p > peak) peak = p;

                _detector.ThresholdDb = ThresholdDb;
                if (_detector.Feed(peak, now) && IsOn)
                {
                    Rumble.Pulse(Strength);
                    LastBite = DateTime.Now;
                    Bites++;
                    SwitchLog.Write($"WoW fishing: bite at {20 * MathF.Log10(Math.Max(peak, 1e-5f)):0} dB, background {_detector.BaselineDb:0} dB");
                    Bite?.Invoke();
                }
                stop.WaitHandle.WaitOne(25);
            }
        }
        finally
        {
            Release(sessions);
            Marshal.ReleaseComObject(devices);
            State = WowListenState.Stopped;
            _detector.Reset();
        }
    }

    /// <summary>Every WoW audio session on every active output device, as peak meters.</summary>
    static void Scan(IMMDeviceEnumerator devices, List<IAudioMeterInformation> found, Dictionary<uint, bool> isWow)
    {
        if (isWow.Count > 64) isWow.Clear();
        if (devices.EnumAudioEndpoints(ERender, StateActive, out var collection) != 0) return;
        try
        {
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                try
                {
                    var iid = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj) != 0 || obj is not IAudioSessionManager2 manager) continue;
                    try
                    {
                        if (manager.GetSessionEnumerator(out var list) != 0) continue;
                        try
                        {
                            list.GetCount(out int n);
                            for (int s = 0; s < n; s++)
                            {
                                if (list.GetSession(s, out var session) != 0) continue;
                                bool keep = false;
                                if (session is IAudioSessionControl2 control && control.GetProcessId(out uint pid) == 0 && pid != 0)
                                {
                                    if (!isWow.TryGetValue(pid, out keep)) isWow[pid] = keep = IsWowProcess(pid);
                                    if (keep && session is IAudioMeterInformation meter) found.Add(meter);
                                    else keep = false;
                                }
                                if (!keep) Marshal.ReleaseComObject(session);
                            }
                        }
                        finally { Marshal.ReleaseComObject(list); }
                    }
                    finally { Marshal.ReleaseComObject(manager); }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        finally { Marshal.ReleaseComObject(collection); }
    }

    /// <summary>
    /// Wow.exe (retail), WowClassic.exe, WowB/WowT (beta, PTR) and so on. Not WowVoiceProxy: that's voice chat.
    /// </summary>
    static bool IsWowProcess(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            return name.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("WowVoice", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("wowreg", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void Release(List<IAudioMeterInformation> sessions)
    {
        foreach (var s in sessions) Marshal.ReleaseComObject(s);
        sessions.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _thread?.Join(500);
            _thread = null;
        }
    }

    // ---------------- interop ----------------

    const int ERender = 0;
    const uint StateActive = 1, ClsctxAll = 23;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    // Only GetSessionEnumerator is called; the IAudioSessionManager methods before it hold their places.
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    // IAudioSessionControl's methods come first; only GetProcessId is called.
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
