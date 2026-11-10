using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using IOHub.Devices;
using IOHub.Integrations;
using IOHub.Pages;
using IOHub.Profiles;
using IOHub.Triggers;
using IOHub.Ui;
using IOHub.Updates;

namespace IOHub;

public partial class MainWindow : Window
{
    readonly DeviceHub _hub;
    readonly ProfileService _profiles = new();
    readonly AppSettings _settings = AppSettings.Load();
    readonly OverviewPage _overviewPage;
    readonly DevicesPage _devicesPage;
    readonly ProfilesPage _profilesPage;
    readonly TriggersPage _triggersPage;
    readonly SettingsPage _settingsPage;
    readonly ProfileSwitcher _switcher;
    readonly ControllerMouse _mouse;
    readonly WowFishing _fishing;
    readonly FeatureService _features;
    readonly TriggerEngine _engine;
    readonly UpdateChecker _updates;
    readonly Toast _toast = new();
    readonly TrayIcon _tray;
    bool _quitting, _toldAboutTray;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppNameText.Text = AppInfo.Name;
        // Never narrower than the Triggers page's rows need (sidebar + page), so WHEN, THEN and OPTIONS stay side by side.
        MinWidth = Math.Max(MinWidth, 236 + TriggersPage.MinPageWidth + 16);
        if (Width < MinWidth) Width = MinWidth;

        _hub = new DeviceHub(Dispatcher);
        _hub.Refreshed += () => UpdatedText.Text = $"Updated {_hub.LastUpdated:T}";
        // Needs the first device scan, so it runs on the first refresh rather than here.
        _hub.Refreshed += () => _profiles.SeedCurrentSetup(_hub);

        _switcher = new ProfileSwitcher(_hub, _profiles)
        {
            ConfirmDisplays = p => _settings.ConfirmNewDisplays ? DisplayConfirmWindow.Ask(p.Name) : Task.FromResult(true),
        };
        _switcher.Notice += (title, detail, color) => { if (_settings.ShowNotices) _toast.Show(title, detail, color: color); };
        _hub.Refreshed += _switcher.Observe;
        _mouse = new ControllerMouse(_settings);
        _fishing = new WowFishing(Dispatcher);
        _features = new FeatureService(_hub, _profiles, _switcher, _mouse);
        _engine = new TriggerEngine(_profiles, _switcher, _features, _fishing, _toast, Dispatcher, () => ProfileMatcher.Active(_profiles, _hub));

        _overviewPage = new OverviewPage(_hub, _profiles, _settings);
        _overviewPage.OpenProfiles += () => ProfilesNav.IsChecked = true;
        _overviewPage.SwitchRequested += p => _ = _switcher.SwitchTo(p);
        _devicesPage = new DevicesPage(_hub, _settings, _features, _profiles);
        _profilesPage = new ProfilesPage(_hub, _profiles, _settings) { TvWanted = _switcher.StopTurnOff };
        _triggersPage = new TriggersPage(_profiles, _engine, _features, _settings);
        // Devices page → Triggers page: a new trigger for a device feature, or an existing one.
        _devicesPage.AddTrigger += (f, action, value) => { TriggersNav.IsChecked = true; _triggersPage.AddForFeature(f, action, value); };
        _devicesPage.OpenTrigger += id => { TriggersNav.IsChecked = true; _triggersPage.Open(id); };
        _updates = new UpdateChecker(_settings, Dispatcher);
        _updates.Found += OnUpdateFound;
        _settingsPage = new SettingsPage(_settings, _profiles, _engine, _hub, _updates, startInTray => _ = InstallUpdate(startInTray), Quit);
        PageHost.Content = _overviewPage;

        _tray = new TrayIcon(
            open: ShowFromTray,
            exit: Quit,
            checkForUpdates: () => { OpenSettings(); _ = _updates.CheckAsync(); },
            profiles: () => _profiles.Profiles.Select(p =>
                (p.Name, ProfileMatcher.Active(_profiles, _hub) == p, (Action)(() => _ = _switcher.SwitchTo(p)))),
            paused: () => _engine.Paused,
            setPaused: v => _engine.Paused = v);

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => ApplyWindowState();
        Loaded += (_, _) => Start();
        Closing += OnClosing;
    }

    bool _started;

    /// <summary>First device scan and update checks; runs once, whether the window opens or starts in the tray.</summary>
    void Start()
    {
        if (_started) return;
        _started = true;
        _hub.Refresh();
        _updates.Start();
        // Installed copies keep their start-with-Windows entry pointing at wherever the exe now is.
        if (StartupEntry.IsEnabled && AppInstaller.IsRunningInstalled && AppUpdater.ExePath is { } exe)
            StartupEntry.Set(true, exe, _settings.StartInTray);
    }

    /// <summary>Started with Windows or after an automatic update: run without showing the window.</summary>
    public void StartInTray()
    {
        // The window needs a handle for device-change messages even while it's never been shown.
        new WindowInteropHelper(this).EnsureHandle();
        Start();
    }

    void OpenSettings()
    {
        ShowFromTray();
        SettingsNav.IsChecked = true;
    }

    // ---- updates ----

    void OnUpdateFound(AppRelease release)
    {
        // While the window is closed and automatic installs are on, update quietly and come back in the tray.
        if (!IsVisible && _settings.InstallUpdatesAutomatically)
        {
            _ = InstallUpdate(startInTray: true);
            return;
        }
        _tray.Notify($"{AppInfo.Name} {release.Version} is ready", "Click to open Settings and install it.", OpenSettings);
    }

    async Task InstallUpdate(bool startInTray)
    {
        if (await _updates.InstallAsync(startInTray)) Quit();
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost == null) return; // fires during InitializeComponent
        PageHost.Content = sender == ProfilesNav ? _profilesPage
            : sender == DevicesNav ? _devicesPage
            : sender == TriggersNav ? _triggersPage
            : sender == SettingsNav ? _settingsPage
            : _overviewPage;
    }

    // ---- tray ----

    // Closing the window keeps the app (and its triggers) running in the tray, unless that's turned off in Settings.
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        if (App.IsExiting || !_settings.CloseToTray) { Dispatcher.BeginInvoke(Quit); return; }
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _toast.Show($"{AppInfo.Name} is still running", "Triggers keep working. Right-click the tray icon to quit.", 4);
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _engine.Dispose();
        _mouse.Dispose();
        _tray.Dispose();
        _hub.Dispose();
        _toast.Close();
        Close();
        App.ExitApp();
    }

    // ---- window chrome ----

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2, border = 0x00332A26; // #262A33 as COLORREF
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Top-level windows get both without registering: display mode/topology changes and device arrivals/removals.
        if (msg == WmDisplayChange || (msg == WmDeviceChange && wParam == DbtDevNodesChanged))
            _hub.ScheduleRefresh();
        return IntPtr.Zero;
    }

    void ApplyWindowState()
    {
        // A maximized custom-chrome window hangs its resize border off-screen; pad it back in.
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            uint ppi = (uint)Math.Round(dpi.PixelsPerInchX);
            double frame = (GetSystemMetricsForDpi(SmCxSizeFrame, ppi) + GetSystemMetricsForDpi(SmCxPaddedBorder, ppi)) / dpi.DpiScaleX;
            Root.Margin = new Thickness(frame);
            MaxButton.Content = "";
            MaxButton.ToolTip = "Restore";
        }
        else
        {
            Root.Margin = new Thickness(0);
            MaxButton.Content = "";
            MaxButton.ToolTip = "Maximize";
        }
    }

    const int WmDisplayChange = 0x007E, WmDeviceChange = 0x0219;
    static readonly IntPtr DbtDevNodesChanged = 7;
    const int DwmwaUseImmersiveDarkMode = 20, DwmwaWindowCornerPreference = 33, DwmwaBorderColor = 34;
    const int SmCxSizeFrame = 32, SmCxPaddedBorder = 92;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
