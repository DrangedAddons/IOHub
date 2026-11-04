using System.Windows.Threading;

namespace IOHub.Devices;

/// <summary>
/// The app's one view of connected devices. Pages read from it and listen for <see cref="Refreshed"/>; the main
/// window feeds it Windows device-change messages through <see cref="ScheduleRefresh"/>. UI thread only.
/// </summary>
public sealed class DeviceHub : IDisposable
{
    readonly Dispatcher _ui;
    readonly AudioService? _audio;
    readonly string? _audioInitError;
    readonly DispatcherTimer _debounce;
    readonly Timer _xinputPoll;
    int _xinputFingerprint;
    bool _refreshing, _refreshAgain;

    public IReadOnlyList<DisplayDevice> Displays { get; private set; } = [];
    public IReadOnlyList<AudioEndpoint> Audio { get; private set; } = [];
    public InputSnapshot Input { get; private set; } = new([], [], []);
    public IReadOnlyList<CameraDevice> Cameras { get; private set; } = [];
    public string? DisplayError { get; private set; }
    public string? CameraError { get; private set; }
    public string? AudioError { get; private set; }
    public string? InputError { get; private set; }
    public DateTime? LastUpdated { get; private set; }

    public event Action? Refreshing;
    public event Action? Refreshed;

    public DeviceHub(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            _audio = new AudioService();
            _audio.Changed += () => _ui.BeginInvoke(ScheduleRefresh);
        }
        catch (Exception ex) { _audioInitError = ex.Message; }

        // Plug events arrive in bursts (one device = several interfaces); wait for them to settle.
        _debounce = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(450) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };

        // XInput has no connect notification, and Bluetooth pads don't always raise a device change.
        _xinputPoll = new Timer(_ =>
        {
            int fp = InputDetector.XInputFingerprint();
            if (Interlocked.Exchange(ref _xinputFingerprint, fp) != fp) _ui.BeginInvoke(ScheduleRefresh);
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5));
    }

    /// <summary>Whether a sound device is muted; null if unknown.</summary>
    public bool? IsMuted(string audioId) => _audio?.IsMuted(audioId);

    /// <summary>Mutes or unmutes a sound device. Returns an error message, or null.</summary>
    public string? SetMute(string audioId, bool mute) => _audio == null ? "Sound devices couldn't be read." : _audio.SetMute(audioId, mute);

    /// <summary>Makes a sound device the Windows default. Returns an error message, or null.</summary>
    public string? MakeDefaultAudio(string audioId)
    {
        try { AudioSwitcher.SetDefault(audioId); ScheduleRefresh(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public void ScheduleRefresh()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public async void Refresh()
    {
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        Refreshing?.Invoke();
        try
        {
            do
            {
                _refreshAgain = false;
                var displays = Task.Run(() => Try(DisplayDetector.Detect));
                var input = Task.Run(() => Try(InputDetector.Detect));
                var cameras = Task.Run(() => Try(CameraDetector.Detect));
                RefreshAudio();
                (var d, DisplayError) = await displays;
                Displays = d ?? [];
                (var i, InputError) = await input;
                Input = i ?? new InputSnapshot([], [], []);
                (var c, CameraError) = await cameras;
                Cameras = c ?? [];
                _xinputFingerprint = InputDetector.XInputFingerprint();
            } while (_refreshAgain);
            LastUpdated = DateTime.Now;
            Refreshed?.Invoke();
        }
        finally { _refreshing = false; }
    }

    void RefreshAudio()
    {
        foreach (var e in Audio) e.Dispose();
        Audio = [];
        AudioError = _audioInitError;
        if (_audio == null) return;
        try { Audio = _audio.Detect(); }
        catch (Exception ex) { AudioError = ex.Message; }
    }

    static (T? value, string? error) Try<T>(Func<T> detect) where T : class
    {
        try { return (detect(), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public void Dispose()
    {
        _xinputPoll.Dispose();
        _debounce.Stop();
        foreach (var e in Audio) e.Dispose();
        _audio?.Dispose();
    }
}
