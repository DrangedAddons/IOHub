using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using IOHub.Devices;
using IOHub.Profiles;
using IOHub.Ui;

namespace IOHub.Pages;

/// <summary>The landing page: this PC on the left, wired to each profile, with the active one lit.</summary>
public partial class OverviewPage : UserControl
{
    readonly DeviceHub _hub;
    readonly ProfileService _profiles;
    readonly AppSettings _settings;

    static readonly Color Blue = Color.FromRgb(0x5B, 0xB8, 0xF5);

    /// <summary>Raised when a profile card is clicked; the window switches to the Profiles page.</summary>
    public event Action? OpenProfiles;

    /// <summary>Raised by a card's Switch button.</summary>
    public event Action<Profile>? SwitchRequested;

    public OverviewPage(DeviceHub hub, ProfileService profiles, AppSettings settings)
    {
        InitializeComponent();
        _hub = hub;
        _profiles = profiles;
        _settings = settings;
        _hub.Refreshed += Rebuild;
        _profiles.Changed += Rebuild;
        IsVisibleChanged += (_, _) => { if (IsVisible && _stale) Rebuild(); };

        Map.Layout = Enum.TryParse<TopologyLayout>(settings.OverviewLayout, out var layout) ? layout : TopologyLayout.Flow;
        (Map.Layout switch { TopologyLayout.Hub => HubLayout, TopologyLayout.Tree => TreeLayout, _ => FlowLayout }).IsChecked = true;
        Rebuild();
    }

    void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (Map == null) return; // fires during InitializeComponent
        var layout = sender == HubLayout ? TopologyLayout.Hub : sender == TreeLayout ? TopologyLayout.Tree : TopologyLayout.Flow;
        if (layout == Map.Layout) return;
        Map.Layout = layout;
        FitMapSoon();
        // A quick fade hides the jump between arrangements.
        Map.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _settings.OverviewLayout = layout.ToString();
        _settings.Save();
    }

    bool _stale;

    void Rebuild()
    {
        // Hidden pages (including everything while the app sits in the tray) don't rebuild; they catch up when shown.
        if (!IsVisible) { _stale = true; return; }
        _stale = false;
        var profiles = _profiles.Profiles;
        var active = ProfileMatcher.Active(_profiles, _hub);
        List<UIElement> nodes = profiles.Count == 0
            ? [GhostCard()]
            : profiles.Select(p => ProfileCard(p, p == active)).ToList<UIElement>();
        Map.SetContent(HostCard(active), nodes, active == null ? -1 : profiles.IndexOf(active));
        FitMapSoon();

        if (active != null) SetStatus($"In {active.Name}", Blue, Color.FromArgb(0x1F, Blue.R, Blue.G, Blue.B));
        else if (profiles.Count > 0) SetStatus("No profile matches right now", Color.FromRgb(0x7C, 0x84, 0x92), Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        else SetStatus("No profiles yet", Color.FromRgb(0x7C, 0x84, 0x92), Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    }

    void MapScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitMap();

    // After the new cards have been measured, so RequiredWidth sees their sizes.
    void FitMapSoon() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, FitMap);

    /// <summary>Scales the map down when the layout is wider than the window, e.g. Hub's two card columns.</summary>
    void FitMap()
    {
        double available = MapScroll.ActualWidth, needed = Map.RequiredWidth;
        double scale = available > 0 && needed > available ? available / needed : 1;
        if (Map.LayoutTransform is ScaleTransform current && Math.Abs(current.ScaleX - scale) < 0.005) return;
        Map.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    void SetStatus(string text, Color fg, Color bg)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(fg);
        StatusDot.Fill = new SolidColorBrush(fg);
        StatusChip.Background = new SolidColorBrush(bg);
        StatusChip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, fg.R, fg.G, fg.B));
    }

    // ---------------- host ----------------

    UIElement HostCard(Profile? active)
    {
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(13), Background = Res<Brush>("AccentGradient"),
            Child = new TextBlock { Text = "", Style = Res<Style>("Icon"), FontSize = 21, Foreground = Brushes.White },
        });
        var title = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "THIS PC", Style = Res<Style>("Overline") });
        title.Children.Add(new TextBlock
        {
            Text = Environment.MachineName, FontFamily = Res<FontFamily>("DisplayFont"), FontSize = 17, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        stack.Children.Add(head);
        stack.Children.Add(Divider());

        var displays = _hub.Displays.Where(d => d.IsActive).ToList();
        var main = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        stack.Children.Add(InfoRow("", "Display",
            main == null ? "None" : displays.Count > 1 ? $"{main.Name} +{displays.Count - 1}" : main.Name));
        stack.Children.Add(InfoRow("", "Sound", DefaultName(AudioFlow.Output)));
        stack.Children.Add(InfoRow("", "Mic", DefaultName(AudioFlow.Input)));
        int pads = _hub.Input.Controllers.Count;
        stack.Children.Add(InfoRow("", "Controllers", pads == 0 ? "None connected" : $"{pads} connected"));
        int cams = _hub.Cameras.Count(c => c.IsEnabled);
        stack.Children.Add(InfoRow("", "Cameras", cams == 0 ? "None connected" : $"{cams} ready"));

        var card = new Border
        {
            Width = 300, CornerRadius = new CornerRadius(16), Padding = new Thickness(20), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(active != null ? Color.FromArgb(0x60, Blue.R, Blue.G, Blue.B) : Color.FromRgb(0x30, 0x35, 0x3F)),
            Background = new LinearGradientBrush(Color.FromRgb(0x1F, 0x23, 0x2F), Color.FromRgb(0x18, 0x1B, 0x23), 90),
            Child = stack,
        };
        if (active != null) card.Effect = Glow();
        return card;
    }

    string DefaultName(AudioFlow flow) => _hub.Audio.FirstOrDefault(a => a.Flow == flow && a.IsDefault)?.Name ?? "None";

    // ---------------- profiles ----------------

    UIElement ProfileCard(Profile p, bool isActive)
    {
        var color = (Color)ColorConverter.ConvertFromString(p.Color);
        var brush = new SolidColorBrush(color);
        var root = new Grid();
        root.Children.Add(new Border
        {
            Height = 3, CornerRadius = new CornerRadius(0, 0, 3, 3), Background = brush,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 0, 18, 0),
        });

        var stack = new StackPanel { Margin = new Thickness(18, 18, 18, 16) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock
        {
            Text = p.Name, FontFamily = Res<FontFamily>("DisplayFont"), FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var chip = isActive ? ActiveChip() : p.ChangesNothing ? StandbyChip() : SwitchButton(p);
        Grid.SetColumn(chip, 2);
        head.Children.Add(chip);
        stack.Children.Add(head);

        var rows = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (p.Displays.Count > 0)
        {
            var mainId = p.PrimaryDisplay ?? p.Displays[0];
            var (mainName, _) = DeviceName(mainId, _hub.Displays.Select(d => (d.Id, d.Name)));
            bool allThere = p.Displays.All(id => _hub.Displays.Any(d => d.Id == id));
            rows.Children.Add(InfoRow("", "Display", p.Displays.Count > 1 ? $"{mainName} +{p.Displays.Count - 1}" : mainName, !allThere));
        }
        if (p.AudioOutput != null) rows.Children.Add(AudioRow("", "Sound", p.AudioOutput));
        if (p.AudioInput != null) rows.Children.Add(AudioRow("", "Mic", p.AudioInput));
        if (p.MainInput != null)
        {
            var (inputName, connected) = DeviceName(p.MainInput, ProfileMatcher.AllInput(_hub).Select(i => (i.Id, i.Name)));
            rows.Children.Add(InfoRow("", "Input", inputName, !connected));
        }
        if (p.Camera != null)
        {
            var (cameraName, connected) = DeviceName(p.Camera, _hub.Cameras.Select(c => (c.Id, c.Name)));
            rows.Children.Add(InfoRow("", "Camera", cameraName, !connected));
        }
        if (p.ChangesNothing)
            rows.Children.Add(new TextBlock { Text = "Doesn't change anything yet", Style = Res<Style>("Muted"), FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 2) });
        stack.Children.Add(rows);

        stack.Children.Add(Divider());
        var triggers = TriggersFor(p);
        stack.Children.Add(triggers.Count == 0
            ? InfoRow("", "Trigger", "Not set up yet", muted: true)
            : InfoRow("", triggers.Count == 1 ? "Trigger" : "Triggers", string.Join(" · ", triggers)));

        root.Children.Add(stack);

        var normalBorder = new SolidColorBrush(isActive ? Color.FromArgb(0x90, Blue.R, Blue.G, Blue.B) : Color.FromRgb(0x26, 0x2A, 0x33));
        var card = new Border
        {
            Width = 330, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = normalBorder,
            Background = Res<Brush>("SurfaceBrush"), Child = root, Cursor = Cursors.Hand, ToolTip = "Edit in Profiles",
        };
        if (isActive) card.Effect = Glow();
        card.MouseEnter += (_, _) => { if (!isActive) card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C)); };
        card.MouseLeave += (_, _) => card.BorderBrush = normalBorder;
        card.MouseLeftButtonUp += (_, _) => OpenProfiles?.Invoke();
        return card;
    }

    UIElement AudioRow(string glyph, string label, string id)
    {
        var (name, connected) = DeviceName(id, _hub.Audio.Select(a => (a.Id, a.Name)));
        return InfoRow(glyph, label, name, !connected);
    }

    /// <summary>Live name if the device is here, otherwise the name remembered when the profile was saved.</summary>
    (string Name, bool Connected) DeviceName(string id, IEnumerable<(string Id, string Name)> live)
    {
        foreach (var (lid, lname) in live)
            if (lid == id) return (lname, true);
        return (_profiles.Data.Devices.FirstOrDefault(k => k.Id == id)?.Name ?? "Unknown device", false);
    }

    UIElement GhostCard()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = "Create your first profile", Margin = new Thickness(9, 0, 0, 0), FontWeight = FontWeights.SemiBold });
        var dash = new Rectangle { RadiusX = 14, RadiusY = 14, Stroke = new SolidColorBrush(Color.FromRgb(0x35, 0x3A, 0x46)), StrokeThickness = 1.2, StrokeDashArray = [4, 3] };
        var grid = new Grid { Width = 330, Height = 120, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.Children.Add(dash);
        grid.Children.Add(label);
        TextElement.SetForeground(grid, Res<Brush>("MutedBrush"));
        grid.MouseEnter += (_, _) => { dash.Stroke = Res<Brush>("AccentBrush"); TextElement.SetForeground(grid, Res<Brush>("TextBrush")); };
        grid.MouseLeave += (_, _) => { dash.Stroke = new SolidColorBrush(Color.FromRgb(0x35, 0x3A, 0x46)); TextElement.SetForeground(grid, Res<Brush>("MutedBrush")); };
        grid.MouseLeftButtonUp += (_, _) => OpenProfiles?.Invoke();
        return grid;
    }

    // ---------------- pieces ----------------

    UIElement InfoRow(string glyph, string label, string value, bool warn = false, bool muted = false)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 13, Foreground = Res<Brush>("FaintBrush"), VerticalAlignment = VerticalAlignment.Center });
        var l = new TextBlock { Text = label, FontSize = 12, Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(l, 1);
        g.Children.Add(l);
        var v = new TextBlock
        {
            Text = warn ? $"{value} · not connected" : value, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res<Brush>(warn ? "WarnBrush" : muted ? "FaintBrush" : "SubtleBrush"),
            FontStyle = muted ? FontStyles.Italic : FontStyles.Normal, VerticalAlignment = VerticalAlignment.Center, ToolTip = value,
        };
        Grid.SetColumn(v, 2);
        g.Children.Add(v);
        return g;
    }

    UIElement Divider() => new Border { Height = 1, Background = Res<Brush>("LineBrush"), Margin = new Thickness(0, 14, 0, 10) };

    UIElement ActiveChip()
    {
        var dot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Blue), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(dot);
        sp.Children.Add(new TextBlock { Text = "Active", Margin = new Thickness(6, 0, 0, 0), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Blue) });
        return new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x24, Blue.R, Blue.G, Blue.B)), Child = sp,
        };
    }

    /// <summary>Enabled triggers that lead to this profile, including its number in any profile picker.</summary>
    List<string> TriggersFor(Profile p)
    {
        int index = _profiles.Profiles.IndexOf(p);
        var list = new List<string>();
        foreach (var t in _profiles.Data.Triggers.Where(t => t.Enabled))
        {
            if (t.Kind == IOHub.Triggers.TriggerKind.ProfilePicker && t.Key != 0 && index < 9) list.Add($"Picker {index + 1}");
            else if (t.ProfileId == p.Id && (!t.UsesKeyboard || t.Key != 0)) list.Add(IOHub.Triggers.KeyNames.Describe(t));
        }
        return list;
    }

    UIElement SwitchButton(Profile p)
    {
        var button = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = "Switch", FontSize = 11.5, FontWeight = FontWeights.SemiBold },
            ToolTip = $"Switch to {p.Name} now",
        };
        // Button marks the mouse-up handled, so the card behind it doesn't also open the Profiles page.
        button.Click += (_, _) => SwitchRequested?.Invoke(p);
        return button;
    }

    UIElement StandbyChip() => new Border
    {
        Style = Res<Style>("Badge"), Margin = new Thickness(0),
        Child = new TextBlock { Text = "Standby" },
    };

    static Effect Glow() => new DropShadowEffect { Color = Blue, BlurRadius = 32, ShadowDepth = 0, Opacity = 0.35 };

    T Res<T>(string key) where T : class => (T)FindResource(key);
}
