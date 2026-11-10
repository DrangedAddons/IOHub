using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IOHub.Devices;
using IOHub.Triggers;
using Trigger = IOHub.Triggers.Trigger;

namespace IOHub.Pages;

/// <summary>
/// What can be changed on each device, folded into the device itself: a "2 functions · 1 trigger ⌄" button on the
/// device opens its functions underneath (Function | Do it now | Triggers). Which devices are open is remembered.
/// Paired LG TVs that are off aren't detected, so they get their own short list under the displays.
/// </summary>
public partial class DevicesPage
{
    FeatureService? _features;
    Profiles.ProfileService? _profiles;
    List<DeviceFeature> _featureList = [];

    /// <summary>Make a trigger that changes this feature (the main window opens it on the Triggers page).</summary>
    public event Action<DeviceFeature, FeatureAction, string?>? AddTrigger;
    /// <summary>Show this trigger on the Triggers page.</summary>
    public event Action<string>? OpenTrigger;

    const string MouseSettingsKey = FeatureService.ControllersId + ":settings";

    void InitFeatures(FeatureService features, Profiles.ProfileService profiles)
    {
        _features = features;
        _profiles = profiles;
        // Mouse mode turned on or off (from a trigger, say), or pausing for a fullscreen app; triggers added or changed.
        features.Mouse.Changed += () => Dispatcher.BeginInvoke(Render);
        features.Fishing.Changed += () => Dispatcher.BeginInvoke(Render);
        profiles.Changed += Render;
    }

    T Res<T>(string key) => (T)FindResource(key);

    List<Trigger> TriggersFor(DeviceFeature f) =>
        _profiles?.Data.Triggers.Where(t => t.ChangesDevice && t.DeviceId == f.DeviceId && t.Feature == f.Id).ToList() ?? [];

    /// <summary>The feature device a listed item is: a sound device, a display (an LG TV), or any Xbox controller.</summary>
    static string? FeatureKey(object? item) => item switch
    {
        AudioEndpoint a => a.Id,
        DisplayDevice d => d.Id,
        InputDevice { Kind: InputKind.Controller } c when c.Source.StartsWith("XInput") => FeatureService.ControllersId,
        _ => null,
    };

    List<DeviceFeature> FeaturesOf(string? key) => key == null ? [] : _featureList.Where(f => f.DeviceId == key).ToList();

    bool IsOpen(string key) => _settings.OpenDeviceFunctions.Contains(key);

    void SetOpen(string key, bool open)
    {
        _settings.OpenDeviceFunctions.Remove(key);
        if (open) _settings.OpenDeviceFunctions.Add(key);
        _settings.Save();
        Render();
    }

    // ---------------- in the device templates ----------------

    void FxToggle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentControl cc) return;
        var key = FeatureKey(cc.DataContext);
        var features = FeaturesOf(key);
        cc.Content = features.Count == 0 ? null : FunctionsToggle(key!, features);
    }

    void FxPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentControl cc) return;
        var key = FeatureKey(cc.DataContext);
        var features = FeaturesOf(key);
        cc.Content = features.Count == 0 || !IsOpen(key!) ? null : FunctionsPanel(key!, features, cc.DataContext);
    }

    /// <summary>"2 functions · 1 trigger ⌄": opens or closes the device's functions.</summary>
    Button FunctionsToggle(string key, List<DeviceFeature> features)
    {
        int triggers = features.Sum(f => TriggersFor(f).Count);
        bool open = IsOpen(key);
        var text = new StackPanel { Orientation = Orientation.Horizontal };
        text.Children.Add(new TextBlock { Text = features.Count == 1 ? "1 function" : $"{features.Count} functions", FontSize = 12, FontWeight = FontWeights.SemiBold });
        if (triggers > 0)
            text.Children.Add(new TextBlock { Text = triggers == 1 ? " · 1 trigger" : $" · {triggers} triggers", FontSize = 12, Foreground = Res<Brush>("MutedBrush") });
        text.Children.Add(new TextBlock
        {
            Text = open ? "" : "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 9,
            Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0),
        });
        var button = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 3, 10, 4), Content = text,
            ToolTip = open ? "Hide what can be changed on this device" : "What IO Hub can change on this device, and the triggers that do it",
        };
        button.Click += (_, _) => SetOpen(key, !open);
        return button;
    }

    UIElement FunctionsPanel(string key, List<DeviceFeature> features, object? device)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        stack.Children.Add(new Border { Height = 1, Background = Res<Brush>("LineBrush"), Margin = new Thickness(0, 0, 0, 8) });
        // Display cards are narrow, so their functions stack; sound rows are half width, so their first two columns are tighter.
        stack.Children.Add(device switch
        {
            DisplayDevice => StackedFeatures(features),
            AudioEndpoint => FeatureTable(features, functionWidth: 140, nowWidth: 150),
            _ => FeatureTable(features, functionWidth: 170, nowWidth: 190),
        });
        if (key == FeatureService.ControllersId) stack.Children.Add(MouseSettings());
        return stack;
    }

    // ---------------- mouse mode settings (in the controller's functions) ----------------

    UIElement MouseSettings()
    {
        var mouse = _features!.Mouse;
        bool open = IsOpen(MouseSettingsKey);
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var toggle = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 3, 10, 4), HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                    new TextBlock { Text = "Mouse mode settings", FontSize = 12 },
                    new TextBlock { Text = open ? "" : "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 9, Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) },
                },
            },
        };
        toggle.Click += (_, _) => SetOpen(MouseSettingsKey, !open);
        stack.Children.Add(toggle);
        if (!open) return stack;

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
            Render();
        };
        stack.Children.Add(allow);

        var body = new StackPanel { IsEnabled = _settings.ControllerMouse, Opacity = _settings.ControllerMouse ? 1 : 0.45 };
        var speed = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        speed.Children.Add(new TextBlock { Text = "Pointer speed", Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        foreach (var (px, label) in new[] { (800, "Slow"), (1400, "Normal"), (2200, "Fast"), (3200, "Very fast") })
        {
            var pill = new ToggleButton { Style = Res<Style>("PillToggle"), Content = label, IsChecked = _settings.MouseSpeed == px, Background = Res<Brush>("AccentBrush"), Margin = new Thickness(0, 0, 6, 0) };
            pill.Click += (_, _) => { _settings.MouseSpeed = px; _settings.Save(); Render(); };
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
            Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 12, 0, 0),
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
        return stack;
    }

    // ---------------- LG TVs that are off ----------------

    /// <summary>
    /// Paired LG TVs that Windows doesn't see right now (they're off), so you can still turn one on from here.
    /// TVs that are on show their functions on their own display card.
    /// </summary>
    void ShowTvFeatures()
    {
        TvFeatures.Children.Clear();
        var off = _featureList.Where(f => f.Section == "TVs" && _hub.Displays.All(d => d.Id != f.DeviceId)).GroupBy(f => f.DeviceId).ToList();
        if (off.Count == 0) return;

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 28, 0, 14) };
        title.Children.Add(new TextBlock { Text = "TVs that are off", Style = Res<Style>("SectionTitle") });
        title.Children.Add(new TextBlock { Text = "paired, not detected", Style = Res<Style>("CountPill"), Margin = new Thickness(10, 1, 0, 0) });
        TvFeatures.Children.Add(title);
        foreach (var tv in off)
        {
            var features = tv.ToList();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = new Border { Style = Res<Style>("IconTile"), Child = new TextBlock { Text = "", Style = Res<Style>("Icon"), FontSize = 18, Foreground = Res<Brush>("FaintBrush") } };
            Grid.SetColumn(tile, 0); head.Children.Add(tile);
            var names = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = features[0].DeviceName, Style = Res<Style>("ItemTitle"), FontSize = 15 });
            names.Children.Add(new TextBlock { Text = "Off, or not plugged in", Style = Res<Style>("Muted"), Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetColumn(names, 1); head.Children.Add(names);
            var toggle = FunctionsToggle(tv.Key, features);
            toggle.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(toggle, 2); head.Children.Add(toggle);

            var stack = new StackPanel();
            stack.Children.Add(head);
            if (IsOpen(tv.Key))
            {
                var panel = new StackPanel { Margin = new Thickness(56, 14, 0, 0) };
                panel.Children.Add(new Border { Height = 1, Background = Res<Brush>("LineBrush"), Margin = new Thickness(0, 0, 0, 8) });
                panel.Children.Add(FeatureTable(features, functionWidth: 170, nowWidth: 310));
                stack.Children.Add(panel);
            }
            TvFeatures.Children.Add(new Border { Style = Res<Style>("Card"), Margin = new Thickness(0, 0, 0, 12), Child = stack });
        }
    }

    // ---------------- the functions: table and stacked ----------------

    /// <summary>One row per function: what it is, buttons to do it now, and its triggers on the right.</summary>
    UIElement FeatureTable(List<DeviceFeature> features, double functionWidth, double nowWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(functionWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(nowWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int AddRow() { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); return grid.RowDefinitions.Count - 1; }
        void Put(UIElement e, int row, int col, int span = 1)
        {
            Grid.SetRow(e, row); Grid.SetColumn(e, col); Grid.SetColumnSpan(e, span);
            grid.Children.Add(e);
        }

        int head = AddRow();
        foreach (var (text, col) in new[] { ("FUNCTION", 0), ("DO IT NOW", 1), ("TRIGGERS", 2) })
            Put(new TextBlock { Text = text, Style = Res<Style>("Overline"), Margin = new Thickness(0, 0, 12, 6) }, head, col);
        for (int i = 0; i < features.Count; i++)
        {
            var f = features[i];
            int r = AddRow();
            var margin = new Thickness(0, 8, 12, 2);
            Put(FunctionCell(f, margin), r, 0);
            Put(NowCell(f, margin), r, 1);
            var triggers = (FrameworkElement)TriggerLine(f);
            triggers.Margin = new Thickness(0, 6, 0, 0);
            Put(triggers, r, 2);
            if (i < features.Count - 1)
                Put(new Border { Height = 1, Background = Res<Brush>("LineBrush"), Opacity = 0.5, VerticalAlignment = VerticalAlignment.Bottom }, r, 0, 3);
        }
        return grid;
    }

    /// <summary>For narrow cards: each function's name, then its buttons, then its triggers, one under the other.</summary>
    UIElement StackedFeatures(List<DeviceFeature> features)
    {
        var stack = new StackPanel();
        for (int i = 0; i < features.Count; i++)
        {
            var f = features[i];
            stack.Children.Add(FunctionCell(f, new Thickness(0, i == 0 ? 2 : 12, 0, 6)));
            stack.Children.Add(NowCell(f, new Thickness(0, 3, 0, 0)));
            stack.Children.Add(TriggerLine(f));
        }
        return stack;
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
        if (f.DeviceId == FeatureService.ControllersId && f.Id == "mouse" && !_settings.ControllerMouse) detail = "Not allowed yet (Mouse mode settings)";
        cell.Children.Add(new TextBlock { Text = detail, Style = Res<Style>("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        return cell;
    }

    /// <summary>Buttons to do it right now: on/off, each choice, or the action.</summary>
    UIElement NowCell(DeviceFeature f, Thickness margin)
    {
        var cell = new WrapPanel { Margin = margin };
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
                else if (f.Kind != FeatureKind.Choice) Render();
            };
            cell.Children.Add(b);
        }
        return cell;
    }

    /// <summary>What a trigger does to this function, without repeating its name: "Toggle", "HDMI 2", "Toggle while held".</summary>
    static string ActionLabel(Trigger t, DeviceFeature f)
    {
        var label = f.Actions.FirstOrDefault(a => a.Action == t.Action && a.Value == t.Value).Label ?? t.Action.ToString();
        return t.Gesture == GestureKind.WhileHeld ? $"{label} while held" : label;
    }

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
