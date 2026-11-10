using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using IOHub.Profiles;

namespace IOHub.Integrations;

public enum WowListenState { Stopped, NoGame, Listening }

/// <summary>
/// The "WoW fishing bite" trigger's ears. Watches World of Warcraft's own sound (its audio sessions' peak meters, so
/// music or voice chat from other apps don't count) and spots the bite splash with one <see cref="SplashDetector"/>
/// per trigger, each at its own sensitivity. WoW gives addons no event for the bite, which is why this goes by sound.
///
/// Runs on the UI thread, like <see cref="Devices.AudioService"/>'s meters, and only while a WoW trigger is on: one peak
/// read every 25 ms while WoW is running, one look for WoW every 3 seconds while it isn't. The UI thread because
/// Windows hands out the same sound device objects AudioService holds there, and .NET reuses its wrappers for them.
/// </summary>
public sealed class WowFishing : IDisposable
{
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<IAudioMeterInformation> _sessions = [];
    readonly Dictionary<uint, bool> _isWow = [];
    (string Id, SplashDetector Detector)[] _listeners = [];
    long _nextScan;
    uint _wowPid;
    WowListenState _state;
    float _level;

    /// <summary>A trigger's splash was heard, with the trigger's id. UI thread.</summary>
    public event Action<string>? Bite;
    /// <summary>WoW started or stopped making sound, or listening stopped. UI thread.</summary>
    public event Action? StateChanged;

    public WowListenState State => _state;
    /// <summary>WoW's sound level right now, 0..1.</summary>
    public float Level => _level;
    /// <summary>The WoW process being heard, or 0. For <see cref="WowRecorder"/>.</summary>
    /// <summary>Records WoW's sound with every detection marked, for tuning.</summary>
    public WowRecorder Recorder { get; } = new();

    public uint WowPid => _sessions.Count > 0 ? _wowPid : 0;

    public static readonly (string Value, string Label, float Db)[] Sensitivities =
        [("low", "Low", 18), ("normal", "Normal", 12), ("high", "High", 8)];

    public static float ThresholdDb(string? sensitivity) =>
        Sensitivities.FirstOrDefault(s => s.Value == sensitivity).Db is > 0 and var db ? db : 12;

    /// <summary>Create on the UI thread.</summary>
    public WowFishing(Dispatcher ui)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>The WoW triggers that are on, with their sensitivities. None stops listening. UI thread.</summary>
    public void SetTriggers(IEnumerable<(string Id, string? Sensitivity)> triggers)
    {
        var old = _listeners;
        _listeners = triggers.Select(t =>
        {
            // Keep a trigger's detector (and its background level) when only something else changed.
            var d = old.FirstOrDefault(o => o.Id == t.Id).Detector ?? new SplashDetector();
            d.ThresholdDb = ThresholdDb(t.Sensitivity);
            return (t.Id, d);
        }).ToArray();

        if (_listeners.Length > 0 && !_timer.IsEnabled)
        {
            _nextScan = 0;
            _timer.Interval = TimeSpan.FromMilliseconds(25);
            _timer.Start();
        }
        else if (_listeners.Length == 0 && _timer.IsEnabled) Stop();
    }

    void Stop()
    {
        _timer.Stop();
        Release(_sessions);
        _level = 0;
        SetState(WowListenState.Stopped);
    }

    void SetState(WowListenState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke();
    }

    void Tick()
    {
        long now = _clock.ElapsedMilliseconds;
        if (now >= _nextScan)
        {
            Release(_sessions);
            try { Scan(_sessions, _isWow, ref _wowPid); }
            catch (Exception ex) { SwitchLog.Write($"WoW fishing: couldn't look for WoW's sound: {ex.Message}"); }
            // WoW can move to another sound device when you change it in its settings, so keep looking.
            _nextScan = now + (_sessions.Count == 0 ? 3000 : 5000);
            if (_sessions.Count == 0 && _state != WowListenState.NoGame) foreach (var l in _listeners) l.Detector.Reset();
            var state = _sessions.Count == 0 ? WowListenState.NoGame : WowListenState.Listening;
            if (state != _state) SwitchLog.Write(state == WowListenState.Listening ? "WoW fishing: hearing WoW" : "WoW fishing: WoW isn't making sound");
            SetState(state);
            // No WoW: nothing to read until the next look, so tick slowly instead of every 25 ms.
            _timer.Interval = TimeSpan.FromMilliseconds(_sessions.Count == 0 ? 500 : 25);
        }
        if (_sessions.Count == 0) { _level = 0; return; }

        float peak = 0;
        foreach (var meter in _sessions)
            if (meter.GetPeakValue(out var p) == 0 && p > peak) peak = p;

        _level = peak;
        foreach (var (id, detector) in _listeners)
        {
            if (!detector.Feed(peak, now)) continue;
            string heard = $"splash at {20 * MathF.Log10(Math.Max(peak, 1e-5f)):0} dB, background {detector.BaselineDb:0} dB";
            SwitchLog.Write($"WoW fishing: {heard}");
            Recorder.Mark($"detected: {heard}");
            Bite?.Invoke(id);
        }
    }

    /// <summary>
    /// Every WoW audio session on every active output device, as peak meters. The enumerator, device list and devices
    /// can be the very wrappers AudioService holds, so they're left to the GC and never released here.
    /// </summary>
    static void Scan(List<IAudioMeterInformation> found, Dictionary<uint, bool> isWow, ref uint wowPid)
    {
        if (isWow.Count > 64) isWow.Clear();
        // By CLSID, cast only to the interface: the enumerator is one object per process, and when AudioService already
        // made it, .NET hands back AudioService's wrapper, which a coclass of our own can't be cast to (beta.2's bug).
        var devices = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!)!;
        if (devices.EnumAudioEndpoints(ERender, StateActive, out var collection) != 0) return;
        collection.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out var device) != 0) continue;
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
                            if (keep && session is IAudioMeterInformation meter) { found.Add(meter); wowPid = pid; }
                            else keep = false;
                        }
                        if (!keep) Marshal.ReleaseComObject(session);
                    }
                }
                finally { Marshal.ReleaseComObject(list); }
            }
            finally { Marshal.ReleaseComObject(manager); }
        }
    }

    /// <summary>
    /// Wow.exe (retail and Forever), WowClassic.exe, WowB/WowT (beta, PTR) and so on. Not WowVoiceProxy: that's voice chat.
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
        Recorder.Dispose();
        if (_timer.IsEnabled) Stop();
    }

    // ---------------- interop ----------------

    const int ERender = 0;
    const uint StateActive = 1, ClsctxAll = 23;

    static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

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
