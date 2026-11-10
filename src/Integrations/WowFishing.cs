using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using IOHub.Profiles;

namespace IOHub.Integrations;

public enum WowListenState { Stopped, NoGame, Listening }

/// <summary>
/// The "WoW fishing bite" trigger's ears. Listens to World of Warcraft's own sound (music or voice chat from other apps
/// don't count) and spots the bite splash. WoW gives addons no event for the bite, which is why this goes by sound.
///
/// Two parts, both only while a WoW trigger is on:
/// - On the UI thread, like <see cref="Devices.AudioService"/>'s meters: finds WoW's audio sessions (every 3 seconds
///   while it isn't running) and reads their peak level every 25 ms for the page's meter. The UI thread because Windows
///   hands out the same sound device objects AudioService holds there, and .NET reuses its wrappers for them.
/// - On its own thread: captures WoW's sound (<see cref="ProcessLoopback"/>) and matches it against the splash itself
///   (<see cref="SplashMatcher"/>), with each trigger's sensitivity deciding how close is close enough.
/// If Windows won't capture (Windows 10), each trigger falls back to a <see cref="SplashDetector"/> on the peak level,
/// which only knows "loud after quiet".
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
    Ears? _ears;
    uint _earsFailedPid;
    volatile Rule[] _rules = [];
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

    /// <summary>
    /// How close to a splash the sound must be, per sensitivity. Tested on WoW's own sound files: the splashes match at
    /// 0.98 or better and within 4.5 dB of tone, even quiet, muffled or over noise; the cast, the reel-in and a noise
    /// swell are 6.8 dB off or more.
    /// </summary>
    public static (float MinCorr, float MaxShapeDb, float MinRiseDb) MatchRule(string? sensitivity) => sensitivity switch
    {
        "low" => (0.90f, 4.5f, 8),
        "high" => (0.80f, 6.5f, 3),
        _ => (0.85f, 5.5f, 5),
    };

    /// <summary>One splash plays for about 3 seconds; one bite per splash.</summary>
    const int MatchCooldownMs = 3000;

    /// <summary>True while WoW's sound is being matched against the splash, false when it's going by level alone.</summary>
    public bool MatchingSound => _ears is { Running: true };

    /// <summary>Create on the UI thread.</summary>
    public WowFishing(Dispatcher ui)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>The WoW triggers that are on, with their sensitivities. None stops listening. UI thread.</summary>
    public void SetTriggers(IEnumerable<(string Id, string? Sensitivity)> triggers)
    {
        var list = triggers.ToList();
        var old = _listeners;
        _listeners = list.Select(t =>
        {
            // Keep a trigger's detector (and its background level) when only something else changed.
            var d = old.FirstOrDefault(o => o.Id == t.Id).Detector ?? new SplashDetector();
            d.ThresholdDb = ThresholdDb(t.Sensitivity);
            return (t.Id, d);
        }).ToArray();

        var oldRules = _rules;
        _rules = list.Select(t =>
        {
            var (corr, shape, rise) = MatchRule(t.Sensitivity);
            return new Rule(t.Id, corr, shape, rise) { LastHit = oldRules.FirstOrDefault(r => r.Id == t.Id)?.LastHit ?? long.MinValue / 2 };
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
        StopEars();
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
        if (_sessions.Count == 0) { _level = 0; StopEars(); return; }
        if (_ears is { Failed: true } failed)
        {
            _earsFailedPid = failed.Pid;
            StopEars();
            foreach (var l in _listeners) l.Detector.Reset();
        }
        if (_ears?.Pid != _wowPid && _wowPid != _earsFailedPid) StartEars(_wowPid);

        float peak = 0;
        foreach (var meter in _sessions)
            if (meter.GetPeakValue(out var p) == 0 && p > peak) peak = p;

        _level = peak;
        if (_ears != null) return;   // matching the sound itself; the level is only for the meter
        foreach (var (id, detector) in _listeners)
        {
            if (!detector.Feed(peak, now)) continue;
            string heard = $"splash at {20 * MathF.Log10(Math.Max(peak, 1e-5f)):0} dB, background {detector.BaselineDb:0} dB";
            SwitchLog.Write($"WoW fishing: {heard}");
            Recorder.Mark($"detected: {heard}");
            Bite?.Invoke(id);
        }
    }

    // ---------------- matching the splash ----------------

    void StartEars(uint pid)
    {
        StopEars();
        var ears = new Ears(pid);
        var ui = _timer.Dispatcher;
        var matcher = new SplashMatcher();
        long nextQpc = 0, lastNear = 0;

        void OnFrame(SplashScore? scored)
        {
            if (scored is not { } score) return;
            long now = Environment.TickCount64;
            bool any = false;
            foreach (var rule in _rules)
            {
                if (score.Corr < rule.MinCorr || score.ShapeDb > rule.MaxShapeDb || score.RiseDb < rule.MinRiseDb) continue;
                any = true;
                if (now - rule.LastHit < MatchCooldownMs) continue;
                rule.LastHit = now;
                SwitchLog.Write($"WoW fishing: splash heard ({score})");
                Recorder.Mark($"detected: {score}");
                var id = rule.Id;
                ui.BeginInvoke(() => Bite?.Invoke(id));
            }
            // Close calls go in a recording too, at most one a second, to see where the line should be.
            if (!any && score.Corr >= 0.6f && score.ShapeDb <= 9 && score.RiseDb >= 2 && now - lastNear >= 1000)
            {
                lastNear = now;
                Recorder.Mark($"near: {score}");
            }
        }

        ears.Thread = new Thread(() =>
        {
            try
            {
                ProcessLoopback.Run(pid, () => ears.Stopping, (stereo, frames, _, qpc) =>
                {
                    // No packets while WoW plays nothing: that's quiet, which the match needs to see before a splash.
                    if (nextQpc != 0 && qpc - nextQpc > 200_000)
                        matcher.FeedSilence((int)Math.Min((qpc - nextQpc) * SplashMatcher.Rate / 10_000_000, SplashMatcher.Rate), OnFrame);
                    nextQpc = qpc + frames * 10_000_000L / SplashMatcher.Rate;
                    matcher.Feed(stereo, OnFrame);
                }, started: () =>
                {
                    ears.Running = true;
                    SwitchLog.Write("WoW fishing: matching WoW's sound against the splash");
                    ui.BeginInvoke(() => StateChanged?.Invoke());
                });
            }
            catch (Exception ex)
            {
                SwitchLog.Write($"WoW fishing: can't capture WoW's sound, going by its level instead: {ex.Message}");
                ears.Failed = true;
            }
            finally
            {
                ears.Running = false;
                ui.BeginInvoke(() => StateChanged?.Invoke());
            }
        }) { IsBackground = true, Name = "WoW splash matcher", Priority = ThreadPriority.AboveNormal };
        ears.Thread.SetApartmentState(ApartmentState.MTA);
        _ears = ears;
        ears.Thread.Start();
    }

    void StopEars()
    {
        if (_ears is not { } ears) return;
        _ears = null;
        ears.Stopping = true;
        ears.Thread?.Join(500);
    }

    sealed class Ears(uint pid)
    {
        public readonly uint Pid = pid;
        public Thread? Thread;
        public volatile bool Stopping, Running, Failed;
    }

    /// <summary>A trigger's sensitivity as match limits. LastHit is only touched on the matcher's thread.</summary>
    sealed record Rule(string Id, float MinCorr, float MaxShapeDb, float MinRiseDb)
    {
        public long LastHit { get; set; }
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
