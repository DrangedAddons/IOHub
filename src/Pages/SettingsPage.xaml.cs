using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IOHub.Devices;
using IOHub.Profiles;
using IOHub.Triggers;
using IOHub.Ui;
using IOHub.Updates;
using Microsoft.Win32;

namespace IOHub.Pages;

/// <summary>
/// App settings: startup and tray, switching, updates, your data, and about. Each row is a label, an explanation and
/// a switch or button on the right, like Windows' own Settings. Rebuilt when something it shows changes.
/// </summary>
public partial class SettingsPage : UserControl
{
    readonly AppSettings _settings;
    readonly ProfileService _profiles;
    readonly TriggerEngine _engine;
    readonly DeviceHub _hub;
    readonly UpdateChecker _updates;
    readonly Action<bool> _installUpdate;
    readonly Action _quit;

    /// <param name="installUpdate">Downloads and restarts into the update; the argument says whether to restart in the tray.</param>
    public SettingsPage(AppSettings settings, ProfileService profiles, TriggerEngine engine, DeviceHub hub,
        UpdateChecker updates, Action<bool> installUpdate, Action quit)
    {
        InitializeComponent();
        _settings = settings;
        _profiles = profiles;
        _engine = engine;
        _hub = hub;
        _updates = updates;
        _installUpdate = installUpdate;
        _quit = quit;
        _updates.Changed += () => { if (IsVisible) Rebuild(); };
        _engine.StateChanged += () => { if (IsVisible) Rebuild(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Rebuild(); };
        Rebuild();
    }

    string? ExePath => AppUpdater.ExePath;

    void Rebuild()
    {
        Sections.Children.Clear();
        Sections.Children.Add(StartupSection());
        Sections.Children.Add(SwitchingSection());
        Sections.Children.Add(UpdatesSection());
        Sections.Children.Add(DataSection());
        Sections.Children.Add(AboutSection());
    }

    // ---------------- sections ----------------

    UIElement StartupSection()
    {
        var rows = new List<UIElement>();
        bool canStart = ExePath != null;
        bool startsWithWindows = StartupEntry.IsEnabled;
        rows.Add(Row("Start with Windows",
            canStart ? "Starts when you sign in, so triggers like Steam Big Picture work without opening the app first."
                     : "Only the installed app can start with Windows. This is a development build.",
            Switch(startsWithWindows, v =>
            {
                StartupEntry.Set(v, ExePath!, _settings.StartInTray);
                Rebuild();
            }, enabled: canStart)));
        rows.Add(Row("Start hidden in the tray",
            "When it starts with Windows, it goes straight to the tray instead of opening this window.",
            Switch(_settings.StartInTray, v =>
            {
                _settings.StartInTray = v;
                _settings.Save();
                if (StartupEntry.IsEnabled && ExePath != null) StartupEntry.Set(true, ExePath, v);
            }, enabled: canStart && startsWithWindows)));
        rows.Add(Row("Keep running when the window is closed",
            "Closing the window leaves the app in the tray so triggers keep working. Turn this off and closing the window quits.",
            Switch(_settings.CloseToTray, v => { _settings.CloseToTray = v; _settings.Save(); })));
        return Section("Startup and tray", "", rows);
    }

    UIElement SwitchingSection()
    {
        var rows = new List<UIElement>
        {
            Row("Show a notice when the profile changes",
                "A small card near the bottom of the screen. It never takes focus, so it's safe over games.",
                Switch(_settings.ShowNotices, v => { _settings.ShowNotices = v; _settings.Save(); })),
            Row("Ask to keep new display layouts",
                "The first time a profile changes your displays, a 15-second Keep or Revert prompt undoes it if nobody answers, "
                + "in case the screen went black. Turning this off isn't recommended.",
                Switch(_settings.ConfirmNewDisplays, v => { _settings.ConfirmNewDisplays = v; _settings.Save(); })),
            Row("Pause all triggers",
                "Hotkeys, gestures and Big Picture stop switching profiles until you turn this off. Also in the tray menu.",
                Switch(_engine.Paused, v => _engine.Paused = v)),
        };
        return Section("Switching", "", rows);
    }

    UIElement UpdatesSection()
    {
        var rows = new List<UIElement>();
        if (!AppUpdater.CanSelfUpdate)
        {
            rows.Add(Row($"Version {AppUpdater.CurrentVersion}", "This is a development build, so it can't update itself.", null));
            return Section("Updates", "", rows);
        }

        string status;
        if (_updates.Progress is { } p) status = $"Downloading version {_updates.Available?.Version}… {p:P0}";
        else if (_updates.Busy) status = "Checking…";
        else if (_updates.Error != null) status = _updates.Error;
        else if (_updates.Available is { } r) status = $"Version {r.Version} is ready to install.";
        else if (_updates.LastChecked is { } at) status = $"Up to date. Last checked at {at:t}.";
        else status = "Not checked yet.";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (_updates.Available != null)
        {
            var install = Button("Update and restart", accent: true);
            install.IsEnabled = !_updates.Busy;
            install.Click += (_, _) => _installUpdate(false);
            buttons.Children.Add(install);
            var notes = Button("What's new");
            notes.Margin = new Thickness(8, 0, 0, 0);
            notes.Click += (_, _) => Open(_updates.Available.PageUrl);
            buttons.Children.Add(notes);
        }
        else
        {
            var check = Button("Check now");
            check.IsEnabled = !_updates.Busy;
            check.Click += async (_, _) => await _updates.CheckAsync();
            buttons.Children.Add(check);
        }
        rows.Add(Row($"Version {AppUpdater.CurrentVersion}", status, buttons, warn: _updates.Error != null));
        rows.Add(Row("Check for updates automatically",
            "Looks once shortly after starting and then every six hours. One small request to GitHub each time.",
            Switch(_settings.CheckForUpdates, v => { _settings.CheckForUpdates = v; _settings.Save(); })));
        rows.Add(Row("Install updates automatically",
            "Only while the window is closed, so it never interrupts you. It restarts straight back into the tray.",
            Switch(_settings.InstallUpdatesAutomatically, v => { _settings.InstallUpdatesAutomatically = v; _settings.Save(); },
                enabled: _settings.CheckForUpdates)));
        return Section("Updates", "", rows);
    }

    UIElement DataSection()
    {
        var rows = new List<UIElement>();
        var transfer = new StackPanel { Orientation = Orientation.Horizontal };
        var export = Button("Export…");
        export.Click += (_, _) => Export();
        var import = Button("Import…");
        import.Margin = new Thickness(8, 0, 0, 0);
        import.Click += (_, _) => Import();
        transfer.Children.Add(export);
        transfer.Children.Add(import);
        rows.Add(Row("Profiles and triggers",
            $"{Count(_profiles.Profiles.Count, "profile")} and {Count(_profiles.Data.Triggers.Count, "trigger")}. "
            + "Export to back them up or move them to another PC.",
            transfer));

        var folder = Button("Open folder");
        folder.Click += (_, _) => { Directory.CreateDirectory(AppInfo.DataDir); Open(AppInfo.DataDir); };
        rows.Add(Row("Data folder", AppInfo.DataDir, folder));

        var reset = Button("Reset…", danger: true);
        reset.Click += (_, _) => Reset();
        rows.Add(Row("Start over",
            "Deletes every profile and trigger, then makes a fresh profile from your current setup. Settings on this page stay.",
            reset));
        return Section("Your data", "", rows);
    }

    UIElement AboutSection()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/logo.png")), Width = 44, Height = 44 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        head.Children.Add(logo);
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = AppInfo.Name, FontFamily = Res<FontFamily>("DisplayFont"), FontSize = 17, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = $"Version {AppUpdater.CurrentVersion} · by {AppInfo.Org}"
                + (AppInstaller.IsRunningInstalled ? "" : AppUpdater.CanSelfUpdate ? " · not installed" : " · development build"),
            Style = Res<Style>("Muted"),
        });
        head.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var github = Button("View on GitHub");
        github.Click += (_, _) => Open(AppInfo.RepoUrl);
        buttons.Children.Add(github);
        if (AppInstaller.IsRunningInstalled)
        {
            var uninstall = Button("Uninstall…", danger: true);
            uninstall.Margin = new Thickness(8, 0, 0, 0);
            uninstall.Click += (_, _) => { if (AppInstaller.Uninstall(Window.GetWindow(this))) _quit(); };
            buttons.Children.Add(uninstall);
        }

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        body.Children.Add(head);
        body.Children.Add(buttons);
        return Section("About", "", [body]);
    }

    // ---------------- actions ----------------

    void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export profiles and triggers",
            FileName = $"{AppInfo.Name} profiles.json",
            Filter = "Profiles (*.json)|*.json",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dialog.FileName, ProfileStore.ToJson(_profiles.Data)); }
        catch (Exception ex) { DarkDialog.Show(Window.GetWindow(this), "Couldn't export", ex.Message); }
    }

    void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import profiles and triggers", Filter = "Profiles (*.json)|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ProfileData data;
        try { data = ProfileStore.FromJson(File.ReadAllText(dialog.FileName)); }
        catch (Exception ex)
        {
            DarkDialog.Show(Window.GetWindow(this), "That file couldn't be read", $"It doesn't look like an {AppInfo.Name} export.\n\n{ex.Message}");
            return;
        }
        if (!DarkDialog.Show(Window.GetWindow(this), "Replace your profiles and triggers?",
                $"The file has {Count(data.Profiles.Count, "profile")} and {Count(data.Triggers.Count, "trigger")}. "
                + $"They replace your current {Count(_profiles.Profiles.Count, "profile")} and {Count(_profiles.Data.Triggers.Count, "trigger")}.",
                primary: "Replace", secondary: "Cancel"))
            return;
        data.SeededCurrentSetup = true; // an import counts as a start; don't add a "Current setup" on top
        _profiles.Replace(data);
        Rebuild();
    }

    void Reset()
    {
        if (!DarkDialog.Show(Window.GetWindow(this), "Delete every profile and trigger?",
                "This can't be undone. Export first if you might want them back.",
                primary: "Delete everything", secondary: "Cancel", danger: true))
            return;
        _profiles.Replace(new ProfileData()); // SeededCurrentSetup is false, so the next scan makes "Current setup" again
        _hub.Refresh();
        Rebuild();
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }

    static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // ---------------- pieces ----------------

    UIElement Section(string title, string glyph, IEnumerable<UIElement> rows)
    {
        var stack = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 16, 20, 6) };
        header.Children.Add(new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 15, Foreground = Res<Brush>("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SectionTitle"), Margin = new Thickness(10, 0, 0, 0) });
        stack.Children.Add(header);
        bool first = true;
        foreach (var row in rows)
        {
            if (!first) stack.Children.Add(new Border { Height = 1, Background = Res<Brush>("LineBrush"), Margin = new Thickness(20, 0, 20, 0) });
            stack.Children.Add(row);
            first = false;
        }
        return new Border { Style = Res<Style>("Card"), Padding = new Thickness(0, 0, 0, 4), Margin = new Thickness(0, 0, 12, 16), Child = stack };
    }

    UIElement Row(string title, string hint, UIElement? control, bool warn = false)
    {
        var grid = new Grid { Margin = new Thickness(20, 12, 20, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
        text.Children.Add(new TextBlock
        {
            Text = hint, Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 3, 0, 0), Foreground = Res<Brush>(warn ? "WarnBrush" : "MutedBrush"),
        });
        grid.Children.Add(text);
        if (control != null)
        {
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return grid;
    }

    CheckBox Switch(bool value, Action<bool> set, bool enabled = true)
    {
        var sw = new CheckBox { Style = Res<Style>("Switch"), IsChecked = value, IsEnabled = enabled };
        sw.Click += (_, _) => set(sw.IsChecked == true);
        return sw;
    }

    Button Button(string text, bool accent = false, bool danger = false) => new()
    {
        Content = text, FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 7, 14, 7),
        Style = Res<Style>(danger ? "DangerButton" : accent ? "AccentButton" : "GhostButton"),
    };

    T Res<T>(string key) where T : class => (T)FindResource(key);
}
