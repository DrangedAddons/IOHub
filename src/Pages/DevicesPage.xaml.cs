using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IOHub.Devices;

namespace IOHub.Pages;

public partial class DevicesPage : UserControl
{
    readonly DeviceHub _hub;
    readonly AppSettings _settings;
    readonly DispatcherTimer _meterTimer;

    public DevicesPage(DeviceHub hub, AppSettings settings, FeatureService features, Profiles.ProfileService profiles)
    {
        InitializeComponent();
        _hub = hub;
        _settings = settings;
        InitFeatures(features, profiles);
        ShowDisabledSwitch.IsChecked = settings.ShowDisabledDevices;
        _hub.Refreshing += SpinRefreshGlyph;
        _hub.Refreshed += Render;

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _meterTimer.Tick += (_, _) => { foreach (var e in _hub.Audio) e.UpdateLevel(); };
        IsVisibleChanged += (_, _) =>
        {
            UpdateMeterTimer();
            if (IsVisible && _stale) Render();
            // The same setting is switched from the Profiles page too.
            if (IsVisible && ShowDisabledSwitch.IsChecked != _settings.ShowDisabledDevices)
                ShowDisabledSwitch.IsChecked = _settings.ShowDisabledDevices;
        };
        Render();
    }

    void ShowDisabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return; // fires during InitializeComponent
        _settings.ShowDisabledDevices = ShowDisabledSwitch.IsChecked == true;
        _settings.Save();
        Render();
    }

    bool ShowDisabled => _settings.ShowDisabledDevices;



    bool _stale;

    void Render()
    {
        // Hidden pages (including everything while the app sits in the tray) don't rebuild; they catch up when shown.
        if (!IsVisible) { _stale = true; return; }
        _stale = false;

        _featureList = _features?.All() ?? [];
        ShowDisplays();
        ShowTvFeatures();
        ShowAudio();
        ShowInput();
        ShowCameras();
    }

    void ShowCameras()
    {
        ShowError(CameraError, _hub.CameraError, "Couldn't read cameras");
        var cams = _hub.Cameras;
        var shown = cams.Where(c => ShowDisabled || c.IsEnabled).ToList();
        CameraList.ItemsSource = shown;
        CameraEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int enabled = cams.Count(c => c.IsEnabled), disabled = cams.Count - enabled;
        CameraCount.Text = enabled.ToString();
        CameraSummary.Text = disabled > 0 ? $"{enabled} ready · {disabled} disabled" : $"{enabled} ready";
    }

    void ShowDisplays()
    {
        ShowError(VideoError, _hub.DisplayError, "Couldn't read displays");
        var displays = _hub.Displays;
        // A fresh list each time: given the same one again, the list wouldn't redraw (a device's functions opened or closed).
        DisplayList.ItemsSource = displays.ToList();
        LayoutMap.SetDisplays(displays);
        int active = displays.Count(d => d.IsActive), standby = displays.Count - active;
        VideoCount.Text = displays.Count.ToString();
        DisplaysSummary.Text = standby > 0 ? $"{active} active · {standby} on standby" : $"{active} active";
    }

    void ShowAudio()
    {
        ShowError(AudioError, _hub.AudioError, "Couldn't read audio devices");
        // Counts cover everything; the lists leave disabled devices out unless asked.
        var allOutputs = _hub.Audio.Where(e => e.Flow == AudioFlow.Output).ToList();
        var allInputs = _hub.Audio.Where(e => e.Flow == AudioFlow.Input).ToList();
        var outputs = allOutputs.Where(e => ShowDisabled || !e.IsDisabled).ToList();
        var inputs = allInputs.Where(e => ShowDisabled || !e.IsDisabled).ToList();
        OutputList.ItemsSource = outputs;
        InputAudioList.ItemsSource = inputs;
        OutputEmpty.Visibility = outputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InputAudioEmpty.Visibility = inputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutputCount.Text = CountText(allOutputs);
        InputAudioCount.Text = CountText(allInputs);
        AudioCount.Text = _hub.Audio.Count(e => e.IsActive).ToString();
    }

    // The tab counts active endpoints, so the section headers lead with the same number.
    static string CountText(List<AudioEndpoint> list)
    {
        int active = list.Count(e => e.IsActive), off = list.Count - active;
        return off > 0 ? $"{active} active · {off} off" : $"{active} active";
    }

    void ShowInput()
    {
        ShowError(InputError, _hub.InputError, "Couldn't read input devices");
        var snap = _hub.Input;
        ControllerList.ItemsSource = snap.Controllers.ToList(); // fresh, so it redraws (see ShowDisplays)
        KeyboardList.ItemsSource = snap.Keyboards;
        MouseList.ItemsSource = snap.Mice;
        ControllerEmpty.Visibility = snap.Controllers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowControllersOff();
        KeyboardEmpty.Visibility = snap.Keyboards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MouseEmpty.Visibility = snap.Mice.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ControllerCount.Text = snap.Controllers.Count.ToString();
        KeyboardCount.Text = snap.Keyboards.Count.ToString();
        MouseCount.Text = snap.Mice.Count.ToString();
        InputCount.Text = (snap.Controllers.Count + snap.Keyboards.Count + snap.Mice.Count).ToString();
    }

    static void ShowError(TextBlock target, string? error, string prefix)
    {
        target.Text = error == null ? "" : $"{prefix}: {error}";
        target.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void SpinRefreshGlyph()
    {
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        RefreshGlyph.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _hub.Refresh();

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (VideoPanel == null) return; // fires during InitializeComponent
        VideoPanel.Visibility = VideoTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = AudioTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InputPanel.Visibility = InputTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CameraPanel.Visibility = CameraTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateMeterTimer();
    }

    // Meters only cost anything while someone's looking at them.
    void UpdateMeterTimer()
    {
        if (IsVisible && AudioTab.IsChecked == true) _meterTimer.Start();
        else _meterTimer.Stop();
    }
}
