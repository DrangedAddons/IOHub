using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOHub.Devices;
using IOHub.Profiles;
using IOHub.Triggers;
using IOHub.Ui;
using Trigger = IOHub.Triggers.Trigger;

namespace IOHub.Pages;

/// <summary>
/// Profiles as columns, devices as rows, grouped into sections. A section a profile leaves blank is one it
/// doesn't change. The grid is rebuilt from the model after every edit; it's a few hundred elements at most.
/// </summary>
public partial class ProfilesPage : UserControl
{
    enum Section { Triggers, Video, Output, Mic, Input, Camera }

    /// <param name="Now">What's active on this PC right now: "Main", "On", "Off", "Default" or "".</param>
    /// <param name="Disabled">Turned off in Windows; only listed when "Show disabled devices" is on or a profile uses it.</param>
    sealed record Row(Section Section, string Id, string Name, string Detail, string Glyph, bool Connected, string Now, bool Disabled = false);

    const double LabelWidth = 284, NowWidth = 84, ProfileWidth = 188, AddWidth = 156;
    const double HeadHeight = 92, SectionHeight = 64, RowHeight = 54, LgRowHeight = 128;

    static readonly Section[] Sections = [Section.Triggers, Section.Video, Section.Output, Section.Mic, Section.Input, Section.Camera];

    readonly DeviceHub _hub;
    readonly ProfileService _profiles;
    readonly AppSettings _settings;
    // Triggers and Displays start open; the long device lists start folded away.
    readonly HashSet<Section> _collapsed = [Section.Output, Section.Mic, Section.Input, Section.Camera];
    Profile? _pendingDelete;
    string? _focusNameOf;
    bool _editingName, _rebuildPending;
    string? _activeId; // the profile the PC is in, for the column highlight
    ColumnDrag? _drag;
    bool _stale;

    ProfileData _data => _profiles.Data;

    public ProfilesPage(DeviceHub hub, ProfileService profiles, AppSettings settings)
    {
        InitializeComponent();
        _hub = hub;
        _profiles = profiles;
        _settings = settings;
        ShowDisabledSwitch.IsChecked = settings.ShowDisabledDevices;
        _hub.Refreshed += Rebuild;
        _profiles.Changed += Rebuild; // triggers edited on the Triggers page
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _stale) Rebuild();
            // The same setting is switched from the Devices page too.
            if (IsVisible && ShowDisabledSwitch.IsChecked != _settings.ShowDisabledDevices)
                ShowDisabledSwitch.IsChecked = _settings.ShowDisabledDevices;
        };
        Rebuild();
    }

    void ShowDisabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return; // fires during InitializeComponent
        _settings.ShowDisabledDevices = ShowDisabledSwitch.IsChecked == true;
        _settings.Save();
        Rebuild();
    }

    bool ShowDisabled => _settings.ShowDisabledDevices;

    // ---------------- rows ----------------

    List<Row> RowsFor(Section section)
    {
        var rows = new List<Row>();
        switch (section)
        {
            case Section.Triggers:
                // Triggers that change a device rather than switch profile have nothing to pick here.
                foreach (var t in _data.Triggers.Where(t => t.Kind != TriggerKind.Unset && !t.ChangesDevice))
                {
                    var (kindTitle, kindGlyph, _) = TriggersPage.KindInfo(t.Kind);
                    // Integrations are named after what they watch, so the detail says what sort of trigger it is instead.
                    var detail = Trigger.CategoryOf(t.Kind) == TriggerCategory.Integrations ? "Integration" : kindTitle;
                    rows.Add(new(section, t.Id, KeyNames.Describe(t), t.Enabled ? detail : $"{detail} · Off", kindGlyph, true, t.Enabled ? "On" : "Off"));
                }
                return rows; // triggers aren't devices: nothing to remember while unplugged

            case Section.Video:
                foreach (var d in _hub.Displays)
                    rows.Add(new(section, d.Id, d.Name,
                        d.IsActive ? $"{d.Connection} · {d.Width} × {d.Height}" : $"{d.Connection} · Standby",
                        d.Glyph, true, d.IsPrimary ? "Main" : d.IsActive ? "On" : "Off"));
                break;

            case Section.Output:
            case Section.Mic:
                var flow = section == Section.Output ? AudioFlow.Output : AudioFlow.Input;
                var referenced = ReferencedIds(section);
                // Disabled endpoints are hidden unless asked for, or a profile already points at one.
                foreach (var e in _hub.Audio.Where(e => e.Flow == flow && (ShowDisabled || e.State != "Disabled" || referenced.Contains(e.Id))))
                    rows.Add(new(section, e.Id, e.Name, e.IsActive ? e.Adapter : $"{e.State} · {e.Adapter}",
                        e.Glyph, true, e.IsDefault ? "Default" : "", e.State == "Disabled"));
                break;

            case Section.Input:
                var input = _hub.Input;
                foreach (var i in input.Controllers.Concat(input.Keyboards).Concat(input.Mice))
                    rows.Add(new(section, i.Id, i.Name, DisplayDevice.Join(i.Kind.ToString(), i.Vendor, i.Connection), i.Glyph, true, ""));
                break;

            case Section.Camera:
                var usedCameras = ReferencedIds(section);
                foreach (var c in _hub.Cameras.Where(c => ShowDisabled || c.IsEnabled || usedCameras.Contains(c.Id)))
                    rows.Add(new(section, c.Id, c.Name, DisplayDevice.Join(c.Subtitle, c.Connection, c.IsEnabled ? "" : "Disabled"), c.Glyph, true, "", !c.IsEnabled));
                break;
        }

        // Devices a profile uses that aren't connected now still get a row, so the profile stays editable.
        foreach (var id in ReferencedIds(section).Where(id => rows.All(r => r.Id != id)))
        {
            var known = _data.Devices.FirstOrDefault(k => k.Id == id);
            rows.Add(new(section, id, known?.Name ?? "Unknown device", "Not connected", DefaultGlyph(section), false, ""));
        }
        return rows;
    }

    HashSet<string> ReferencedIds(Section section) => section switch
    {
        Section.Triggers => [],
        Section.Video => _data.Profiles.SelectMany(p => p.Displays).ToHashSet(),
        Section.Output => _data.Profiles.Select(p => p.AudioOutput).OfType<string>().ToHashSet(),
        Section.Mic => _data.Profiles.Select(p => p.AudioInput).OfType<string>().ToHashSet(),
        Section.Camera => _data.Profiles.Select(p => p.Camera).OfType<string>().ToHashSet(),
        _ => _data.Profiles.Select(p => p.MainInput).OfType<string>().ToHashSet(),
    };

    static string DefaultGlyph(Section s) => s switch
    {
        Section.Triggers => "\uE945",
        Section.Video => "",
        Section.Output => "",
        Section.Mic => "",
        Section.Camera => "",
        _ => "",
    };

    static (string Title, string Glyph, string Hint) SectionInfo(Section s) => s switch
    {
        Section.Triggers => ("Triggers", "\uE945", "Which profile each trigger switches to."),
        Section.Video => ("Displays", "", "Ticked turn on, unticked turn off. Star = main."),
        Section.Output => ("Audio output", "", "Where sound plays."),
        Section.Mic => ("Microphone", "", "What apps record from."),
        Section.Camera => ("Camera", "", "The webcam or capture device to use."),
        _ => ("Main input", "", "What you'll mainly control things with."),
    };

    // ---------------- building the grid ----------------

    void Rebuild()
    {
        // Hidden pages (including everything while the app sits in the tray) don't rebuild; they catch up when shown.
        if (!IsVisible) { _stale = true; return; }
        _stale = false;
        // Rebuilding would throw away a name that's halfway through being typed, a column mid-drag, or the colour picker.
        if (_editingName || _drag != null || _colorPopup?.IsOpen == true) { _rebuildPending = true; return; }
        _rebuildPending = false;

        bool any = _data.Profiles.Count > 0;
        MatrixCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        foreach (var g in new[] { HeadGrid, BodyGrid })
        {
            g.Children.Clear();
            g.ColumnDefinitions.Clear();
            g.RowDefinitions.Clear();
        }
        if (!any) return;
        _activeId = ProfileMatcher.Active(_profiles, _hub)?.Id;

        var widths = new List<double> { LabelWidth, NowWidth };
        widths.AddRange(_data.Profiles.Select(_ => ProfileWidth));
        widths.Add(AddWidth);
        foreach (var w in widths)
        {
            HeadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        }
        int columns = widths.Count;

        // Header row
        HeadGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeadHeight) });
        Place(HeadGrid, new TextBlock { Text = "DEVICES", Style = Res<Style>("Overline"), Margin = new Thickness(22, 0, 0, 14), VerticalAlignment = VerticalAlignment.Bottom }, 0, 0);
        Place(HeadGrid, new TextBlock
        {
            Text = "NOW", Style = Res<Style>("Overline"), Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = "What's active on this PC right now",
        }, 0, 1);
        for (int i = 0; i < _data.Profiles.Count; i++)
            Place(HeadGrid, ProfileHeader(_data.Profiles[i]), 0, 2 + i);
        Place(HeadGrid, AddColumn(), 0, columns - 1);

        // Body
        foreach (var section in Sections)
        {
            var rows = RowsFor(section);
            int r = AddRow(SectionHeight);
            Place(BodyGrid, new Border { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x24)) }, r, 0, columns);
            Place(BodyGrid, new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Res<Brush>("LineBrush") }, r, 0, columns);
            Place(BodyGrid, SectionLabel(section, rows.Count), r, 0);
            for (int i = 0; i < _data.Profiles.Count; i++)
                Place(BodyGrid, SectionCell(_data.Profiles[i], section, rows), r, 2 + i);

            if (_collapsed.Contains(section)) continue;
            if (rows.Count == 0)
            {
                int er = AddRow(RowHeight);
                Place(BodyGrid, new TextBlock
                {
                    Text = section == Section.Triggers ? "No triggers yet. Add them on the Triggers page." : "Nothing connected here right now.",
                    Style = Res<Style>("Muted"),
                    Margin = new Thickness(62, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                }, er, 0, columns);
            }
            foreach (var row in rows)
            {
                int dr = AddRow(RowHeight);
                if (row.Disabled)
                {
                    // A muted red wash plus an edge marker, so disabled rows read as different at a glance.
                    Place(BodyGrid, new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x16, 0xF0, 0x6A, 0x6A)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xF0, 0x6A, 0x6A)),
                        BorderThickness = new Thickness(3, 0, 0, 0),
                    }, dr, 0, columns);
                }
                Place(BodyGrid, new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Res<Brush>("LineBrush"), Opacity = 0.6 }, dr, 0, columns);
                Place(BodyGrid, RowLabel(row), dr, 0);
                Place(BodyGrid, NowCell(row), dr, 1);
                for (int i = 0; i < _data.Profiles.Count; i++)
                    Place(BodyGrid, DeviceCell(_data.Profiles[i], row), dr, 2 + i);

                // An LG TV gets a row of its own for power control, under its display.
                if (row.Section == Section.Video && _data.LgTvs.TryGetValue(row.Id, out var tvLink) && !tvLink.NotATv
                    // Only under LG displays. A TV that's off isn't detected, so an undetected one keeps its row.
                    && _hub.Displays.FirstOrDefault(d => d.Id == row.Id) is not { IsLg: false })
                {
                    int lr = AddRow(LgRowHeight);
                    Place(BodyGrid, new Border { Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)) }, lr, 0, columns);
                    Place(BodyGrid, new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Res<Brush>("LineBrush"), Opacity = 0.6 }, lr, 0, columns);
                    Place(BodyGrid, LgLabel(row, tvLink), lr, 0);
                    for (int i = 0; i < _data.Profiles.Count; i++)
                        Place(BodyGrid, LgCell(_data.Profiles[i], row, tvLink), lr, 2 + i);
                }
            }
        }

        // Column dividers on top, so the section bands don't hide them.
        for (int c = 1; c < columns; c++)
        {
            foreach (var g in new[] { HeadGrid, BodyGrid })
            {
                var line = new Border
                {
                    BorderBrush = Res<Brush>("LineBrush"), BorderThickness = new Thickness(1, 0, 0, 0),
                    IsHitTestVisible = false, Opacity = c == 1 || c == columns - 1 ? 0.6 : 1,
                };
                Panel.SetZIndex(line, 10);
                Place(g, line, 0, c, 1, Math.Max(1, g.RowDefinitions.Count));
            }
        }

        // The active profile's column: a soft blue wash with blue edges, the same blue as the Overview's live wire.
        int activeCol = _data.Profiles.FindIndex(p => p.Id == _activeId);
        if (activeCol >= 0)
        {
            foreach (var g in new[] { HeadGrid, BodyGrid })
            {
                var wash = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x10, 0x5B, 0xB8, 0xF5)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x5B, 0xB8, 0xF5)),
                    BorderThickness = new Thickness(1, 0, 1, 0),
                    IsHitTestVisible = false,
                };
                Panel.SetZIndex(wash, 11); // over the dividers it replaces
                Place(g, wash, 0, 2 + activeCol, 1, Math.Max(1, g.RowDefinitions.Count));
            }
        }
    }

    int AddRow(double height)
    {
        BodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        return BodyGrid.RowDefinitions.Count - 1;
    }

    static void Place(Grid grid, UIElement element, int row, int col, int colSpan = 1, int rowSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, col);
        Grid.SetColumnSpan(element, colSpan);
        Grid.SetRowSpan(element, rowSpan);
        grid.Children.Add(element);
    }

    T Res<T>(string key) where T : class => (T)FindResource(key);

    static SolidColorBrush BrushOf(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // ---------------- header cells ----------------

    UIElement ProfileHeader(Profile p)
    {
        var brush = BrushOf(p.Color);
        // Transparent background so the whole header is a drag handle, not just the text.
        var root = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Drag to reorder · double-click to rename" };
        root.Children.Add(new Border
        {
            Height = 3, CornerRadius = new CornerRadius(0, 0, 3, 3), Background = brush,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 0, 14, 0),
        });
        var grip = new TextBlock
        {
            Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, Foreground = Res<Brush>("FaintBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0),
            Opacity = 0, IsHitTestVisible = false,
        };
        root.Children.Add(grip);
        root.MouseEnter += (_, _) => grip.Opacity = 1;
        root.MouseLeave += (_, _) => grip.Opacity = 0;

        // Dot on the left, menu on the right, name centred between them.
        var content = new Grid { Margin = new Thickness(8, 22, 8, 10) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        var title = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Place(content, title, 0, 1);

        var dot = new Border
        {
            Width = 20, Height = 20, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = "Change colour", VerticalAlignment = VerticalAlignment.Center,
            Child = new Ellipse { Width = 11, Height = 11, Fill = brush },
        };
        dot.MouseLeftButtonDown += (_, e) => e.Handled = true; // not the start of a column drag
        dot.MouseLeftButtonUp += (_, _) => ShowColorPicker(p, dot);
        dot.HorizontalAlignment = HorizontalAlignment.Left;
        Place(content, dot, 0, 0);

        // Not clickable until renaming starts, so a press on the name begins a drag instead.
        var name = new TextBox
        {
            Style = Res<Style>("InlineEdit"), Text = p.Name, TextAlignment = TextAlignment.Center, MaxWidth = ProfileWidth - 72,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        name.GotKeyboardFocus += (_, _) => _editingName = true;
        name.LostKeyboardFocus += (_, _) =>
        {
            _editingName = false;
            name.IsHitTestVisible = false;
            var text = name.Text.Trim();
            if (text.Length > 0 && text != p.Name) { p.Name = text; Save(); }
            name.Text = p.Name;
            if (_rebuildPending) Dispatcher.BeginInvoke(Rebuild);
        };
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) name.Text = p.Name;
            if (e.Key is Key.Enter or Key.Escape) { Keyboard.ClearFocus(); e.Handled = true; }
        };
        title.Children.Add(name);
        if (_focusNameOf == p.Id)
        {
            _focusNameOf = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                BodyScroll.ScrollToRightEnd();
                StartRename(name);
            });
        }

        var menu = new Button { Style = Res<Style>("IconButton"), Content = "", ToolTip = "More" };
        menu.Click += (_, _) => ShowProfileMenu(p, menu, () => StartRename(name));
        Place(content, menu, 0, 2);

        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { StartRename(name); e.Handled = true; return; }
            if (name.IsKeyboardFocused) return;
            _drag = new ColumnDrag(p, root, e.GetPosition(HeadGrid).X);
        };
        root.MouseMove += (_, e) => DragMove(root, e);
        root.MouseLeftButtonUp += (_, _) => DragEnd(root, commit: true);
        root.LostMouseCapture += (_, _) => DragEnd(root, commit: false);

        // The column says what the profile switches; the header only needs to say if it's the live one.
        if (p.Id == _activeId)
            Place(content, new TextBlock
            {
                Text = "Active", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xB8, 0xF5)), HorizontalAlignment = HorizontalAlignment.Center,
            }, 1, 0, 3);

        root.Children.Add(content);
        return root;
    }

    Popup? _colorPopup;

    /// <summary>The colour dot's popup: preset swatches, a hex code box, and Custom… for Windows' full colour dialog.</summary>
    void ShowColorPicker(Profile p, FrameworkElement anchor)
    {
        void Apply(string hex)
        {
            if (_colorPopup != null) _colorPopup.IsOpen = false;
            hex = hex.ToUpperInvariant();
            if (hex == p.Color.ToUpperInvariant()) return;
            p.Color = hex;
            Commit();
        }

        var panel = new StackPanel { Width = 6 * 32 };
        panel.Children.Add(new TextBlock { Text = "PROFILE COLOUR", Style = Res<Style>("Overline"), Margin = new Thickness(0, 0, 0, 10) });

        // A custom colour shows as an extra swatch so it can be picked again after trying a preset.
        bool custom = !ProfileColors.Presets.Contains(p.Color, StringComparer.OrdinalIgnoreCase);
        var swatches = new WrapPanel();
        foreach (var hex in custom ? [.. ProfileColors.Presets, p.Color] : ProfileColors.Presets)
            swatches.Children.Add(Swatch(hex, hex.Equals(p.Color, StringComparison.OrdinalIgnoreCase), () => Apply(hex)));
        panel.Children.Add(swatches);
        panel.Children.Add(new Border { Height = 1, Background = Res<Brush>("LineBrush"), Margin = new Thickness(0, 6, 0, 12) });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var preview = new Ellipse { Width = 14, Height = 14, Fill = BrushOf(p.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Place(row, preview, 0, 0);
        var hexBox = new TextBox { Style = Res<Style>("Field"), Text = p.Color.ToUpperInvariant(), MaxLength = 7, ToolTip = "A colour code like #7B8CFF. Enter applies it." };
        hexBox.TextChanged += (_, _) =>
        {
            bool ok = TryHex(hexBox.Text, out var hex);
            if (ok) preview.Fill = BrushOf(hex);
            hexBox.BorderBrush = ok || hexBox.Text.Length < 7 ? Res<Brush>("Line2Brush") : Res<Brush>("DangerBrush");
        };
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && TryHex(hexBox.Text, out var hex)) { Apply(hex); e.Handled = true; }
            if (e.Key == Key.Escape && _colorPopup != null) _colorPopup.IsOpen = false;
        };
        Place(row, hexBox, 0, 1);
        var more = new Button { Style = Res<Style>("GhostButton"), Content = "Custom…", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Pick any colour" };
        more.Click += (_, _) =>
        {
            if (_colorPopup != null) _colorPopup.IsOpen = false;
            var current = (Color)ColorConverter.ConvertFromString(p.Color);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true, AnyColor = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
                // The dialog's "custom colours" row starts with the presets (it wants 0x00BBGGRR).
                CustomColors = ProfileColors.Presets.Select(h => (Color)ColorConverter.ConvertFromString(h)).Select(c => c.B << 16 | c.G << 8 | c.R).ToArray(),
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                Apply($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        };
        Place(row, more, 0, 2);
        panel.Children.Add(row);

        _colorPopup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, HorizontalOffset = -8, VerticalOffset = 6,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x2B)), BorderBrush = Res<Brush>("Line2Brush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 12, 12), // room for the shadow
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.5 },
                Child = panel,
            },
        };
        _colorPopup.Closed += (_, _) => { if (_rebuildPending) Dispatcher.BeginInvoke(Rebuild); };
        _colorPopup.IsOpen = true;
    }

    UIElement Swatch(string hex, bool selected, Action pick)
    {
        var swatch = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = BrushOf(hex), Margin = new Thickness(0, 0, 6, 6),
            BorderThickness = new Thickness(2), BorderBrush = selected ? Brushes.White : Brushes.Transparent,
            Cursor = Cursors.Hand, ToolTip = hex.ToUpperInvariant(),
        };
        swatch.MouseEnter += (_, _) => { if (!selected) swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)); };
        swatch.MouseLeave += (_, _) => { if (!selected) swatch.BorderBrush = Brushes.Transparent; };
        swatch.MouseLeftButtonUp += (_, _) => pick();
        return swatch;
    }

    static bool TryHex(string text, out string hex)
    {
        hex = text.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        return hex.Length == 7 && hex.Skip(1).All(Uri.IsHexDigit);
    }

    UIElement AddColumn()
    {
        var button = new Button
        {
            Style = Res<Style>("AddColumnButton"), Margin = new Thickness(14, 16, 14, 14),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = Res<FontFamily>("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "New profile", Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold },
                },
            },
        };
        button.Click += New_Click;
        return button;
    }

    static void StartRename(TextBox name)
    {
        name.IsHitTestVisible = true;
        name.Focus();
        name.SelectAll();
    }

    void ShowProfileMenu(Profile p, FrameworkElement anchor, Action rename)
    {
        int index = _data.Profiles.IndexOf(p);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuItem("Rename", "", true, rename));
        menu.Items.Add(MenuItem("Duplicate", "", true, () =>
        {
            var copy = p.Clone();
            copy.Name = UniqueName(p.Name + " copy");
            _data.Profiles.Insert(index + 1, copy);
            Commit();
        }));
        menu.Items.Add(new Separator());
        var delete = MenuItem("Delete", "", true, () => AskDelete(p));
        delete.Foreground = Res<Brush>("DangerBrush");
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    MenuItem MenuItem(string header, string glyph, bool enabled, Action action)
    {
        var item = new MenuItem
        {
            Header = header, IsEnabled = enabled,
            Icon = new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 13 },
        };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------------- dragging columns ----------------

    sealed class ColumnDrag(Profile profile, FrameworkElement header, double startX)
    {
        public Profile Profile { get; } = profile;
        public FrameworkElement Header { get; } = header;
        public double StartX { get; } = startX;
        public bool Started { get; set; }
        public int Slot { get; set; } = -1;
        public List<Border> Markers { get; } = [];
    }

    const double ProfilesLeft = LabelWidth + NowWidth;

    void DragMove(FrameworkElement header, MouseEventArgs e)
    {
        if (_drag == null || _drag.Header != header) return;
        if (e.LeftButton != MouseButtonState.Pressed) { DragEnd(header, commit: false); return; }
        double x = e.GetPosition(HeadGrid).X;

        if (!_drag.Started)
        {
            if (Math.Abs(x - _drag.StartX) < 5) return;
            _drag.Started = true;
            header.CaptureMouse();
            header.Cursor = Cursors.SizeWE;
            header.Opacity = 0.92;
            Panel.SetZIndex(header, 50);
            header.RenderTransform = new TranslateTransform();
            if (header is Grid g) g.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x23, 0x27, 0x31));
            header.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black };

            // An insertion line through the header and every row.
            foreach (var grid in new[] { HeadGrid, BodyGrid })
            {
                var marker = new Border
                {
                    Width = 3, CornerRadius = new CornerRadius(1.5), Background = Res<Brush>("AccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false,
                };
                Panel.SetZIndex(marker, 40);
                Place(grid, marker, 0, 0, grid.ColumnDefinitions.Count, Math.Max(1, grid.RowDefinitions.Count));
                _drag.Markers.Add(marker);
            }
        }

        // Keep the header inside the profile columns.
        int from = _data.Profiles.IndexOf(_drag.Profile);
        double minDx = -from * ProfileWidth, maxDx = (_data.Profiles.Count - 1 - from) * ProfileWidth;
        ((TranslateTransform)header.RenderTransform).X = Math.Clamp(x - _drag.StartX, minDx - 20, maxDx + 20);

        // The gap between columns nearest the cursor.
        _drag.Slot = Math.Clamp((int)Math.Round((x - ProfilesLeft) / ProfileWidth), 0, _data.Profiles.Count);
        foreach (var marker in _drag.Markers)
            marker.Margin = new Thickness(ProfilesLeft + _drag.Slot * ProfileWidth - 1.5, 0, 0, 0);
    }

    void DragEnd(FrameworkElement header, bool commit)
    {
        var drag = _drag;
        if (drag == null || drag.Header != header) return;
        _drag = null;
        if (header.IsMouseCaptured) header.ReleaseMouseCapture(); // raises LostMouseCapture, which lands here with _drag already null
        if (!drag.Started) { if (_rebuildPending) Rebuild(); return; }

        int from = _data.Profiles.IndexOf(drag.Profile);
        int to = drag.Slot > from ? drag.Slot - 1 : drag.Slot;
        if (commit && drag.Slot >= 0 && to != from)
        {
            _data.Profiles.RemoveAt(from);
            _data.Profiles.Insert(to, drag.Profile);
            Commit();
        }
        else Rebuild();
    }

    // ---------------- section rows ----------------

    UIElement SectionLabel(Section section, int count)
    {
        var (title, glyph, hint) = SectionInfo(section);
        bool collapsed = _collapsed.Contains(section);

        var grid = new Grid { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var chevron = new Button
        {
            Style = Res<Style>("IconButton"), Content = collapsed ? "" : "", FontSize = 10,
            ToolTip = collapsed ? "Show devices" : "Hide devices",
        };
        chevron.Click += (_, _) =>
        {
            if (!_collapsed.Remove(section)) _collapsed.Add(section);
            Rebuild();
        };
        Place(grid, chevron, 0, 0);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("IconFont"), FontSize = 14, Foreground = Res<Brush>("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SectionTitle"), FontSize = 14, Margin = new Thickness(9, 0, 0, 0) });
        titleRow.Children.Add(new TextBlock { Text = count.ToString(), Style = Res<Style>("CountPill") });
        Place(grid, titleRow, 0, 1);

        Place(grid, new TextBlock
        {
            Text = hint, Style = Res<Style>("Muted"), FontSize = 11.5, ToolTip = hint,
            Margin = new Thickness(29, 3, 0, 0),
        }, 1, 1);
        return grid;
    }

    UIElement SectionCell(Profile p, Section section, List<Row> rows)
    {
        var brush = BrushOf(p.Color);
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        string state;
        ToggleButton action;
        if (section == Section.Triggers)
        {
            var mine = _data.Triggers.Where(t => !t.IsPicker && t.ProfileId == p.Id).ToList();
            state = mine.Count switch { 0 => "None", 1 => "1 trigger", _ => $"{mine.Count} triggers" };
            action = new ToggleButton
            {
                Style = Res<Style>("PillToggle"), Content = "Clear", Background = brush,
                Visibility = mine.Count == 0 ? Visibility.Hidden : Visibility.Visible, ToolTip = $"Unlink every trigger from {p.Name}",
            };
            action.Click += (_, _) => { foreach (var t in mine) t.ProfileId = null; Commit(); };
        }
        else if (section == Section.Video)
        {
            var connected = rows.Where(r => r.Connected).Select(r => r.Id).ToList();
            bool all = connected.Count > 0 && connected.All(p.Displays.Contains);
            state = p.Displays.Count == 0 ? "Unchanged" : $"{p.Displays.Count} on";
            action = new ToggleButton
            {
                Style = Res<Style>("PillToggle"), Content = "All", IsChecked = all, Background = brush,
                IsEnabled = connected.Count > 0, ToolTip = all ? "Leave displays unchanged" : "Turn every connected display on",
            };
            action.Click += (_, _) =>
            {
                p.DisplaysConfirmed = false;
                if (all)
                {
                    p.Displays.Clear();
                    p.PrimaryDisplay = null;
                }
                else
                {
                    p.Displays = p.Displays.Union(connected).ToList();
                    if (p.PrimaryDisplay == null || !p.Displays.Contains(p.PrimaryDisplay))
                        p.PrimaryDisplay = _hub.Displays.FirstOrDefault(d => d.IsPrimary)?.Id is { } main && p.Displays.Contains(main)
                            ? main : p.Displays[0];
                }
                Commit();
            };
        }
        else
        {
            // One default device / one main input, so there's no "all" here; the pill clears the choice.
            var chosen = Selected(p, section);
            state = chosen == null ? "Unchanged" : rows.FirstOrDefault(r => r.Id == chosen)?.Name ?? "Set";
            action = new ToggleButton
            {
                Style = Res<Style>("PillToggle"), Content = "Clear", Background = brush,
                Visibility = chosen == null ? Visibility.Hidden : Visibility.Visible, ToolTip = "Leave this unchanged",
            };
            action.Click += (_, _) => { SetSelected(p, section, null); Commit(); };
        }

        bool unchanged = state is "Unchanged" or "None";
        panel.Children.Add(new TextBlock
        {
            Text = state, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ProfileWidth - 24,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5),
            Foreground = Res<Brush>(unchanged ? "FaintBrush" : "SubtleBrush"),
            FontStyle = unchanged ? FontStyles.Italic : FontStyles.Normal,
            FontWeight = unchanged ? FontWeights.Normal : FontWeights.SemiBold,
        });
        action.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(action);
        return panel;
    }

    static string? Selected(Profile p, Section s) => s switch
    {
        Section.Output => p.AudioOutput,
        Section.Mic => p.AudioInput,
        Section.Input => p.MainInput,
        Section.Camera => p.Camera,
        _ => null,
    };

    static void SetSelected(Profile p, Section s, string? id)
    {
        switch (s)
        {
            case Section.Output: p.AudioOutput = id; break;
            case Section.Mic: p.AudioInput = id; break;
            case Section.Input: p.MainInput = id; break;
            case Section.Camera: p.Camera = id; break;
        }
    }

    // ---------------- device rows ----------------

    UIElement RowLabel(Row row)
    {
        var grid = new Grid { Margin = new Thickness(44, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tile = new Border
        {
            Style = Res<Style>("IconTileSmall"), Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
            Child = new TextBlock { Text = row.Glyph, Style = Res<Style>("Icon"), FontSize = 14 },
        };
        Place(grid, tile, 0, 0);

        var text = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Name, Style = Res<Style>("ItemTitle"), FontSize = 13, ToolTip = row.Name });
        text.Children.Add(new TextBlock
        {
            Text = row.Detail, Style = Res<Style>("Muted"), FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0), ToolTip = row.Detail,
            Foreground = row.Disabled ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x8A)) : Res<Brush>(row.Connected ? "MutedBrush" : "WarnBrush"),
        });
        Place(grid, text, 0, 1);

        if (!row.Connected || row.Disabled) tile.Opacity = 0.5;
        return grid;
    }

    UIElement NowCell(Row row)
    {
        var (text, brush) = row.Now switch
        {
            "Main" => ("Main", "AccentBrush"),
            "On" => ("On", "SuccessBrush"),
            "Default" => ("Default", "SuccessBrush"),
            "Off" => ("Off", "FaintBrush"),
            _ => ("", "FaintBrush"),
        };
        if (text == "") return new Border();
        return new TextBlock
        {
            Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>(brush),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    UIElement DeviceCell(Profile p, Row row)
    {
        var brush = BrushOf(p.Color);
        if (row.Section == Section.Triggers) return TriggerCell(p, row, brush);
        if (row.Section == Section.Video)
        {
            bool on = p.Displays.Contains(row.Id);
            bool main = on && p.PrimaryDisplay == row.Id;
            var grid = new Grid();
            var check = new ToggleButton
            {
                Style = Res<Style>("MatrixCheck"), IsChecked = on, Background = brush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = on ? $"On in {p.Name}" : $"Turn on in {p.Name}",
            };
            check.Click += (_, _) =>
            {
                p.DisplaysConfirmed = false;
                if (on)
                {
                    p.Displays.Remove(row.Id);
                    if (p.PrimaryDisplay == row.Id) p.PrimaryDisplay = p.Displays.FirstOrDefault();
                }
                else
                {
                    p.Displays.Add(row.Id);
                    p.PrimaryDisplay ??= row.Id;
                }
                Commit();
            };
            grid.Children.Add(check);

            // Sits just right of the tick box; only there when the display is on.
            var star = new ToggleButton
            {
                Style = Res<Style>("StarToggle"), IsChecked = main, Background = brush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(62, 0, 0, 0), Visibility = on ? Visibility.Visible : Visibility.Hidden,
                ToolTip = main ? "Main display" : "Make this the main display",
            };
            star.Click += (_, _) => { p.PrimaryDisplay = row.Id; p.DisplaysConfirmed = false; Commit(); };
            grid.Children.Add(star);
            return grid;
        }

        bool selected = Selected(p, row.Section) == row.Id;
        var radio = new ToggleButton
        {
            Style = Res<Style>("MatrixRadio"), IsChecked = selected, Background = brush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = selected ? "Click again to leave this unchanged" : $"Use in {p.Name}",
        };
        radio.Click += (_, _) => { SetSelected(p, row.Section, selected ? null : row.Id); Commit(); };
        return radio;
    }

    /// <summary>
    /// A trigger switches to one profile, so its row works like a radio across the columns. The profile picker
    /// reaches every profile by number instead; its row just shows the number.
    /// </summary>
    UIElement TriggerCell(Profile p, Row row, Brush brush)
    {
        var t = _data.Triggers.FirstOrDefault(x => x.Id == row.Id);
        if (t == null) return new Border();
        if (t.IsPicker && !t.UsesController)
        {
            int n = _data.Profiles.IndexOf(p) + 1;
            return new TextBlock
            {
                Text = n <= 9 ? n.ToString() : "—", FontWeight = FontWeights.SemiBold, Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = n <= 9 ? $"Press {n} in the profile picker for {p.Name}" : "The picker only reaches the first nine profiles",
            };
        }
        if (t.IsPicker && t.UsesController)
        {
            int i = _data.Profiles.IndexOf(p);
            var picks = ControllerButtons.PickerButtons;
            return new TextBlock
            {
                Text = i < picks.Length ? ControllerButtons.Name(picks[i]) : "—", FontWeight = FontWeights.SemiBold, Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = i < picks.Length ? $"Press {ControllerButtons.Name(picks[i])} in the controller's profile picker for {p.Name}"
                                           : $"The controller's picker only reaches the first {picks.Length} profiles",
            };
        }
        bool selected = t.ProfileId == p.Id;
        var radio = new ToggleButton
        {
            Style = Res<Style>("MatrixRadio"), IsChecked = selected, Background = brush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = selected ? "Click again to unlink" : $"Switch to {p.Name} with this trigger",
        };
        radio.Click += (_, _) => { t.ProfileId = selected ? null : p.Id; Commit(); };
        return radio;
    }

    // ---------------- LG TV power ----------------

    // Display ids with a pairing in progress, so the row can say so and a second click doesn't start another.
    readonly HashSet<string> _pairing = [];
    /// <summary>TVs being turned on or off from their menu, with what the button says meanwhile.</summary>
    readonly Dictionary<string, string> _tvBusy = [];

    /// <summary>
    /// The label for an LG TV's power row: which TV it is and where, or a Pair button. The menu has the rest
    /// (try it, pair again, forget, not a TV).
    /// </summary>
    UIElement LgLabel(Row row, LgTvLink tv)
    {
        var stack = new StackPanel { Margin = new Thickness(87, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "LG TV power", Style = Res<Style>("ItemTitle"), FontSize = 12.5 });

        string text = _pairing.Contains(row.Id) ? "Accept the prompt on the TV…"
            : _tvBusy.TryGetValue(row.Id, out var busy) ? busy
            : tv.IsPaired ? $"{(tv.Name != "" ? tv.Name : "LG TV")} · {tv.Ip}  ▾"
            : "Pair this TV  ▾";
        var button = new Button
        {
            Style = Res<Style>("GhostButton"), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !_pairing.Contains(row.Id) && !_tvBusy.ContainsKey(row.Id),
            Content = new TextBlock { Text = text, FontSize = 11.5, Foreground = Res<Brush>(tv.IsPaired ? "MutedBrush" : "AccentBrush") },
            ToolTip = tv.IsPaired ? "Paired: IO Hub can turn this TV on and off" + (tv.Mac == "" ? " (waking it from fully off needs the TV's network address; pair again while it's on)" : "")
                                  : "Connect IO Hub to the TV over your network. The TV asks once to allow it.",
        };
        button.Click += async (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            if (tv.IsPaired)
            {
                menu.Items.Add(MenuItem("Turn it on now", "", true, () => _ = TryTv(row, tv, t => LgWebOs.PowerOnAsync(t, s => SwitchLog.Write($"LG {t.Name}: {s}")), "on")));
                menu.Items.Add(MenuItem("Turn it off now", "", true, () => _ = TryTv(row, tv, LgWebOs.PowerOffAsync, "off")));
                menu.Items.Add(new Separator());
            }
            // Look for TVs while the menu builds; it's a two-second listen.
            var searching = new MenuItem { Header = "Looking for LG TVs on the network…", IsEnabled = false };
            menu.Items.Add(searching);
            menu.Items.Add(MenuItem("Enter its IP address…", "", true, () => AskForIp(row, tv)));
            menu.Items.Add(new Separator());
            if (tv.IsPaired)
                menu.Items.Add(MenuItem("Forget this TV", "", true, () => { tv.ClientKey = ""; tv.Ip = ""; tv.Mac = ""; Commit(); }));
            menu.Items.Add(MenuItem("This isn't a TV", "", true, () => { tv.NotATv = true; Commit(); }));
            menu.IsOpen = true;

            var found = await LgWebOs.DiscoverAsync(TimeSpan.FromSeconds(2));
            int at = menu.Items.IndexOf(searching);
            if (at < 0) return; // menu closed meanwhile
            menu.Items.Remove(searching);
            // The search only finds TVs to pair. The paired one is reached by its address whether or not it shows up here.
            if (found.Count == 0)
                menu.Items.Insert(at, new MenuItem { Header = tv.IsPaired ? "No other LG TVs found to pair" : "No LG TVs answered (is it on?)", IsEnabled = false });
            foreach (var f in found)
                menu.Items.Insert(at++, MenuItem(f.Ip == tv.Ip && tv.IsPaired ? $"Pair again with {f.Name}  ({f.Ip})" : $"Pair with {f.Name}  ({f.Ip})", "", true, () => _ = Pair(row, tv, f.Ip, f.Name)));
        };
        stack.Children.Add(button);
        return stack;
    }

    async Task Pair(Row row, LgTvLink tv, string ip, string name)
    {
        _pairing.Add(row.Id);
        Rebuild();
        var (key, macs, error) = await LgWebOs.PairAsync(ip);
        _pairing.Remove(row.Id);
        if (key == null)
        {
            DarkDialog.Show(Window.GetWindow(this), "The TV didn't pair",
                $"{error}.\n\nMake sure the TV is on and on the same network, then try again and accept the prompt on the TV with the remote.");
            Rebuild();
            return;
        }
        if (ip != tv.Ip) tv.Mac = "";
        tv.Ip = ip;
        tv.ClientKey = key;
        tv.ReturnTo = "";
        if (name != "") tv.Name = name;
        // Its network cards (the TV's own list, and the one the network sees now it's on): needed to wake it later.
        tv.AddMacs(macs);
        SwitchLog.Write($"Paired LG TV {tv.Name} at {ip}" + (tv.Mac != "" ? $" (MAC {tv.Mac})" : " (MAC not found)"));
        Commit();
    }

    void AskForIp(Row row, LgTvLink tv)
    {
        var ip = InputDialog.Ask(Window.GetWindow(this), "LG TV's IP address",
            "On the TV: Settings → General → Network → Wi-Fi or Wired connection → Advanced. It looks like 192.168.1.42.",
            tv.Ip);
        if (ip == null) return;
        if (!System.Net.IPAddress.TryParse(ip.Trim(), out _))
        {
            DarkDialog.Show(Window.GetWindow(this), "That isn't an IP address", "It should be four numbers separated by dots, like 192.168.1.42.");
            return;
        }
        _ = Pair(row, tv, ip.Trim(), tv.Name);
    }

    /// <summary>Set by the main window: a TV is being turned on by hand, so stop anything keeping it off.</summary>
    public Action<string>? TvWanted { get; set; }

    async Task TryTv(Row row, LgTvLink tv, Func<LgTvLink, Task<string?>> action, string what)
    {
        if (what == "on") TvWanted?.Invoke(row.Id);
        _tvBusy[row.Id] = $"Turning it {what}…";
        Rebuild();
        string? error;
        try { error = await action(tv); }
        finally { _tvBusy.Remove(row.Id); }
        Commit(); // keeps any MAC addresses learned on the way
        if (error != null) DarkDialog.Show(Window.GetWindow(this), $"Couldn't turn the TV {what}", error);
    }

    /// <summary>Auto on, auto off and the HDMI input for one profile. Only usable when the TV is paired and the profile turns this display on.</summary>
    UIElement LgCell(Profile p, Row row, LgTvLink tv)
    {
        bool usable = tv.IsPaired && p.Displays.Contains(row.Id);
        var options = p.LgTv.TryGetValue(row.Id, out var o) ? o : new LgTvOptions();
        void Set(LgTvOptions next) { p.LgTv[row.Id] = next; Commit(); }

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 8, 0),
            IsEnabled = usable,
            ToolTip = usable ? null : !tv.IsPaired ? "Pair the TV first" : $"Tick this display in {p.Name} first" };
        CheckBox Switch(string label, bool value, Action<bool> set, string tip)
        {
            var sw = new CheckBox { Style = Res<Style>("Switch"), IsChecked = value, Margin = new Thickness(0, 2, 0, 2), ToolTip = tip,
                Content = new TextBlock { Text = label, FontSize = 11.5 } };
            sw.Click += (_, _) => set(sw.IsChecked == true);
            return sw;
        }
        panel.Children.Add(Switch("Auto on", options.PowerOn, v => Set(options with { PowerOn = v }),
            $"Turn the TV on when switching to {p.Name}"));
        // When switching to a profile that doesn't use the TV. One or the other, or neither (the TV is left as it is).
        panel.Children.Add(new TextBlock { Text = "When leaving", FontSize = 11, Foreground = Res<Brush>("MutedBrush"), Margin = new Thickness(0, 4, 0, 0),
            ToolTip = $"What happens to the TV when switching from {p.Name} to a profile that doesn't use it. Neither: it's left on the PC's input." });
        panel.Children.Add(Switch("Auto off", options.Leaving == LgLeave.TurnOff, v => Set(options.WithLeaving(v ? LgLeave.TurnOff : LgLeave.Stay)),
            "Turn the TV off"));
        panel.Children.Add(Switch("Auto return", options.Leaving == LgLeave.AutoReturn, v => Set(options.WithLeaving(v ? LgLeave.AutoReturn : LgLeave.Stay)),
            "Put the TV back the way it was: off if it was off, otherwise the input or app it was showing (HDMI 1, Netflix…)"));
        panel.Children.Add(Dropdown(
            options.HdmiInput is >= 1 and <= 4 ? $"Input: HDMI {options.HdmiInput}" : "Input: unchanged",
            "Switch the TV to this input when switching to this profile",
            new[] { 0, 1, 2, 3, 4 }.Select(n => (n == 0 ? "Leave it" : $"HDMI {n}", (string?)null, n == options.HdmiInput, (Action)(() => Set(options with { HdmiInput = n }))))));
        if (!usable) panel.Opacity = 0.45;
        return panel;
    }

    /// <summary>A small "Label ▾" button that opens a menu of choices; the current one is bold.</summary>
    Button Dropdown(string label, string tip, IEnumerable<(string Text, string? Tip, bool Current, Action Pick)> choices)
    {
        var button = new Button { Style = Res<Style>("IconButton"), Width = double.NaN, Height = double.NaN, Padding = new Thickness(4, 1, 4, 1), FontFamily = Res<FontFamily>("UiFont"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0),
            Content = new TextBlock { Text = label + " ▾", FontSize = 11 }, ToolTip = tip };
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (var c in choices)
            {
                var item = new MenuItem { Header = c.Text, ToolTip = c.Tip, FontWeight = c.Current ? FontWeights.SemiBold : FontWeights.Normal };
                item.Click += (_, _) => c.Pick();
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return button;
    }

    // ---------------- actions ----------------

    void New_Click(object sender, RoutedEventArgs e)
    {
        var p = new Profile { Name = UniqueName("Profile " + (_data.Profiles.Count + 1)), Color = NextColor() };
        _data.Profiles.Add(p);
        _focusNameOf = p.Id;
        Commit();
    }

    void Capture_Click(object sender, RoutedEventArgs e)
    {
        var p = ProfileService.Capture(_hub, UniqueName("Current setup"), NextColor());
        _data.Profiles.Add(p);
        _focusNameOf = p.Id;
        Commit();
    }

    string NextColor() =>
        ProfileColors.All.FirstOrDefault(c => _data.Profiles.All(p => p.Color != c))
        ?? ProfileColors.All[_data.Profiles.Count % ProfileColors.All.Length];

    string UniqueName(string name)
    {
        if (_data.Profiles.All(p => p.Name != name)) return name;
        for (int n = 2; ; n++)
            if (_data.Profiles.All(p => p.Name != $"{name} {n}")) return $"{name} {n}";
    }

    void AskDelete(Profile p)
    {
        _pendingDelete = p;
        ConfirmTitle.Text = $"Delete “{p.Name}”?";
        ConfirmOverlay.Visibility = Visibility.Visible;
        ConfirmDelete.Focus();
    }

    void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingDelete = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDelete != null) _data.Profiles.Remove(_pendingDelete);
        _pendingDelete = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        Commit();
    }

    void BodyScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Pad the header by the body's vertical scrollbar so both can scroll the same distance sideways.
        double scrollbar = BodyScroll.ActualWidth - BodyScroll.ViewportWidth;
        if (Math.Abs(HeadGrid.Margin.Right - scrollbar) > 0.5) HeadGrid.Margin = new Thickness(0, 0, scrollbar, 0);
        HeadScroll.ScrollToHorizontalOffset(BodyScroll.HorizontalOffset);
    }

    // ---------------- persistence ----------------

    // Save raises ProfileService.Changed, which this page listens to and rebuilds from; no second rebuild here.
    void Commit() => Save();

    void Save()
    {
        // Remember names for every device a profile points at, and forget the rest.
        var current = Sections.SelectMany(s => RowsFor(s).Where(r => r.Connected).Select(r => (s, r))).ToList();
        var known = new List<KnownDevice>();
        foreach (var section in Sections)
        {
            foreach (var id in ReferencedIds(section))
            {
                var live = current.FirstOrDefault(c => c.s == section && c.r.Id == id).r;
                var name = live?.Name ?? _data.Devices.FirstOrDefault(k => k.Id == id)?.Name ?? "Unknown device";
                known.Add(new KnownDevice { Id = id, Name = name, Section = section.ToString() });
            }
        }
        _data.Devices = known;

        if (_profiles.Save() is { } error)
            MessageBox.Show($"Couldn't save profiles: {error}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
