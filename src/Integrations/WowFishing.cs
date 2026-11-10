using System.Diagnostics;
using System.Runtime.InteropServices;
using IOHub.Profiles;

namespace IOHub.Integrations;

public enum WowListenState { Stopped, NoGame, Listening }

/// <summary>
/// The "WoW fishing bite" trigger's ears. Watches World of Warcraft's own sound (its audio sessions' peak meters, so
/// music or voice chat from other apps don't count) and spots the bite splash with one <see cref="SplashDetector"/>
/// per trigger, each at its own sensitivity. WoW gives addons no event for the bite, which is why this goes by sound.
///
/// Runs on its own thread, only while a WoW trigger is on. About 40 peak reads a second while WoW is running; one
/// look for WoW every 3 seconds while it isn't.
/// </summary>
public sealed class WowFishing : IDisposable
{
    readonly object _gate = new();
    Thread? _thread;
    CancellationTokenSource? _cts;
    volatile (string Id, SplashDetector Detector)[] _listeners = [];
    volatile WowListenState _state;
    volatile float _level;

    /// <summary>A trigger's splash was heard. Raised on the listening thread with the trigger's id.</summary>
    public event Action<string>? Bite;
    /// <summary>WoW started or stopped making sound (or listening stopped). Raised on the listening thread.</summary>
    public event Action? StateChanged;

    public WowListenState State => _state;
    /// <summary>WoW's sound level right now, 0..1.</summary>
    public float Level => _level;

    public static readonly (string Value, string Label, float Db)[] Sensitivities =
        [("low", "Low", 18), ("normal", "Normal", 12), ("high", "High", 8)];

    public static float ThresholdDb(string? sensitivity) =>
        Sensitivities.FirstOrDefault(s => s.Value == sensitivity).Db is > 0 and var db ? db : 12;

    /// <summary>The WoW triggers that are on, with their sensitivities. None stops listening.</summary>
    public void SetTriggers(IEnumerable<(string Id, string? Sensitivity)> triggers)
    {
        lock (_gate)
        {
            var old = _listeners;
            _listeners = triggers.Select(t =>
            {
                // Keep a trigger's detector (and its background level) when only something else changed.
                var d = old.FirstOrDefault(o => o.Id == t.Id).Detector ?? new SplashDetector();
                d.ThresholdDb = ThresholdDb(t.Sensitivity);
                return (t.Id, d);
            }).ToArray();

            if (_listeners.Length > 0 && _thread == null)
            {
                var cts = _cts = new CancellationTokenSource();
                _thread = new Thread(() => Listen(cts.Token)) { IsBackground = true, Name = "WoW fishing", Priority = ThreadPriority.AboveNormal };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
            else if (_listeners.Length == 0 && _thread != null) StopLocked();
        }
    }

    void StopLocked()
    {
        _cts!.Cancel();
        // Wait for it, so starting straight back up never leaves two listeners running.
        _thread!.Join(1000);
        _thread = null;
        _cts = null;
    }

    void SetState(WowListenState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke();
    }

    // ---------------- listening thread ----------------

    void Listen(CancellationToken stop)
    {
        var sessions = new List<IAudioMeterInformation>();
        var isWow = new Dictionary<uint, bool>();
        IMMDeviceEnumerator? devices = null;
        try { devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }
        catch { return; }

        var clock = Stopwatch.StartNew();
        long nextScan = 0;
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
                    if (sessions.Count == 0 && _state != WowListenState.NoGame) foreach (var l in _listeners) l.Detector.Reset();
                    SetState(sessions.Count == 0 ? WowListenState.NoGame : WowListenState.Listening);
                }
                if (sessions.Count == 0) { _level = 0; stop.WaitHandle.WaitOne(500); continue; }

                float peak = 0;
                foreach (var meter in sessions)
                    if (meter.GetPeakValue(out var p) == 0 && p > peak) peak = p;

                _level = peak;
                foreach (var (id, detector) in _listeners)
                {
                    if (!detector.Feed(peak, now)) continue;
                    SwitchLog.Write($"WoW fishing: splash at {20 * MathF.Log10(Math.Max(peak, 1e-5f)):0} dB, background {detector.BaselineDb:0} dB");
                    Bite?.Invoke(id);
                }
                stop.WaitHandle.WaitOne(25);
            }
        }
        finally
        {
            Release(sessions);
            Marshal.ReleaseComObject(devices);
            _level = 0;
            SetState(WowListenState.Stopped);
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
        lock (_gate) { if (_thread != null) StopLocked(); }
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
