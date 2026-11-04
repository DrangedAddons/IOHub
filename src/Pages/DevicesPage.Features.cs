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
        profiles.Changed += () => { if (IsVisible) { ShowControllerFeatures(); ShowTvFeatures(); ShowAudioFeatures(); } else _stale = true; };
    }

    T Res<T>(string key) => (T)FindResource(key);

    List<Trigger> TriggersFor(DeviceFeature f) =>
        _profiles?.Data.Triggers.Where(t => t.ChangesDevice && t.DeviceId == f.DeviceId && t.Feature == f.Id).ToList() ?? [];

    // ---------------- controllers: mouse mode ----------------

    void ShowControllerFeatures()
    {
        ControllerFeatures.Children.Clear();
        if (_features == null || !_settings.ShowDeviceFunctions) return;
        var mouse = _features.Mouse;
        var pads = _features.All().Where(f => f.Section == "Controllers").ToList();

        var stack = new StackPanel();
        stack.Children.Add(CardHeading("", "Controller functions", "What IO Hub can do with your Xbox controllers, and the triggers that do it."));
        stack.Children.Add(FeatureTable(pads.GroupBy(f => f.DeviceId), deviceHeadings: false));

        // Mouse mode's own settings.
        var settings = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        settings.Children.Add(new TextBlock { Text = "MOUSE MODE SETTINGS", Style = Res<Style>("Overline") });
        var allow = new CheckBox
        {
            Style = Res<Style>("Switch"), IsChecked = _settings.ControllerMouse, Margin = new Thickness(0, 12, 0, 0),
            Content = new TextBlock { Text = "Let IO Hub control the mouse with Xbox controllers", FontWeight = FontWeights.SemiBold },
            ToolTip = "Off: mouse mode stays off, whatever triggers say.",
        };
        allow.Click += (_, _) =>
        {
            _settings.ControllerMouse = allow.IsChecked == true;
            _settings.Save();
            if (!_settings.ControllerMouse) mouse.Set(false);
            ShowControllerFeatures();
        };
        settings.Children.Add(allow);

        var body = new StackPanel { IsEnabled = _settings.ControllerMouse, Opacity = _settings.ControllerMouse ? 1 : 0.45 };
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
        settings.Children.Add(body);
        stack.Children.Add(settings);
        ControllerFeatures.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 18, 20, 18), Child = stack });
    }

    // ---------------- LG TVs ----------------

    void ShowTvFeatures()
    {
        TvFeatures.Children.Clear();
        if (_features == null || !_settings.ShowDeviceFunctions) return;
        var tvs = _features.All().Where(f => f.Section == "TVs").GroupBy(f => f.DeviceId).ToList();
        if (tvs.Count == 0) return;

        var stack = new StackPanel();
        stack.Children.Add(CardHeading("", "TV functions", "Paired LG TVs, connected or not: a TV that's off isn't detected, and that's when you'd turn it on."));
        stack.Children.Add(FeatureTable(tvs, deviceHeadings: true));
        TvFeatures.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 28, 0, 0), Child = stack });
    }

    // ---------------- sound devices ----------------

    void ShowAudioFeatures()
    {
        AudioFeatures.Children.Clear();
        if (_features == null || !_settings.ShowDeviceFunctions) return;
        var sound = _features.All().Where(f => f.Section is "Sound output" or "Microphones").GroupBy(f => f.DeviceId).ToList();
        if (sound.Count == 0) return;
        var stack = new StackPanel();
        stack.Children.Add(CardHeading("", "Sound functions", "Each sound device that's plugged in and enabled."));
        stack.Children.Add(FeatureTable(sound, deviceHeadings: true, sectionHeadings: true));
        AudioFeatures.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 18, 20, 18), Child = stack });
    }

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

    // ---------------- the function table ----------------

    const double FunctionColumn = 210, NowColumn = 310;

    UIElement CardHeading(string glyph, string title, string subtitle)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new Border
        {
            Style = Res<Style>("IconTile"), Background = Res<Brush>("AccentSoftBrush"), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x7B, 0x8C, 0xFF)),
            Child = new TextBlock { Text = glyph, Style = Res<Style>("Icon"), FontSize = 18, Foreground = Res<Brush>("AccentBrush") },
        });
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, Style = Res<Style>("ItemTitle"), FontSize = 15 });
        titles.Children.Add(new TextBlock { Text = subtitle, Style = Res<Style>("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        head.Children.Add(titles);
        return head;
    }

    /// <summary>
    /// Every function in one column, what you can do with it now in the next, and its triggers on the right; one row
    /// per function, grouped under each device when <paramref name="deviceHeadings"/> is set.
    /// </summary>
    UIElement FeatureTable(IEnumerable<IGrouping<string, DeviceFeature>> devices, bool deviceHeadings, bool sectionHeadings = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FunctionColumn) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NowColumn) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int AddRow() { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); return grid.RowDefinitions.Count - 1; }
        void Put(UIElement e, int row, int col, int span = 1)
        {
            Grid.SetRow(e, row); Grid.SetColumn(e, col); Grid.SetColumnSpan(e, span);
            grid.Children.Add(e);
        }

        int head = AddRow();
        foreach (var (text, col) in new[] { ("FUNCTION", 0), ("DO IT NOW", 1), ("TRIGGERS", 2) })
            Put(new TextBlock { Text = text, Style = Res<Style>("Overline"), Margin = new Thickness(0, 0, 12, 8) }, head, col);
        Put(new Border { Height = 1, Background = Res<Brush>("LineBrush"), VerticalAlignment = VerticalAlignment.Bottom }, head, 0, 3);

        string? section = null;
        foreach (var device in devices)
        {
            if (sectionHeadings && device.First().Section != section)
            {
                section = device.First().Section;
                int s = AddRow();
                Put(new TextBlock { Text = section.ToUpperInvariant(), Style = Res<Style>("Overline"), Margin = new Thickness(0, 20, 0, 0) }, s, 0, 3);
            }
            if (deviceHeadings)
            {
                int r = AddRow();
                Put(new TextBlock { Text = device.First().DeviceName, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = Res<Brush>("AccentBrush"), Margin = new Thickness(0, 14, 0, 4) }, r, 0, 3);
            }
            foreach (var f in device)
            {
                int r = AddRow();
                var cellMargin = new Thickness(0, 10, 12, 4);
                Put(FunctionCell(f, cellMargin), r, 0);
                Put(NowCell(f, cellMargin), r, 1);
                var triggers = TriggerLine(f);
                ((FrameworkElement)triggers).Margin = new Thickness(0, 8, 0, 2);
                Put(triggers, r, 2);
                Put(new Border { Height = 1, Background = Res<Brush>("LineBrush"), Opacity = 0.5, VerticalAlignment = VerticalAlignment.Bottom }, r, 0, 3);
            }
        }
        return grid;
    }

    UIElement FunctionCell(DeviceFeature f, Thickness margin)
    {
        var cell = new StackPanel { Margin = margin };
        cell.Children.Add(new TextBlock { Text = f.Title, FontWeight = FontWeights.SemiBold });
        string detail = f.Kind switch
        {
            FeatureKind.Toggle => f.State() switch { true => f.OnLabel == "Mute" ? "Muted" : "On", false => f.OnLabel == "Mute" ? "Not muted" : "Off", _ => "On or off" },
            FeatureKind.Choice => "Pick one",
            _ => f.State() == true ? "It's the default now" : "One-off action",
        };
        if (f.DeviceId == FeatureService.ControllersId && f.Id == "mouse" && _features!.Mouse.IsPaused) detail = "Paused: fullscreen app in front";
        if (f.DeviceId == FeatureService.ControllersId && f.Id == "mouse" && !_settings.ControllerMouse) detail = "Not allowed yet (see settings below)";
        cell.Children.Add(new TextBlock { Text = detail, Style = Res<Style>("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        return cell;
    }

    /// <summary>Buttons to do it right now: on/off, each choice, or the action.</summary>
    UIElement NowCell(DeviceFeature f, Thickness margin)
    {
        var cell = new WrapPanel { Margin = new Thickness(margin.Left, margin.Top - 3, margin.Right, margin.Bottom) };
        var state = f.State();
        var actions = f.Kind == FeatureKind.Toggle
            // When the state is known, only the change that makes sense; otherwise both.
            ? f.Actions.Where(a => a.Action != FeatureAction.Toggle && (state == null || (a.Action == FeatureAction.On) != state))
            : f.Actions;
        foreach (var (action, value, label) in actions)
        {
            bool done = f.Kind == FeatureKind.Action && state == true;
            var b = new Button
            {
                Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6),
                Content = label, IsEnabled = !done, Opacity = done ? 0.45 : 1,
            };
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                var error = await f.Run(action, value);
                b.IsEnabled = true;
                if (error != null) Ui.DarkDialog.Show(Window.GetWindow(this), $"Couldn't change {f.DeviceName}", error);
                else if (f.Kind == FeatureKind.Toggle) Render();
            };
            cell.Children.Add(b);
        }
        return cell;
    }

    /// <summary>What a trigger does to this function, without repeating its name: "Toggle", "HDMI 2", "On while held".</summary>
    static string ActionLabel(Trigger t, DeviceFeature f) =>
        t.Gesture == GestureKind.WhileHeld ? $"{f.OnLabel} while held"
        : f.Actions.FirstOrDefault(a => a.Action == t.Action && a.Value == t.Value).Label ?? t.Action.ToString();

    /// <summary>The triggers that change this feature (click one to open it), and "Add a trigger".</summary>
    UIElement TriggerLine(DeviceFeature f)
    {
        var line = new WrapPanel();
        foreach (var t in TriggersFor(f))
        {
            var chip = new Button
            {
                Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6),
                ToolTip = "Open on the Triggers page", Opacity = t.Enabled ? 1 : 0.5,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, Foreground = Res<Brush>("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                        new TextBlock { Text = KeyNames.Describe(t), FontSize = 12 },
                        new TextBlock { Text = "  →  " + ActionLabel(t, f) + (t.Enabled ? "" : " (off)"), FontSize = 12, Foreground = Res<Brush>("MutedBrush") },
                    },
                },
            };
            chip.Click += (_, _) => OpenTrigger?.Invoke(t.Id);
            line.Children.Add(chip);
        }
        var add = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6),
            Content = new TextBlock { Text = "+ Add a trigger", FontSize = 12, Foreground = Res<Brush>("MutedBrush") },
        };
        add.Click += (_, _) =>
        {
            var (action, value, _) = f.Actions.First();
            AddTrigger?.Invoke(f, action, value);
        };
        line.Children.Add(add);
        return line;
    }
}
