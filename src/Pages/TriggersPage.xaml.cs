using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    readonly AppSettings _settings;
    Recording? _recording;
    string? _scrollTo;
    // Rows start collapsed to a one-line summary; these are the ones opened for editing.
    readonly HashSet<string> _expanded = [];

    sealed record Recording(Trigger Trigger, bool Combo, Button Button);

    List<Trigger> AllTriggers => _profiles.Data.Triggers;

    public TriggersPage(ProfileService profiles, TriggerEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _profiles = profiles;
        _engine = engine;
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
        TriggerKind.SteamBigPicture => ("Steam Big Picture", "", "Switch while Big Picture is open"),
        _ => ("New trigger", "", "Choose what sets it off"),
    };

    static readonly TriggerKind[] Kinds = [TriggerKind.Hotkey, TriggerKind.KeyGesture, TriggerKind.ProfilePicker, TriggerKind.SteamBigPicture];

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
                Child = new TextBlock { Text = Trigger.CategoryOf(t.Kind) == TriggerCategory.Integrations ? "Integration" : "Keys" },
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

        var flow = new WrapPanel { Margin = new Thickness(56, 16, 0, 0), Opacity = t.Enabled ? 1 : 0.5, IsEnabled = t.Enabled };
        flow.Children.Add(Block("WHEN", WhenEditor(t)));
        flow.Children.Add(new TextBlock
        {
            Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 14, Foreground = Res<Brush>("FaintBrush"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 28, 0),
        });
        flow.Children.Add(Block("THEN", ThenEditor(t)));
        if (Options(t, compact: true) is { } options) flow.Children.Add(Block("OPTIONS", options));
        stack.Children.Add(flow);
        return new Border { Style = Res<Style>("Card"), Padding = new Thickness(20, 16, 20, 16), Child = stack };
    }

    UIElement Block(string label, UIElement content)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 28, 6), MaxWidth = 520 };
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

    /// <summary>Shown on a new trigger: pick what kind it is, grouped into Keys and Integrations.</summary>
    UIElement TypePicker(Trigger t)
    {
        var panel = new StackPanel();
        foreach (var category in new[] { TriggerCategory.Keys, TriggerCategory.Integrations })
        {
            panel.Children.Add(Label(category == TriggerCategory.Keys ? "KEYS" : "INTEGRATIONS", top: category == TriggerCategory.Keys ? 0 : 14));
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
            TriggerKind.ProfilePicker => KeyNames.Describe(t),
            TriggerKind.SteamBigPicture => target == null ? "Not linked to a profile"
                : $"Switches to {target.Name} while it's open" + (t.SwitchBack ? ", then reverts" : ""),
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
            test.IsEnabled = t.Kind == TriggerKind.ProfilePicker ? _profiles.Profiles.Count > 0 : t.ProfileId != null;
            menu.Items.Add(test);
            menu.Items.Add(MenuItem("Change type", "", () =>
            {
                t.Kind = TriggerKind.Unset;
                t.Key = 0;
                t.Mods = KeyMods.None;
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
            case TriggerKind.ProfilePicker:
                var row = new WrapPanel();
                row.Children.Add(GestureChoice(t));
                var key = KeyButton(t, combo: false);
                key.Margin = new Thickness(10, 0, 0, 0);
                row.Children.Add(key);
                panel.Children.Add(row);
                if (t.Gesture == GestureKind.Hold) panel.Children.Add(HoldChoice(t));
                if (t.Kind == TriggerKind.ProfilePicker)
                    panel.Children.Add(Note("then press 1–9 within a couple of seconds to pick the profile with that number."));
                break;

            case TriggerKind.SteamBigPicture:
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = "Steam Big Picture opens", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(StateChip(_engine.BigPictureOpen ? "Open now" : "Closed", _engine.BigPictureOpen));
                panel.Children.Add(line);
                panel.Children.Add(Note("Works with any Steam install: it watches for the Big Picture window itself."));
                break;
        }
        return panel;
    }

    UIElement GestureChoice(Trigger t)
    {
        var box = new Border
        {
            Background = Res<Brush>("SurfaceBrush"), BorderBrush = Res<Brush>("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(3),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (g, label) in new[] { (GestureKind.DoubleTap, "Double tap"), (GestureKind.TripleTap, "Triple tap"), (GestureKind.Hold, "Hold") })
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
        _recording = null;
        _engine.Suspended = false;
        Rebuild();
    }

    void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not { } rec) return;
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
        if (t.Kind == TriggerKind.ProfilePicker)
        {
            if (_profiles.Profiles.Count == 0) { panel.Children.Add(Note("Make a profile first.")); return panel; }
            var chips = new WrapPanel();
            for (int i = 0; i < _profiles.Profiles.Count && i < 9; i++)
            {
                var p = _profiles.Profiles[i];
                var color = (Color)ColorConverter.ConvertFromString(p.Color);
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                chip.Children.Add(new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
                    BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = (i + 1).ToString(), FontWeight = FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                chip.Children.Add(new TextBlock { Text = p.Name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                chips.Children.Add(chip);
            }
            panel.Children.Add(chips);
            panel.Children.Add(Note("Numbers follow the column order on the Profiles page."));
            return panel;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "Switch to", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(ProfileButton(t));
        panel.Children.Add(row);
        return panel;
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
        if (t.Kind is TriggerKind.ProfilePicker or TriggerKind.Unset) return null;
        var panel = new StackPanel { Margin = new Thickness(0, compact ? 8 : 0, 0, 0) };
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
            panel.Children.Add(Option("Press again to switch back", "The same keys put the previous setup back, so one trigger works as a toggle.",
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
        else if (t.UsesKeyboard && t.Key == 0) text = "Set a key to turn this on";
        else if (t.Kind != TriggerKind.ProfilePicker && t.ProfileId == null) text = "Pick a profile to turn this on";
        else if (_engine.IsEngaged(t)) text = t.HasEnd ? "Switched. Reverts when Big Picture closes." : "Switched. Fire it again to go back.";
        else if (_engine.LastFired(t) is { } at) text = $"Last fired at {at:T}";
        else text = t.Kind == TriggerKind.SteamBigPicture ? "Watching for Big Picture" : "Ready";

        bool live = t.Enabled && !_engine.Paused && (text is "Ready" or "Watching for Big Picture" || text.StartsWith("Last"));
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
