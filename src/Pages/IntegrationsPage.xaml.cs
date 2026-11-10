using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IOHub.Integrations;

namespace IOHub.Pages;

/// <summary>
/// One card per game or app, listing its connections: "Fishing bite → rumble" and, greyed out, the ones still to
/// come. While the page is showing it listens to WoW even with the connection off, so the level meter can be used to
/// tune the sensitivity.
/// </summary>
public partial class IntegrationsPage : UserControl
{
    readonly AppSettings _settings;
    readonly WowFishing _fishing;
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    ProgressBar? _meter;
    TextBlock? _status, _gameBadge;
    Border? _gameBadgeBox;
    string _shownStatus = "";
    WowListenState _shownState = (WowListenState)(-1);

    public IntegrationsPage(AppSettings settings, WowFishing fishing)
    {
        InitializeComponent();
        _settings = settings;
        _fishing = fishing;
        _fishing.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Rebuild(); });
        _fishing.Bite += () => Dispatcher.BeginInvoke(UpdateStatus);
        _meterTimer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            _fishing.Watch(IsVisible);
            if (IsVisible) { Rebuild(); _meterTimer.Start(); }
            else _meterTimer.Stop();
        };
        Rebuild();
    }

    void Rebuild()
    {
        Sections.Children.Clear();
        _shownStatus = "";
        _shownState = (WowListenState)(-1);
        Sections.Children.Add(WowSection());
    }

    // ---------------- World of Warcraft ----------------

    UIElement WowSection()
    {
        var rows = new List<UIElement> { FishingRow() };
        foreach (var (title, hint) in Upcoming)
            rows.Add(Row(title, hint, SoonLabel(), dim: true));

        _gameBadge = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        _gameBadgeBox = new Border { Style = Res<Style>("Badge"), Child = _gameBadge, VerticalAlignment = VerticalAlignment.Center };
        return Section("World of Warcraft", "", rows, _gameBadgeBox);
    }

    /// <summary>Connections that need more than WoW's sound (a small companion addon), listed so you can see what's coming.</summary>
    static readonly (string Title, string Hint)[] Upcoming =
    [
        ("Queue pop → rumble", "Dungeon, raid or battleground invite is ready."),
        ("Ready check → rumble", "The group leader started a ready check."),
        ("Low health → rumble", "Your health drops below a level you pick."),
        ("Whisper → rumble", "Someone whispers you."),
    ];

    UIElement FishingRow()
    {
        bool on = _fishing.IsOn;
        var stack = new StackPanel();
        stack.Children.Add(Row("Fishing bite → rumble",
            "When the bobber splashes, every Xbox controller gives two short buzzes. WoW doesn't tell addons about the bite, "
            + "so IO Hub listens for the splash in WoW's own sound (other apps don't count). Also under the controller's functions on the Devices page.",
            Switch(on, v => _fishing.Set(v))));

        var body = new StackPanel { Margin = new Thickness(20, 0, 20, 14) };
        body.Children.Add(Pills("Sensitivity", WowFishing.Sensitivities.Select(s => (s.Value, s.Label)), _settings.WowFishingSensitivity,
            v => { _settings.WowFishingSensitivity = v; _settings.Save(); Rebuild(); },
            "Higher catches quieter splashes, but also more sounds that aren't the bite. Try Normal first."));

        var strength = Pills("Rumble", WowFishing.Strengths.Select(s => (s.Value, s.Label)), _settings.WowRumbleStrength,
            v => { _settings.WowRumbleStrength = v; _settings.Save(); Rebuild(); }, null);
        var test = Button("Test rumble");
        test.Margin = new Thickness(10, 0, 0, 0);
        test.Click += (_, _) => _fishing.TestRumble();
        ((StackPanel)strength).Children.Add(test);
        body.Children.Add(strength);

        // Live level: watch it jump when the bobber splashes, to judge the sensitivity.
        var level = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        level.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        level.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        level.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        level.Children.Add(new TextBlock { Text = "WoW sound", Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        _meter = new ProgressBar { Style = Res<Style>("Meter"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_meter, 1);
        level.Children.Add(_meter);
        _status = new TextBlock { Style = Res<Style>("Muted"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(_status, 2);
        level.Children.Add(_status);
        body.Children.Add(level);

        body.Children.Add(new TextBlock
        {
            Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 12, 0, 0),
            Text = "Works best when the splash is the loudest thing: in WoW's Sound settings, turn Music off and Ambience down while you fish. "
                 + "It only counts a splash after a couple of quiet seconds, so fights and busy towns won't set it off. Each bite is written to switch.log.",
        });
        stack.Children.Add(body);
        UpdateStatus();
        return stack;
    }

    void Tick()
    {
        if (_meter == null) return;
        // Peak level on a 60 dB scale, so quiet ambience still moves the bar.
        float peak = _fishing.Level;
        double db = peak > 1e-5f ? 20 * Math.Log10(peak) : -100;
        _meter.Value = Math.Clamp((db + 60) / 60, 0, 1);
        if (_fishing.State != _shownState) UpdateStatus();
    }

    void UpdateStatus()
    {
        var state = _fishing.State;
        _shownState = state;
        if (_gameBadge != null && _gameBadgeBox != null)
        {
            bool running = state == WowListenState.Listening;
            _gameBadge.Text = running ? "Running" : "Not running";
            _gameBadge.Foreground = Res<Brush>(running ? "SuccessBrush" : "MutedBrush");
            _gameBadgeBox.Style = Res<Style>(running ? "BadgeSuccess" : "Badge");
        }
        if (_status == null) return;
        string text = state switch
        {
            WowListenState.Listening when !_fishing.IsOn => "Listening. Turn it on to rumble.",
            WowListenState.Listening => "Listening",
            WowListenState.NoGame => "Waiting for WoW to make a sound",
            _ => "Starting…",
        };
        if (_fishing.LastBite is { } at)
            text += $" · last bite {at:T}" + (_fishing.Bites > 1 ? $" · {_fishing.Bites} this session" : "");
        if (text == _shownStatus) return;
        _shownStatus = text;
        _status.Text = text;
    }

    // ---------------- pieces (same look as Settings) ----------------

    UIElement Section(string title, string glyph, IEnumerable<UIElement> rows, UIElement? right = null)
    {
        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(20, 16, 20, 6) };
        if (right != null) { DockPanel.SetDock(right, Dock.Right); header.Children.Add(right); }
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 15, Foreground = Res<Brush>("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SectionTitle"), Margin = new Thickness(10, 0, 0, 0) });
        header.Children.Add(name);
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

    UIElement Row(string title, string hint, UIElement? control, bool dim = false)
    {
        var grid = new Grid { Margin = new Thickness(20, 12, 20, 12), Opacity = dim ? 0.55 : 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
        text.Children.Add(new TextBlock
        {
            Text = hint, Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 3, 0, 0),
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

    UIElement Pills(string label, IEnumerable<(string Value, string Label)> choices, string current, Action<string> pick, string? tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), ToolTip = tip };
        row.Children.Add(new TextBlock { Text = label, Width = 92, Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        foreach (var (value, text) in choices)
        {
            var pill = new ToggleButton { Style = Res<Style>("PillToggle"), Content = text, IsChecked = current == value, Background = Res<Brush>("AccentBrush"), Margin = new Thickness(0, 0, 6, 0) };
            pill.Click += (_, _) => pick(value);
            row.Children.Add(pill);
        }
        return row;
    }

    TextBlock SoonLabel() => new() { Text = "Soon", FontSize = 11.5, Foreground = Res<Brush>("FaintBrush") };

    CheckBox Switch(bool value, Action<bool> set)
    {
        var sw = new CheckBox { Style = Res<Style>("Switch"), IsChecked = value };
        sw.Click += (_, _) => set(sw.IsChecked == true);
        return sw;
    }

    Button Button(string text) => new()
    {
        Content = text, FontWeight = FontWeights.SemiBold, Padding = new Thickness(12, 5, 12, 5), Style = Res<Style>("GhostButton"),
    };

    T Res<T>(string key) where T : class => (T)FindResource(key);
}
