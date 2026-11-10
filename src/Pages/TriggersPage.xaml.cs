using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using IOHub.Devices;
using IOHub.Profiles;
using IOHub.Triggers;
using Trigger = IOHub.Triggers.Trigger;

namespace IOHub.Pages;

/// <summary>
/// One row (or card) per trigger, edited in place: what sets it off (When), which profile it switches to (Then), and
/// whether it switches back. Cards are rebuilt from the model after every edit.
/// </summary>
public partial class TriggersPage : UserControl
{
    readonly ProfileService _profiles;
    readonly TriggerEngine _engine;
    readonly FeatureService _features;
    readonly AppSettings _settings;
    Recording? _recording;
    string? _scrollTo;
    // Rows start collapsed to a one-line summary; these are the ones opened for editing.
    readonly HashSet<string> _expanded = [];

    /// <param name="Timer">Set when recording controller buttons: it reads the controllers.</param>
    sealed record Recording(Trigger Trigger, bool Combo, Button Button, System.Windows.Threading.DispatcherTimer? Timer = null);

    List<Trigger> AllTriggers => _profiles.Data.Triggers;

    public TriggersPage(ProfileService profiles, TriggerEngine engine, FeatureService features, AppSettings settings)
    {
        InitializeComponent();
        _profiles = profiles;
        _engine = engine;
        _features = features;
        _settings = settings;
        (settings.TriggersLayout == "Cards" ? CardsLayout : RowsLayout).IsChecked = true;
        _profiles.Changed += Rebuild;
        _engine.StateChanged += Rebuild;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopRecording();
            else if (_stale) Rebuild();
        };
        Rebuild();
    }

    /// <summary>"Manage in Addons" was clicked.</summary>
    public event Action? OpenAddons;

    void Save()
    {
        _profiles.Save(); // raises Changed: the engine reloads and this page rebuilds
    }

    // ---------------- list ----------------

    bool RowsMode => _settings.TriggersLayout != "Cards";

    void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return; // fires during InitializeComponent
        _settings.TriggersLayout = sender == CardsLayout ? "Cards" : "Rows";
        _settings.Save();
        Rebuild();
    }

    bool _stale;

    void Rebuild()
    {
        // Hidden pages (including everything while the app sits in the tray) don't rebuild; they catch up when shown.
        if (!IsVisible) { _stale = true; return; }
        _stale = false;
        if (_recording != null) return; // StopRecording rebuilds
        Cards.Children.Clear();
        // Rows are full width: a minimum item width wider than any window leaves one column.
        Cards.MinItemWidth = RowsMode ? 100_000 : 440;
        Cards.Spacing = RowsMode ? 12 : 16;
        FrameworkElement? scrollTarget = null;
        foreach (var t in AllTriggers)
        {
            var element = RowsMode ? Row(t) : Card(t);
            if (t.Id == _scrollTo) scrollTarget = element;
            Cards.Children.Add(element);
        }
        if (AllTriggers.Count > 0) Cards.Children.Add(AddAnother());
        EmptyState.Visibility = AllTriggers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PausedNote.Visibility = _engine.Paused ? Visibility.Visible : Visibility.Collapsed;

        if (scrollTarget != null)
        {
            _scrollTo = null;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => scrollTarget.BringIntoView());
        }
    }

    public static (string Title, string Glyph, string Hint) KindInfo(TriggerKind k) => k switch
    {
        TriggerKind.Hotkey => ("Hotkey", "", "A key combination, like Ctrl + Alt + T"),
        TriggerKind.KeyGesture => ("Key gesture", "", "Double tap, triple tap or hold one key"),
        TriggerKind.ProfilePicker => ("Profile picker", "", "A gesture, then 1–9 to pick the profile"),
        TriggerKind.ControllerCombo => ("Controller buttons", "\uE7FC", "Press, tap or hold a button, or several together like View + Menu"),
        TriggerKind.ControllerPicker => ("Controller picker", "\uE7FC", "Buttons together, then A, B, X, Y or the D-pad to pick the profile"),
        TriggerKind.WowFishingBite => ("WoW fishing bite", "\uE767", "Your bobber splashes in World of Warcraft, heard in its sound"),
        TriggerKind.SteamBigPicture => ("Steam Big Picture", "", "Switch while Big Picture is open"),
        _ => ("New trigger", "", "Choose what sets it off"),
    };

    // Pickers aren't a kind any more: "Pick a profile" is a THEN choice on key gestures and controller buttons.
    static readonly TriggerKind[] Kinds = [TriggerKind.Hotkey, TriggerKind.KeyGesture, TriggerKind.ControllerCombo, TriggerKind.SteamBigPicture, TriggerKind.WowFishingBite];

    static string CategoryName(TriggerCategory c) => c switch
    {
        TriggerCategory.Integrations => "Integration",
        TriggerCategory.Controller => "Controller",
        _ => "Keys",
    };

    /// <summary>
    /// Icon, type, one-line summary, on/off and the menu. Rows also show the status here, and a chevron when
    /// <paramref name="expanded"/> is set.
    /// </summary>
    Grid Header(Trigger t, bool withStatus, bool? expanded = null)
    {
        var (title, glyph, _) = KindInfo(t.Kind);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Border
        {
            Style = Res<Style>("IconTile"), Background = Res<Brush>("AccentSoftBrush"), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x7B, 0x8C, 0xFF)),
            Child = new TextBlock { Text = glyph, Style = Res<Style>("Icon"), FontSize = 18, Foreground = Res<Brush>("AccentBrush") },
        };
        Place(head, tile, 0);
        var titles = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock { Text = title, Style = Res<Style>("ItemTitle"), FontSize = 15 });
        if (t.Kind != TriggerKind.Unset)
            titleRow.Children.Add(new Border
            {
                Style = Res<Style>("Badge"), Margin = new Thickness(10, 1, 0, 0),
                Child = new TextBlock { Text = CategoryName(Trigger.CategoryOf(t.Kind)) },
            });
        titles.Children.Add(titleRow);
        titles.Children.Add(new TextBlock { Text = Summary(t), Style = Res<Style>("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        Place(head, titles, 1);
        if (withStatus)
        {
            var status = StatusLine(t);
            status.Margin = new Thickness(0, 0, 18, 0);
            Place(head, status, 2);
        }
        if (t.Kind != TriggerKind.Unset)
        {
            var enabled = new CheckBox { Style = Res<Style>("Switch"), IsChecked = t.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = t.Enabled ? "On" : "Off" };
            enabled.Click += (_, _) => { t.Enabled = enabled.IsChecked == true; Save(); };
            Place(head, enabled, 3);
        }
        var menu = new Button { Style = Res<Style>("IconButton"), Content = "", Margin = new Thickness(8, 0, -6, 0), ToolTip = "More", VerticalAlignment = VerticalAlignment.Center };
        menu.Click += (_, _) => ShowMenu(t, menu);
        Place(head, menu, 4);
        if (expanded is bool open)
            Place(head, new TextBlock
            {
                Text = open ? "" : "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 11,
                Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 2, 0),
            }, 5);
        return head;
    }

    FrameworkElement Card(Trigger t)
    {
        var stack = new StackPanel();
        stack.Children.Add(Header(t, withStatus: false));
        stack.Children.Add(Divider());
        if (t.Kind == TriggerKind.Unset)
        {
            stack.Children.Add(TypePicker(t));
            return new Border { Style = Res<Style>("Card"), Child = stack, BorderBrush = Res<Brush>("AccentBrush") };
        }

        var body = new StackPanel { Opacity = t.Enabled ? 1 : 0.5, IsEnabled = t.Enabled };
        body.Children.Add(Label("WHEN"));
        body.Children.Add(WhenEditor(t));
        body.Children.Add(Label("THEN", top: 18));
        body.Children.Add(ThenEditor(t));
        if (Options(t, compact: false) is { } options) { options.Margin = new Thickness(0, 18, 0, 0); body.Children.Add(options); }
        stack.Children.Add(body);

        var status = StatusLine(t);
        status.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(new Border { BorderBrush = Res<Brush>("LineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0), Child = status });
        return new Border { Style = Res<Style>("Card"), Child = stack };
    }

    /// <summary>
    /// The full-width layout. Collapsed it's one line: type, summary, status, on/off. Expanded (click the line)
    /// it adds When → Then → Options left to right, wrapping if narrow.
    /// </summary>
    FrameworkElement Row(Trigger t)
    {
        bool open = t.Kind == TriggerKind.Unset || _expanded.Contains(t.Id);
        var stack = new StackPanel();
        var header = Header(t, withStatus: t.Kind != TriggerKind.Unset, expanded: t.Kind == TriggerKind.Unset ? null : open);
        if (t.Kind != TriggerKind.Unset)
        {
            // The switch and menu button handle their own clicks, so this only fires on the rest of the line.
            header.Background = Brushes.Transparent;
            header.Cursor = Cursors.Hand;
            header.ToolTip = open ? "Collapse" : "Show details";
            header.MouseLeftButtonUp += (_, _) =>
            {
                if (!_expanded.Remove(t.Id)) _expanded.Add(t.Id);
                Rebuild();
            };
        }
        stack.Children.Add(header);
        if (!open)
            return new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 12, 20, 12), Child = stack };
        if (t.Kind == TriggerKind.Unset)
        {
            stack.Children.Add(Divider());
            stack.Children.Add(TypePicker(t));
            return new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 16, 20, 16), Child = stack, BorderBrush = Res<Brush>("AccentBrush") };
        }

        // Fixed columns side by side, never wrapping under each other: the window can't get narrower than they need
        // (MainWindow's MinWidth), and long text wraps inside its own column.
        var flow = new Grid { Margin = new Thickness(0, 16, 0, 0), Opacity = t.Enabled ? 1 : 0.5, IsEnabled = t.Enabled };
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WhenWidth) });
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ArrowWidth) });
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ThenWidth) });
        flow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = OptionsMinWidth });
        Place(flow, Block("WHEN", WhenEditor(t)), 0);
        Place(flow, new TextBlock
        {
            Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 14, Foreground = Res<Brush>("FaintBrush"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 0, 0),
        }, 1);
        Place(flow, Block("THEN", ThenEditor(t)), 2);
        if (Options(t, compact: true) is { } options) Place(flow, Block("OPTIONS", options), 3);
        stack.Children.Add(flow);
        return new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 16, 20, 16), Child = stack };
    }

    // Rows layout column widths, from the widest things in them (the five-gesture bar is about 410 px, the THEN choice
    // about 380). MainWindow.MinWidth comes from these, so the columns always fit side by side.
    public const double WhenWidth = 440, ArrowWidth = 42, ThenWidth = 420, OptionsMinWidth = 220;

    /// <summary>
    /// The narrowest this page can be with a row's columns side by side: the columns, the card's padding, the page's
    /// margins, and room for the scroll bar.
    /// </summary>
    public const double MinPageWidth = WhenWidth + ArrowWidth + ThenWidth + OptionsMinWidth + 42 + 80 + 20;

    UIElement Block(string label, UIElement content)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 28, 6) };
        block.Children.Add(Label(label));
        block.Children.Add(content);
        return block;
    }

    /// <summary>The dashed "add another" slot after the last trigger.</summary>
    UIElement AddAnother()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = "Add another trigger", Margin = new Thickness(9, 0, 0, 0), FontWeight = FontWeights.SemiBold });
        var idle = new SolidColorBrush(Color.FromRgb(0x35, 0x3A, 0x46));
        var dash = new Rectangle { RadiusX = 14, RadiusY = 14, Stroke = idle, StrokeThickness = 1.2, StrokeDashArray = [4, 3] };
        var slot = new Grid { MinHeight = RowsMode ? 64 : 140, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "New trigger" };
        slot.Children.Add(dash);
        slot.Children.Add(label);
        TextElement.SetForeground(slot, Res<Brush>("MutedBrush"));
        slot.MouseEnter += (_, _) =>
        {
            dash.Stroke = Res<Brush>("AccentBrush");
            dash.Fill = new SolidColorBrush(Color.FromArgb(0x0D, 0x7B, 0x8C, 0xFF));
            TextElement.SetForeground(slot, Res<Brush>("TextBrush"));
        };
        slot.MouseLeave += (_, _) =>
        {
            dash.Stroke = idle;
            dash.Fill = null;
            TextElement.SetForeground(slot, Res<Brush>("MutedBrush"));
        };
        slot.MouseLeftButtonUp += (_, _) => New_Click(slot, new RoutedEventArgs());
        return slot;
    }

    /// <summary>Shown on a new trigger: pick what kind it is, grouped into Keys, Controller and Integrations.</summary>
    UIElement TypePicker(Trigger t)
    {
        var panel = new StackPanel();
        foreach (var category in new[] { TriggerCategory.Keys, TriggerCategory.Controller, TriggerCategory.Integrations })
        {
            panel.Children.Add(Label(category switch { TriggerCategory.Keys => "KEYS", TriggerCategory.Controller => "CONTROLLER", _ => "INTEGRATIONS" },
                top: category == TriggerCategory.Keys ? 0 : 14));
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var kind in Kinds.Where(k => Trigger.CategoryOf(k) == category)) wrap.Children.Add(TypeTile(t, kind));
            if (category == TriggerCategory.Integrations)
                wrap.Children.Add(new TextBlock
                {
                    Text = "More integrations coming", Style = Res<Style>("Muted"), FontStyle = FontStyles.Italic,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 10),
                });
            panel.Children.Add(wrap);
        }
        return panel;
    }

    UIElement TypeTile(Trigger t, TriggerKind kind)
    {
        var (title, glyph, hint) = KindInfo(kind);
        var content = new Grid { Width = 232 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(content, new TextBlock
        {
            Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 16, Foreground = Res<Brush>("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0),
        }, 0);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = hint, Style = Res<Style>("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) });
        Place(content, text, 1);
        var button = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 10, 10), Content = content };
        button.Click += (_, _) =>
        {
            t.Kind = kind;
            // Big Picture is a "while it's open" trigger, so going back afterwards is the expected default.
            t.SwitchBack = kind == TriggerKind.SteamBigPicture;
            // Holding is the hardest to set off by accident mid-game.
            if (kind == TriggerKind.ControllerCombo) { t.Gesture = GestureKind.Hold; t.HoldMs = 800; }
            else if (t.Gesture == GestureKind.Press) t.Gesture = GestureKind.DoubleTap;
            // Only key gestures and controller buttons can open the picker.
            if (t.IsPicker && kind is not (TriggerKind.KeyGesture or TriggerKind.ControllerCombo)) t.Then = ThenKind.Profile;
            if (t.Gesture == GestureKind.WhileHeld && kind is not (TriggerKind.KeyGesture or TriggerKind.ControllerCombo)) t.Gesture = GestureKind.DoubleTap;
            // What a bite is for: buzz the controller. Still changeable, but it starts out ready.
            if (kind == TriggerKind.WowFishingBite && !t.ChangesDevice && t.ProfileId == null
                && _features.Find(FeatureService.ControllersId, "rumble") is { } rumble)
                PickFeature(t, rumble, FeatureAction.Run, null);
            Save();
        };
        return button;
    }

    string Summary(Trigger t)
    {
        var target = _profiles.Profiles.FirstOrDefault(p => p.Id == t.ProfileId);
        return t.Kind switch
        {
            TriggerKind.Unset => "Choose what sets it off",
            _ when t.IsPicker => KeyNames.Describe(t),
            TriggerKind.SteamBigPicture when !t.ChangesDevice => target == null ? "Not linked to a profile"
                : $"Switches to {target.Name} while it's open" + (t.SwitchBack ? ", then reverts" : ""),
            TriggerKind.WowFishingBite when t.ChangesDevice => $"Fishing bite → {_features.Describe(t, withDevice: true)}",
            _ when t.ChangesDevice => t.Kind == TriggerKind.SteamBigPicture
                ? $"While it's open → {_features.Describe(t, withDevice: true)}" + (t.SwitchBack ? ", undone when it closes" : "")
                : $"{KeyNames.Describe(t)} → {_features.Describe(t, withDevice: true)}",
            _ => target == null ? KeyNames.Describe(t)
                : $"{KeyNames.Describe(t)} → {target.Name}" + (t.SwitchBack ? " · press again to go back" : ""),
        };
    }

    void ShowMenu(Trigger t, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (t.Kind != TriggerKind.Unset)
        {
            var test = MenuItem("Try it now", "", () => _ = _engine.Test(t));
            test.IsEnabled = t.IsPicker ? _profiles.Profiles.Count > 0 : t.ChangesDevice ? t.Feature != null : t.ProfileId != null;
            menu.Items.Add(test);
            menu.Items.Add(MenuItem("Change type", "", () =>
            {
                t.Kind = TriggerKind.Unset;
                t.Key = 0;
                t.Mods = KeyMods.None;
                t.Buttons = 0;
                Save();
            }));
            menu.Items.Add(new Separator());
        }
        var delete = MenuItem("Delete", "", () => { AllTriggers.Remove(t); Save(); });
        delete.Foreground = Res<Brush>("DangerBrush");
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    // ---------------- When ----------------

    UIElement WhenEditor(Trigger t)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        switch (t.Kind)
        {
            case TriggerKind.Hotkey:
                panel.Children.Add(KeyButton(t, combo: true));
                break;

            case TriggerKind.KeyGesture:
                var row = new StackPanel(); // gestures, then the key under them: never side by side, so it never wraps
                row.Children.Add(GestureChoice(t));
                var key = KeyButton(t, combo: false);
                key.Margin = new Thickness(0, 8, 0, 0);
                row.Children.Add(key);
                panel.Children.Add(row);
                if (t.Gesture == GestureKind.Hold) panel.Children.Add(HoldChoice(t));
                break;

            case TriggerKind.ControllerCombo:
                var padRow = new StackPanel(); // gestures, then the buttons under them
                padRow.Children.Add(GestureChoice(t, controller: true));
                var buttons = ButtonsButton(t);
                buttons.Margin = new Thickness(0, 8, 0, 0);
                padRow.Children.Add(buttons);
                panel.Children.Add(padRow);
                if (t.Gesture == GestureKind.Hold) panel.Children.Add(HoldChoice(t));
                panel.Children.Add(Note(ControllerButtons.Count(t.Buttons) == 1
                    ? SingleButtonWarning(t.Buttons)
                    : "Only counts when exactly these buttons are held, so it won't go off mid-game while you're holding something else."));
                if (_engine.ControllersConnected == 0) panel.Children.Add(Note("No controller is connected right now."));
                break;

            case TriggerKind.SteamBigPicture:
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = "Steam Big Picture opens", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(StateChip(_engine.BigPictureOpen ? "Open now" : "Closed", _engine.BigPictureOpen));
                panel.Children.Add(line);
                panel.Children.Add(Note("Works with any Steam install: it watches for the Big Picture window itself."));
                break;

            case TriggerKind.WowFishingBite:
                WowEditor(t, panel);
                break;
        }
        return panel;
    }

    /// <summary>WoW fishing bite: whether WoW is running, how sensitive, and WoW's live sound level to tune that by.</summary>
    void WowEditor(Trigger t, StackPanel panel)
    {
        bool listening = _engine.Wow.State == Integrations.WowListenState.Listening;
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Your bobber splashes", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (t.Enabled && !_engine.Paused) line.Children.Add(StateChip(listening ? "WoW running" : "WoW not running", listening));
        panel.Children.Add(line);

        var sensitivity = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "Higher catches quieter splashes, but also more sounds that aren't the bite. Try Normal first.",
        };
        sensitivity.Children.Add(new TextBlock { Text = "Sensitivity", Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        foreach (var (value, label, _) in Integrations.WowFishing.Sensitivities)
        {
            var pill = new ToggleButton { Style = Res<Style>("PillToggle"), Content = label, IsChecked = t.Sensitivity == value, Background = Res<Brush>("AccentBrush"), Margin = new Thickness(0, 0, 6, 0) };
            pill.Click += (_, _) => { t.Sensitivity = value; Save(); };
            sensitivity.Children.Add(pill);
        }
        panel.Children.Add(sensitivity);

        panel.Children.Add(WowMeter(t, listening));

        panel.Children.Add(Note(!listening || _engine.Wow.MatchingSound
            ? "Listens to WoW's own sound, so other apps don't count, and matches it against the bite splash itself, so casting, reeling in, "
              + "looting, fights and people splashing through the water don't set it off. Another angler's splash sounds the same but quieter, "
              + "so splashes far quieter than yours are ignored, or set the threshold yourself on the meter."
            : "Listens to WoW's own sound, so other apps don't count. This Windows can't capture WoW's sound for matching the splash itself, "
              + "so it goes by level: it only takes a loud sound after a couple of quiet seconds."));

        // The companion addon: quiets everything but effects while a pole is in hand, so the splash stands out.
        panel.Children.Add(AddonsPage.FishIOStatus(() => OpenAddons?.Invoke()));

        panel.Children.Add(WowRecording());
    }

    /// <summary>
    /// WoW's sound level, live, on a 60 dB scale (-60 to 0 dBFS) so quiet ambience still moves it. Drag on it to set
    /// the quietest splash that rumbles (the line); Auto goes back to ignoring splashes far quieter than yours. A thin
    /// tick holds the loudest of the last two seconds, so a splash's height can be read off; a green one marks the last
    /// splash that rumbled.
    /// </summary>
    UIElement WowMeter(Trigger t, bool listening)
    {
        const double W = 240, H = 10;
        static double X(double db) => Math.Clamp((db + 60) / 60, 0, 1) * W;
        static float Db(double x) => (float)Math.Round(Math.Clamp(x / W, 0, 1) * 60 - 60);

        var box = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "WoW sound", Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });

        var area = new Grid { Width = W, Height = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Drag to set the quietest splash that rumbles. Splashes under the line are ignored." };
        var meter = new ProgressBar { Style = Res<Style>("Meter"), Width = W, Height = H, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        area.Children.Add(meter);
        var marks = new Canvas { IsHitTestVisible = false };
        area.Children.Add(marks);
        Rectangle Tick(Brush brush, double width, double height) =>
            new() { Width = width, Height = height, Fill = brush, RadiusX = 1, RadiusY = 1 };
        var hold = Tick(Res<Brush>("MutedBrush"), 2, H);
        var last = Tick(Res<Brush>("SuccessBrush"), 2, H);
        var line = Tick(Res<Brush>("AccentBrush"), 3, 22);
        foreach (var r in new[] { hold, last, line }) marks.Children.Add(r);
        Canvas.SetTop(hold, (22 - H) / 2); Canvas.SetTop(last, (22 - H) / 2); Canvas.SetTop(line, 0);
        row.Children.Add(area);

        var label = new TextBlock { Style = Res<Style>("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        row.Children.Add(label);
        var auto = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 2, 10, 3), Content = "Auto", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Back to automatic: splashes far quieter than your own are someone else's." };
        auto.Click += (_, _) => { t.MinLevelDb = null; Save(); };
        row.Children.Add(auto);
        box.Children.Add(row);

        void ShowThreshold()
        {
            line.Visibility = t.MinLevelDb == null ? Visibility.Collapsed : Visibility.Visible;
            if (t.MinLevelDb is { } min) Canvas.SetLeft(line, X(min) - 1.5);
            label.Text = t.MinLevelDb is { } m ? $"Rumbles at {m:0} dBFS or louder" : "Threshold: automatic";
            auto.Visibility = t.MinLevelDb == null ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowThreshold();

        // Drag: the line follows, and it's saved when let go.
        area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); t.MinLevelDb = Db(e.GetPosition(area).X); ShowThreshold(); e.Handled = true; };
        area.MouseMove += (_, e) => { if (area.IsMouseCaptured) { t.MinLevelDb = Db(e.GetPosition(area).X); ShowThreshold(); } };
        area.MouseLeftButtonUp += (_, _) => { if (!area.IsMouseCaptured) return; area.ReleaseMouseCapture(); Save(); };

        double holdDb = -100;
        long holdAt = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            float peak = _engine.Wow.Level;
            double db = peak > 1e-5f ? 20 * Math.Log10(peak) : -100;
            meter.Value = Math.Clamp((db + 60) / 60, 0, 1);
            long now = Environment.TickCount64;
            if (db >= holdDb || now - holdAt > 2000) { holdDb = db; holdAt = now; }
            hold.Visibility = holdDb > -60 ? Visibility.Visible : Visibility.Hidden;
            Canvas.SetLeft(hold, X(holdDb) - 1);
            last.Visibility = _engine.Wow.LastSplashDb is null ? Visibility.Hidden : Visibility.Visible;
            if (_engine.Wow.LastSplashDb is { } s) Canvas.SetLeft(last, X(s) - 1);
        };
        if (listening)
        {
            meter.Loaded += (_, _) => timer.Start();
            meter.Unloaded += (_, _) => timer.Stop();
        }

        box.Children.Add(Note("Drag on the meter to set the quietest splash that rumbles: the grey tick shows how loud the last couple of seconds got, "
            + "the green one your last bite. Put the line above other anglers' splashes and under yours."));
        return box;
    }

    /// <summary>
    /// Record WoW's sound for tuning: a WAV of WoW only, with every detection marked in a text file beside it. Fish a
    /// few casts, then send the two files over.
    /// </summary>
    UIElement WowRecording()
    {
        var recorder = _engine.Wow.Recorder;
        var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var button = new Button
        {
            Style = Res<Style>(recorder.IsRecording ? "DangerButton" : "GhostButton"), Padding = new Thickness(12, 5, 12, 5), FontWeight = FontWeights.SemiBold,
            Content = recorder.IsRecording ? "Stop recording" : "Record WoW's sound",
            IsEnabled = recorder.IsRecording || _engine.Wow.WowPid != 0,
            ToolTip = "For tuning the bite detection: records WoW's sound only, and marks every time it thought it heard a bite.",
        };
        button.Click += (_, _) =>
        {
            if (recorder.IsRecording) recorder.Stop();
            else if (_engine.Wow.WowPid is var pid and not 0) recorder.Start(pid);
        };
        row.Children.Add(button);
        var folder = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(12, 5, 12, 5), Content = "Open folder", Margin = new Thickness(8, 0, 0, 0) };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(Integrations.WowRecorder.Folder);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Integrations.WowRecorder.Folder) { UseShellExecute = true }); } catch { }
        };
        row.Children.Add(folder);
        var status = new TextBlock { Style = Res<Style>("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        row.Children.Add(status);
        box.Children.Add(row);

        void Show() => status.Text = recorder.IsRecording
            ? $"Recording {recorder.Elapsed:m\\:ss} of {Integrations.WowRecorder.MaxLength.TotalMinutes:0}:00"
            : recorder.Error ?? (_engine.Wow.WowPid == 0 ? "Start WoW to record it" : "");
        Show();
        if (recorder.IsRecording)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => Show();
            status.Loaded += (_, _) => timer.Start();
            status.Unloaded += (_, _) => timer.Stop();
        }
        box.Children.Add(Note("Helps tune the bite detection. Fish 10 to 15 casts with a pole in hand, then stop. Click the bobber just after "
            + "each splash: your clicks, keys and controller presses are bookmarked, so they show where the real bites were. "
            + "It saves WoW's sound only (about 11 MB a minute) and a list of those bookmarks and of when it thought it heard a bite. "
            + "Typing keys are listed only as \"a typing key\", never which one. Needs Windows 11."));
        return box;
    }

    UIElement GestureChoice(Trigger t, bool controller = false)
    {
        var box = new Border
        {
            Background = Res<Brush>("SurfaceBrush"), BorderBrush = Res<Brush>("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var choices = new List<(GestureKind, string)> { (GestureKind.DoubleTap, "Double tap"), (GestureKind.TripleTap, "Triple tap"), (GestureKind.Hold, "Hold") };
        if (controller) choices.Insert(0, (GestureKind.Press, "Press"));
        // Lasts while held, undone when let go: for switching profile, or a device feature that's on or off.
        if (WhileHeldFits(t)) choices.Add((GestureKind.WhileHeld, "While held"));
        foreach (var (g, label) in choices)
        {
            var rb = new RadioButton
            {
                Style = Res<Style>("SegTab"), GroupName = "gesture-" + t.Id, IsChecked = t.Gesture == g,
                Content = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12.5 },
            };
            rb.Checked += (_, _) => { if (t.Gesture != g) { t.Gesture = g; Save(); } };
            row.Children.Add(rb);
        }
        box.Child = row;
        return box;
    }

    UIElement HoldChoice(Trigger t)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Hold for", Foreground = Res<Brush>("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        foreach (var ms in new[] { 500, 800, 1500, 3000 })
        {
            var b = new ToggleButton
            {
                Style = Res<Style>("PillToggle"), Content = $"{ms / 1000.0:0.#} s", IsChecked = t.HoldMs == ms,
                Background = Res<Brush>("AccentBrush"), Margin = new Thickness(0, 0, 6, 0),
            };
            b.Click += (_, _) => { t.HoldMs = ms; Save(); };
            row.Children.Add(b);
        }
        return row;
    }

    Button KeyButton(Trigger t, bool combo)
    {
        var button = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        button.Content = t.Key == 0
            ? KeyPrompt(combo ? "Click, then press the keys" : "Click, then press the key")
            : KeyChips(combo ? KeyNames.Combo(t.Mods, t.Key) : KeyNames.Of(t.Key));
        button.ToolTip = "Click to change";
        button.Click += (_, _) => StartRecording(t, combo, button);
        return button;
    }

    UIElement KeyChips(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var parts = text.Split(" + ");
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) row.Children.Add(new TextBlock { Text = "+", Foreground = Res<Brush>("FaintBrush"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x43, 0x50)),
                BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 3),
                Child = new TextBlock { Text = parts[i], FontWeight = FontWeights.SemiBold, FontSize = 12.5 },
            });
        }
        return row;
    }

    UIElement KeyPrompt(string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Res<Brush>("MutedBrush") },
            new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), Foreground = Res<Brush>("MutedBrush") },
        },
    };

    // ---------------- recording keys ----------------

    void StartRecording(Trigger t, bool combo, Button button)
    {
        StopRecording();
        _recording = new Recording(t, combo, button);
        _engine.Suspended = true; // so pressing an existing hotkey here doesn't fire it
        button.BorderBrush = Res<Brush>("AccentBrush");
        button.Content = KeyPrompt(combo ? "Press the keys… (Esc cancels)" : "Press the key… (Esc cancels)");
        button.Focus();
        button.LostKeyboardFocus += OnRecorderLostFocus;
    }

    void OnRecorderLostFocus(object sender, KeyboardFocusChangedEventArgs e) => StopRecording();

    void StopRecording()
    {
        if (_recording == null) return;
        _recording.Button.LostKeyboardFocus -= OnRecorderLostFocus;
        _recording.Timer?.Stop();
        _recording = null;
        _padListening?.Dispose();
        _padListening = null;
        _engine.Suspended = false;
        Rebuild();
    }

    // ---------------- recording controller buttons ----------------

    Button ButtonsButton(Trigger t)
    {
        var button = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(10, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        button.Content = t.Buttons == 0 ? KeyPrompt("Click, then press the button or buttons") : KeyChips(ControllerButtons.Combo(t.Buttons));
        button.ToolTip = "Click to change";
        button.Click += (_, _) => StartPadRecording(t, button);
        return button;
    }

    /// <summary>
    /// Records a button combination: everything held at once, taken when they're all let go. The controllers are
    /// read here directly (the engine's reader is paused while recording, so the old combination doesn't fire).
    /// </summary>
    IDisposable? _padListening; // PlayStation and Switch pads are read faster while recording

    void StartPadRecording(Trigger t, Button button)
    {
        StopRecording();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _recording = new Recording(t, false, button, timer);
        _engine.Suspended = true;
        button.BorderBrush = Res<Brush>("AccentBrush");
        button.Focus(); // so Esc reaches the page
        button.LostKeyboardFocus += OnRecorderLostFocus;

        _padListening?.Dispose();
        _padListening = Pads.Listen();
        var slots = Pads.Connected();
        int seen = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastScan = 0L;
        void Prompt(string text) => button.Content = KeyPrompt(text);
        Prompt(slots.Count == 0 ? "Connect a controller… (Esc cancels)" : "Press the button, or hold several together, then let go… (Esc cancels)");
        timer.Tick += (_, _) =>
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) { StopRecording(); return; }
            if (slots.Count == 0 && clock.ElapsedMilliseconds - lastScan > 1000)
            {
                lastScan = clock.ElapsedMilliseconds;
                slots = Pads.Connected();
                if (slots.Count > 0) Prompt("Press the button, or hold several together, then let go… (Esc cancels)");
            }
            int held = 0;
            foreach (var slot in slots) if (Pads.TryRead(slot, out var b)) held |= b;
            if (held != 0)
            {
                if ((held | seen) != seen) button.Content = KeyChips(ControllerButtons.Combo(held | seen) + " + …");
                seen |= held;
                return;
            }
            if (seen == 0) return;
            t.Buttons = seen;
            StopRecording();
            Save();
        };
        timer.Start();
    }

    void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } rec) return;
        if (rec.Timer != null)
        {
            // Recording a controller: only Esc means anything here.
            e.Handled = true;
            if (e.Key == Key.Escape) StopRecording();
            return;
        }
        e.Handled = true; // keep Space/Enter from clicking the button
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed or Key.None) return;
        if (key == Key.Escape) { StopRecording(); return; }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        if (rec.Combo)
        {
            var mods = CurrentMods();
            if (KeyNames.IsModifier(vk))
            {
                rec.Button.Content = KeyChips(KeyNames.Combo(mods, 0) + " + …");
                return;
            }
            if (mods == KeyMods.None && KeyNames.IsTypingKey(vk))
            {
                // Taking a plain letter system-wide would stop it typing anywhere.
                rec.Button.Content = KeyPrompt("Add Ctrl, Alt, Shift or Win to that key");
                return;
            }
            rec.Trigger.Key = vk;
            rec.Trigger.Mods = mods;
        }
        else
        {
            if (KeyNames.IsTypingKey(vk))
            {
                // Double-tapping a letter happens all the time while typing.
                rec.Button.Content = KeyPrompt("Pick a key you don't type with, like Right Ctrl, Caps Lock or F1–F12");
                return;
            }
            rec.Trigger.Key = vk;
            rec.Trigger.Mods = KeyMods.None;
        }

        StopRecording();
        Save();
    }

    static KeyMods CurrentMods()
    {
        var m = KeyMods.None;
        var wpf = Keyboard.Modifiers;
        if (wpf.HasFlag(ModifierKeys.Control)) m |= KeyMods.Ctrl;
        if (wpf.HasFlag(ModifierKeys.Alt)) m |= KeyMods.Alt;
        if (wpf.HasFlag(ModifierKeys.Shift)) m |= KeyMods.Shift;
        if (wpf.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) m |= KeyMods.Win;
        return m;
    }

    // ---------------- Then ----------------

    UIElement ThenEditor(Trigger t)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(ThenChoice(t));
        if (t.IsPicker)
        {
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 8), FontSize = 13.5, TextWrapping = TextWrapping.Wrap,
                Text = t.UsesController ? "Opens the profile list. Then press a button within a few seconds:" : "Opens the profile list. Then press a number within a couple of seconds:",
            });
            if (_profiles.Profiles.Count == 0) { panel.Children.Add(Note("Make a profile first.")); return panel; }
            bool pad = t.UsesController;
            var picks = ControllerButtons.PickerButtons;
            var chips = new WrapPanel();
            for (int i = 0; i < _profiles.Profiles.Count && i < (pad ? picks.Length : 9); i++)
            {
                var p = _profiles.Profiles[i];
                var color = (Color)ColorConverter.ConvertFromString(p.Color);
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                chip.Children.Add(new Border
                {
                    MinWidth = 22, Height = 22, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
                    BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = pad ? ControllerButtons.ShortName(picks[i]) : (i + 1).ToString(), FontWeight = FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                chip.Children.Add(new TextBlock { Text = p.Name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                chips.Children.Add(chip);
            }
            panel.Children.Add(chips);
            panel.Children.Add(Note(pad ? "Buttons follow the column order on the Profiles page." : "Numbers follow the column order on the Profiles page."));
            if (t.Gesture == GestureKind.WhileHeld) panel.Children.Add(Note("The list stays open while held, and closes when let go."));
            return panel;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (t.ChangesDevice)
        {
            row.Children.Add(FeatureButton(t));
            panel.Children.Add(row);
            if (t.Gesture == GestureKind.WhileHeld) panel.Children.Add(Note("Does it while held, and puts it back how it was when let go."));
            return panel;
        }
        row.Children.Add(new TextBlock { Text = "Switch to", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(ProfileButton(t));
        panel.Children.Add(row);
        if (t.Gesture == GestureKind.WhileHeld) panel.Children.Add(Note("Switches while held, and back to the previous setup when let go."));
        return panel;
    }

    /// <summary>Switch to a profile, or change something about one device.</summary>
    UIElement ThenChoice(Trigger t)
    {
        var box = new Border
        {
            Background = Res<Brush>("SurfaceBrush"), BorderBrush = Res<Brush>("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var choices = new List<(ThenKind, string)> { (ThenKind.Profile, "Switch to a profile") };
        if (t.Kind is TriggerKind.KeyGesture or TriggerKind.ControllerCombo) choices.Add((ThenKind.Picker, "Pick a profile"));
        choices.Add((ThenKind.Device, "Change a device"));
        foreach (var (then, label) in choices)
        {
            var rb = new RadioButton
            {
                Style = Res<Style>("SegTab"), GroupName = "then-" + t.Id, IsChecked = t.Then == then,
                Content = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12.5 },
            };
            rb.Checked += (_, _) =>
            {
                if (t.Then == then) return;
                t.Then = then;
                if (then != ThenKind.Profile) { t.ProfileId = null; t.SwitchBack = false; }
                if (t.Gesture == GestureKind.WhileHeld && !WhileHeldFits(t)) t.Gesture = t.UsesController ? GestureKind.Hold : GestureKind.DoubleTap;
                Save();
            };
            row.Children.Add(rb);
        }
        box.Child = row;
        return box;
    }

    /// <summary>
    /// The device feature a trigger changes, picked from a menu of what's connected: kind of device → device →
    /// feature → what to do with it.
    /// </summary>
    Button FeatureButton(Trigger t)
    {
        var feature = _features.Find(t.DeviceId, t.Feature);
        // Two lines, the device over what it does to it, so it fits its column; anything longer is cut with "…".
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lines = new StackPanel();
        if (t.Feature == null)
            lines.Children.Add(new TextBlock { Text = "Pick a device and what to change", Foreground = Res<Brush>("WarnBrush") });
        else
        {
            lines.Children.Add(new TextBlock
            {
                Text = (feature?.DeviceName ?? t.DeviceName ?? "Device") + (feature == null ? " (not connected)" : ""),
                FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = feature == null ? Res<Brush>("WarnBrush") : Res<Brush>("TextBrush"),
            });
            lines.Children.Add(new TextBlock { Text = _features.Describe(t, withDevice: false), Foreground = Res<Brush>("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        }
        Place(content, lines, 0);
        Place(content, new TextBlock { Text = "\uE70D", FontFamily = Res<FontFamily>("IconFont"), FontSize = 9, Margin = new Thickness(12, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Res<Brush>("MutedBrush") }, 1);

        var button = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(12, 6, 12, 6), Content = content,
            HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxWidth = ThenWidth - 28,
        };
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            var all = _features.All();
            foreach (var section in all.GroupBy(f => f.Section))
            {
                var sectionItem = new MenuItem { Header = section.Key };
                foreach (var device in section.GroupBy(f => f.DeviceId))
                {
                    var deviceItem = new MenuItem { Header = device.First().DeviceName, FontWeight = device.Key == t.DeviceId ? FontWeights.SemiBold : FontWeights.Normal };
                    foreach (var f in device)
                    {
                        var featureItem = new MenuItem { Header = f.Title, FontWeight = f.DeviceId == t.DeviceId && f.Id == t.Feature ? FontWeights.SemiBold : FontWeights.Normal };
                        foreach (var (action, value, label) in f.Actions)
                        {
                            var item = new MenuItem
                            {
                                Header = label,
                                FontWeight = f.DeviceId == t.DeviceId && f.Id == t.Feature && action == t.Action && value == t.Value ? FontWeights.SemiBold : FontWeights.Normal,
                            };
                            item.Click += (_, _) => { PickFeature(t, f, action, value); Save(); };
                            featureItem.Items.Add(item);
                        }
                        deviceItem.Items.Add(featureItem);
                    }
                    sectionItem.Items.Add(deviceItem);
                }
                menu.Items.Add(sectionItem);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Nothing that can be changed is connected", IsEnabled = false });
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>"While held" is for keys and controller buttons, whatever they do: the switch, list or change lasts while held.</summary>
    static bool WhileHeldFits(Trigger t) => t.Kind is TriggerKind.KeyGesture or TriggerKind.ControllerCombo;

    /// <summary>One button on its own: fine for some, risky for the ones games use.</summary>
    static string SingleButtonWarning(int button) => button switch
    {
        ControllerButtons.Guide => "On an Xbox controller this button on its own also opens Game Bar, unless that's turned off (Windows Settings → Gaming → Game Bar). Steam may also take the PS or Home button for itself.",
        ControllerButtons.View or ControllerButtons.LeftStick or ControllerButtons.RightStick
            => $"{ControllerButtons.Name(button)} on its own: games use it too, so Hold for a second or more is safest.",
        _ => $"{ControllerButtons.Name(button)} on its own also does whatever it does in your game. A combination, or holding it for a while, is safer.",
    };

    static void PickFeature(Trigger t, DeviceFeature f, FeatureAction action, string? value)
    {
        t.Then = ThenKind.Device;
        t.DeviceId = f.DeviceId;
        t.DeviceName = f.DeviceName;
        t.Feature = f.Id;
        t.Action = action;
        t.Value = value;
        t.ProfileId = null;
    }

    /// <summary>From the Devices page: a new trigger that already knows what it changes. Its card asks for the type.</summary>
    public void AddForFeature(DeviceFeature f, FeatureAction action, string? value)
    {
        var t = new Trigger { Kind = TriggerKind.Unset };
        PickFeature(t, f, action, value);
        AllTriggers.Add(t);
        _expanded.Add(t.Id);
        _scrollTo = t.Id;
        Save();
    }

    /// <summary>Opens a trigger's row (from the Devices page).</summary>
    public void Open(string triggerId)
    {
        _expanded.Add(triggerId);
        _scrollTo = triggerId;
        Rebuild();
    }

    Button ProfileButton(Trigger t)
    {
        var target = _profiles.Profiles.FirstOrDefault(p => p.Id == t.ProfileId);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (target != null)
        {
            content.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(target.Color)), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = target.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) });
        }
        else content.Children.Add(new TextBlock { Text = "Pick a profile", Foreground = Res<Brush>("WarnBrush") });
        content.Children.Add(new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 9, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Res<Brush>("MutedBrush") });

        var button = new Button { Style = Res<Style>("GhostButton"), Padding = new Thickness(12, 6, 12, 6), Content = content };
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            if (_profiles.Profiles.Count == 0) menu.Items.Add(new MenuItem { Header = "Make a profile first", IsEnabled = false });
            foreach (var p in _profiles.Profiles)
            {
                var item = new MenuItem
                {
                    Header = p.Name,
                    Icon = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Color)) },
                    FontWeight = p.Id == t.ProfileId ? FontWeights.SemiBold : FontWeights.Normal,
                };
                item.Click += (_, _) => { t.ProfileId = p.Id; Save(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return button;
    }

    // ---------------- options and status ----------------

    FrameworkElement? Options(Trigger t, bool compact)
    {
        // A bite has no end to undo anything at.
        if (t.IsPicker || t.Kind is TriggerKind.Unset or TriggerKind.WowFishingBite) return null;
        var panel = new StackPanel { Margin = new Thickness(0, compact ? 8 : 0, 0, 0) };
        if (t.ChangesDevice)
        {
            // Only Big Picture has an end to undo it at; keys and buttons can use Toggle or While held instead.
            if (t.Kind != TriggerKind.SteamBigPicture) return null;
            panel.Children.Add(Option("Undo it when Big Picture closes", "Turns it back off (or on) when Big Picture closes. Toggles toggle again.",
                t.SwitchBack, v => t.SwitchBack = v, compact));
            return panel;
        }
        if (t.Kind == TriggerKind.SteamBigPicture)
        {
            panel.Children.Add(Option("Revert to the previous profile when Big Picture closes",
                "Puts the displays and audio back to how they were before Big Picture opened.",
                t.SwitchBack, v => t.SwitchBack = v, compact));
            panel.Children.Add(Option("Restart Big Picture so it fits the new screen",
                "Big Picture keeps the size of the screen it opened on. If it opens on your monitor and then moves to the TV, it can look stretched, small or cut off. "
                + "This closes and reopens it once the switch is done, so it fills the TV. Leave it off unless you see that problem.",
                t.ReopenBigPicture, v => t.ReopenBigPicture = v, compact));
        }
        else
        {
            if (t.Gesture == GestureKind.WhileHeld) return null; // it always goes back when let go
            panel.Children.Add(Option(t.UsesController ? "Do it again to switch back" : "Press again to switch back",
                $"The same {(t.UsesController ? "buttons" : "keys")} put the previous setup back, so one trigger works as a toggle.",
                t.SwitchBack, v => t.SwitchBack = v, compact));
        }
        return panel;
    }

    /// <summary>A switch with its label and explanation. Compact (rows) keeps the explanation narrower.</summary>
    UIElement Option(string label, string hint, bool value, Action<bool> set, bool compact)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, compact ? 8 : 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sw = new CheckBox
        {
            Style = Res<Style>("Switch"), IsChecked = value,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
        };
        sw.Click += (_, _) => { set(sw.IsChecked == true); Save(); };
        Place(grid, sw, 0);
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = hint, Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 2, 0, 0), MaxWidth = compact ? 380 : double.PositiveInfinity,
        });
        Place(grid, text, 1);
        return grid;
    }

    StackPanel StatusLine(Trigger t)
    {
        string text;
        if (t.Kind == TriggerKind.Unset) text = "Choose a type to finish setting this up";
        else if (!t.Enabled) text = "Off";
        else if (!t.HasInput) text = t.UsesController ? "Set the buttons to turn this on" : "Set a key to turn this on";
        else if (t.ChangesDevice && t.Feature == null) text = "Pick what to change to turn this on";
        else if (t.ChangesDevice && _features.Find(t.DeviceId, t.Feature) == null) text = $"{t.DeviceName ?? "The device"} isn't connected";
        else if (!t.IsPicker && !t.ChangesDevice && t.ProfileId == null) text = "Pick a profile to turn this on";
        else if (_engine.IsEngaged(t)) text = t.HasEnd ? "Switched. Reverts when Big Picture closes." : "Switched. Fire it again to go back.";
        else if (_engine.LastFired(t) is { } at) text = $"Last fired at {at:T}";
        else if (t.UsesController && _engine.ControllersConnected == 0) text = "Ready, once a controller connects";
        else if (t.IsPicker && _profiles.Profiles.Count == 0) text = "Make a profile to turn this on";
        else text = t.Kind switch
        {
            TriggerKind.SteamBigPicture => "Watching for Big Picture",
            TriggerKind.WowFishingBite => _engine.Wow.State == Integrations.WowListenState.Listening ? "Listening for the splash" : "Waiting for WoW",
            _ => "Ready",
        };

        bool live = t.Enabled && !_engine.Paused && (text is "Ready" or "Watching for Big Picture" or "Listening for the splash" || text.StartsWith("Last"));
        if (text.StartsWith("Ready, once")) live = false;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center,
            Fill = _engine.IsEngaged(t) ? new SolidColorBrush(Color.FromRgb(0x5B, 0xB8, 0xF5)) : Res<Brush>(live ? "SuccessBrush" : "FaintBrush"),
        });
        row.Children.Add(new TextBlock { Text = text, Style = Res<Style>("Muted"), Margin = new Thickness(8, 0, 0, 0) });
        return row;
    }

    // ---------------- new ----------------

    void New_Click(object sender, RoutedEventArgs e)
    {
        // A blank trigger straight away; its card asks for the type.
        var t = new Trigger
        {
            Kind = TriggerKind.Unset,
            ProfileId = _profiles.Profiles.Count == 1 ? _profiles.Profiles[0].Id : null,
        };
        AllTriggers.Add(t);
        _expanded.Add(t.Id);
        _scrollTo = t.Id;
        Save();
    }

    // ---------------- pieces ----------------

    TextBlock Label(string text, double top = 0) => new() { Text = text, Style = Res<Style>("Overline"), Margin = new Thickness(0, top, 0, 0) };
    TextBlock Note(string text) => new() { Text = text, Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 8, 0, 0) };
    UIElement Divider() => new Border { Height = 1, Background = Res<Brush>("LineBrush"), Margin = new Thickness(0, 16, 0, 14) };

    UIElement StateChip(string text, bool on) => new Border
    {
        Style = Res<Style>(on ? "BadgeInfo" : "Badge"), Margin = new Thickness(10, 0, 0, 0),
        Child = new TextBlock { Text = text },
    };

    static void Place(Grid g, UIElement e, int col)
    {
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    MenuItem MenuItem(string header, string glyph, Action action)
    {
        var item = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 13 } };
        item.Click += (_, _) => action();
        return item;
    }

    T Res<T>(string key) where T : class => (T)FindResource(key);
}
