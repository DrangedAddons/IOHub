using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IOHub.Devices;
using IOHub.Triggers;
using Trigger = IOHub.Triggers.Trigger;

namespace IOHub.Pages;

/// <summary>
/// The changeable features of devices, shown under them: mouse mode under the controllers, power and input under
/// paired LG TVs, mute and default in each sound device's menu. Each shows its triggers and can start a new one.
/// </summary>
public partial class DevicesPage
{
    FeatureService? _features;
    Profiles.ProfileService? _profiles;

    /// <summary>Make a trigger that changes this feature (the main window opens it on the Triggers page).</summary>
    public event Action<DeviceFeature, FeatureAction, string?>? AddTrigger;
    /// <summary>Show this trigger on the Triggers page.</summary>
    public event Action<string>? OpenTrigger;

    void InitFeatures(FeatureService features, Profiles.ProfileService profiles)
    {
        _features = features;
        _profiles = profiles;
        // Turning mouse mode on or off (from a trigger, say) or it pausing for a fullscreen app.
        features.Mouse.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) ShowControllerFeatures(); else _stale = true; });
        profiles.Changed += () => { if (IsVisible) { ShowControllerFeatures(); ShowTvFeatures(); } else _stale = true; };
    }

    T Res<T>(string key) => (T)FindResource(key);

    List<Trigger> TriggersFor(DeviceFeature f) =>
        _profiles?.Data.Triggers.Where(t => t.ChangesDevice && t.DeviceId == f.DeviceId && t.Feature == f.Id).ToList() ?? [];

    // ---------------- controllers: mouse mode ----------------

    void ShowControllerFeatures()
    {
        ControllerFeatures.Children.Clear();
        if (_features == null) return;
        var mouse = _features.Mouse;
        var feature = _features.All().First(f => f.DeviceId == FeatureService.ControllersId && f.Id == "mouse");

        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Style = Res<Style>("IconTile"), Background = Res<Brush>("AccentSoftBrush"), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x7B, 0x8C, 0xFF)),
            Child = new TextBlock { Text = "", Style = Res<Style>("Icon"), FontSize = 18, Foreground = Res<Brush>("AccentBrush") },
        });
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock { Text = "Mouse mode", Style = Res<Style>("ItemTitle"), FontSize = 15 });
        string state = !mouse.IsOn ? "Off" : mouse.IsPaused ? "Paused: fullscreen app in front" : "On";
        titleRow.Children.Add(new Border
        {
            Style = Res<Style>(mouse.IsOn && !mouse.IsPaused ? "BadgeSuccess" : "Badge"), Margin = new Thickness(10, 1, 0, 0),
            Child = new TextBlock { Text = state },
        });
        titles.Children.Add(titleRow);
        titles.Children.Add(new TextBlock { Text = "Use an Xbox controller as the mouse, for the desktop on a TV.", Style = Res<Style>("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        head.Children.Add(titles);
        stack.Children.Add(head);

        var allow = new CheckBox
        {
            Style = Res<Style>("Switch"), IsChecked = _settings.ControllerMouse, Margin = new Thickness(0, 16, 0, 0),
            Content = new TextBlock { Text = "Let IO Hub control the mouse with Xbox controllers", FontWeight = FontWeights.SemiBold },
        };
        allow.Click += (_, _) =>
        {
            _settings.ControllerMouse = allow.IsChecked == true;
            _settings.Save();
            if (!_settings.ControllerMouse) mouse.Set(false);
            ShowControllerFeatures();
        };
        stack.Children.Add(allow);

        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0), IsEnabled = _settings.ControllerMouse, Opacity = _settings.ControllerMouse ? 1 : 0.45 };
        var actions = new WrapPanel();
        var now = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 10, 8),
            Content = mouse.IsOn ? "Turn it off now" : "Turn it on now",
        };
        now.Click += (_, _) => { mouse.Set(!mouse.IsOn); ShowControllerFeatures(); };
        actions.Children.Add(now);
        body.Children.Add(actions);
        body.Children.Add(TriggerLine(feature));

        // Speed.
        var speed = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        speed.Children.Add(new TextBlock { Text = "Pointer speed", Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        foreach (var (px, label) in new[] { (800, "Slow"), (1400, "Normal"), (2200, "Fast"), (3200, "Very fast") })
        {
            var pill = new ToggleButton { Style = Res<Style>("PillToggle"), Content = label, IsChecked = _settings.MouseSpeed == px, Background = Res<Brush>("AccentBrush"), Margin = new Thickness(0, 0, 6, 0) };
            pill.Click += (_, _) => { _settings.MouseSpeed = px; _settings.Save(); ShowControllerFeatures(); };
            speed.Children.Add(pill);
        }
        body.Children.Add(speed);

        var pause = new CheckBox
        {
            Style = Res<Style>("Switch"), IsChecked = _settings.MousePauseInFullscreen, Margin = new Thickness(0, 12, 0, 0),
            Content = new TextBlock { Text = "Pause while a game or other fullscreen app is in front" },
            ToolTip = "Games still get the controller too, so the pointer and clicks would land in them as well.",
        };
        pause.Click += (_, _) => { _settings.MousePauseInFullscreen = pause.IsChecked == true; _settings.Save(); };
        body.Children.Add(pause);

        body.Children.Add(new TextBlock
        {
            Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 14, 0, 0),
            Text = "Left stick moves the pointer (hold LT for slow and precise). Right stick scrolls. A, B and X are left, right and middle click. "
                 + "LB and RB are back and forward. The D-pad is the arrow keys, Menu is Enter, View is Esc, and Y opens the on-screen keyboard.",
        });
        body.Children.Add(new TextBlock
        {
            Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 8, 0, 0),
            Foreground = Res<Brush>("WarnBrush"),
            Text = "Steam has its own controller mouse. Turn it off so the two don't fight over the pointer: Steam → Settings → Controller → "
                 + "Guide Button Chord Layout and Desktop Layout. It can't click in windows running as administrator (Task Manager, installers).",
        });
        stack.Children.Add(body);
        ControllerFeatures.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(18), Child = stack });
    }

    // ---------------- LG TVs ----------------

    void ShowTvFeatures()
    {
        TvFeatures.Children.Clear();
        if (_features == null) return;
        var tvs = _features.All().Where(f => f.Section == "TVs").GroupBy(f => f.DeviceId).ToList();
        if (tvs.Count == 0) return;

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 28, 0, 14) };
        title.Children.Add(new TextBlock { Text = "TV controls", Style = Res<Style>("SectionTitle") });
        title.Children.Add(new TextBlock { Text = "LG, paired", Style = Res<Style>("CountPill"), Margin = new Thickness(10, 1, 0, 0) });
        TvFeatures.Children.Add(title);
        foreach (var tv in tvs)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = tv.First().DeviceName, Style = Res<Style>("ItemTitle"), FontSize = 15, Margin = new Thickness(0, 0, 0, 4) });
            foreach (var f in tv)
            {
                var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var line = new WrapPanel();
                line.Children.Add(new TextBlock { Text = f.Title, FontWeight = FontWeights.SemiBold, Width = 70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
                foreach (var (action, value, label) in f.Actions.Where(a => a.Action != FeatureAction.Toggle))
                {
                    var b = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), Content = label };
                    b.Click += async (_, _) =>
                    {
                        b.IsEnabled = false;
                        var error = await f.Run(action, value);
                        b.IsEnabled = true;
                        if (error != null) Ui.DarkDialog.Show(Window.GetWindow(this), $"Couldn't change {f.DeviceName}", error);
                    };
                    line.Children.Add(b);
                }
                row.Children.Add(line);
                row.Children.Add(TriggerLine(f));
                stack.Children.Add(row);
            }
            TvFeatures.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 12), Child = stack });
        }
    }

    // ---------------- sound devices ----------------

    void AudioMore_Click(object sender, RoutedEventArgs e)
    {
        if (_features == null || sender is not Button { Tag: string id } button) return;
        var features = _features.All().Where(f => f.DeviceId == id).ToList();
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var f in features)
        {
            string label = f.Kind == FeatureKind.Toggle ? (f.State() == true ? f.OffLabel : f.OnLabel) : f.Title;
            var action = f.Kind == FeatureKind.Toggle ? (f.State() == true ? FeatureAction.Off : FeatureAction.On) : FeatureAction.Run;
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                var error = await f.Run(action, null);
                if (error != null) Ui.DarkDialog.Show(Window.GetWindow(this), $"Couldn't change {f.DeviceName}", error);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var f in features)
            foreach (var t in TriggersFor(f))
            {
                var item = new MenuItem { Header = $"{KeyNames.Describe(t)} → {_features.Describe(t, withDevice: false)}" };
                item.Click += (_, _) => OpenTrigger?.Invoke(t.Id);
                menu.Items.Add(item);
            }
        var add = new MenuItem { Header = "Add a trigger" };
        foreach (var f in features)
        {
            var (action, value, _) = f.Actions.First();
            var item = new MenuItem { Header = f.Kind == FeatureKind.Toggle ? $"{f.Title}: toggle" : f.Title };
            item.Click += (_, _) => AddTrigger?.Invoke(f, action, value);
            add.Items.Add(item);
        }
        menu.Items.Add(add);
        menu.IsOpen = true;
    }

    // ---------------- shared ----------------

    /// <summary>The triggers that change this feature (click one to open it), and "Add a trigger".</summary>
    UIElement TriggerLine(DeviceFeature f)
    {
        var line = new WrapPanel();
        foreach (var t in TriggersFor(f))
        {
            var chip = new Button
            {
                Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6),
                ToolTip = "Open on the Triggers page", Opacity = t.Enabled ? 1 : 0.5,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, Foreground = Res<Brush>("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                        new TextBlock { Text = $"{KeyNames.Describe(t)} → {_features!.Describe(t, withDevice: false)}" + (t.Enabled ? "" : " (off)"), FontSize = 12 },
                    },
                },
            };
            chip.Click += (_, _) => OpenTrigger?.Invoke(t.Id);
            line.Children.Add(chip);
        }
        var add = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), Content = "+ Add a trigger" };
        add.Click += (_, _) =>
        {
            var (action, value, _) = f.Actions.First();
            AddTrigger?.Invoke(f, action, value);
        };
        line.Children.Add(add);
        return line;
    }
}
