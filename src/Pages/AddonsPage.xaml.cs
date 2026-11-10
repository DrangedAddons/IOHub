using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IOHub.Pages;

/// <summary>
/// Game addons that go with IO Hub's integrations, one card each. IO Hub carries them inside the exe, installs them in
/// the game version you pick, and updates installed copies when it starts with a newer one. So far: FishIO, for the
/// WoW fishing bite trigger. Rebuilt whenever it's shown or something on it changes.
/// </summary>
public partial class AddonsPage : UserControl
{
    public AddonsPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) Rebuild(); };
    }

    void Rebuild()
    {
        Cards.Children.Clear();
        Cards.Children.Add(new Border
        {
            Style = (Style)FindResource("Card"), Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(0, 0, 12, 16),
            Child = FishIOControls(Rebuild),
        });
    }

    /// <summary>
    /// One line for where FishIO matters (the WoW fishing bite trigger, the controller's functions): installed or not,
    /// where, and a link to this page to manage it.
    /// </summary>
    public static UIElement FishIOStatus(Action openAddons)
    {
        Style S(string key) => (Style)Application.Current.FindResource(key);
        var installs = Integrations.FishIOAddon.Find();
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = "FishIO addon", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new Border
        {
            Style = S(installs.Count > 0 ? "BadgeInfo" : "Badge"), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = installs.Count > 0 ? "Installed" : "Not installed" },
        });
        row.Children.Add(new TextBlock
        {
            Style = S("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            Text = installs.Count > 0 ? Integrations.FishIOAddon.Describe(installs) : "turns WoW's music and ambience off while fishing, so the splash stands out",
        });
        var manage = new Button { Style = S("GhostButton"), Padding = new Thickness(10, 2, 10, 3), Content = "Manage in Addons", VerticalAlignment = VerticalAlignment.Center };
        manage.Click += (_, _) => openAddons();
        row.Children.Add(manage);
        return row;
    }

    /// <summary>
    /// FishIO, which IO Hub carries and installs: a dropdown of every WoW version found (with FishIO's state in each),
    /// Install (or Update), Open folder, and "…" for Reinstall, Remove and Copy folder path, then a line saying what's
    /// there and where. A development copy (a git working copy) is shown but never changed.
    /// </summary>
    UIElement FishIOControls(Action changed)
    {
        Style S(string key) => (Style)Application.Current.FindResource(key);
        Brush B(string key) => (Brush)Application.Current.FindResource(key);
        var box = new StackPanel();
        var installs = Integrations.FishIOAddon.Find();
        var games = Integrations.FishIOAddon.Games();
        string bundled = Integrations.FishIOAddon.Bundled;

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = "FishIO", Style = S("SectionTitle"), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new Border
        {
            Style = S(installs.Count > 0 ? "BadgeInfo" : "Badge"), Margin = new Thickness(10, 0, 0, 0),
            Child = new TextBlock { Text = installs.Count > 0 ? "Installed" : "Not installed" },
        });
        box.Children.Add(head);
        box.Children.Add(new TextBlock
        {
            Style = S("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 4, 0, 0),
            Text = $"For the WoW fishing bite trigger. While a fishing pole is in your main hand it turns music, ambience and chatter off, so the splash stands out. IO Hub carries {bundled} "
                 + "and updates installed copies when it starts with a newer one. Changes load at the next /reload or login.",
        });

        // One row: which WoW version (a dropdown of every one found, FishIO's state in each), Install or Update, Open
        // folder, and "…" for the rest. One line of detail under it.
        if (games.Count == 0)
        {
            box.Children.Add(new TextBlock
            {
                Style = S("Muted"), Foreground = B("WarnBrush"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 10, 0, 0),
                Text = "IO Hub couldn't find World of Warcraft. Start the game once with IO Hub running and it will find it.",
            });
            return box;
        }
        Integrations.FishIOInstall? InstallIn(string flavor) => installs.FirstOrDefault(i => i.Flavor.Equals(flavor, StringComparison.OrdinalIgnoreCase));
        // Start on a version that has it, else the one it's made for.
        if (_fishIOPick == null || !games.Any(g => g.Folder == _fishIOPick))
            _fishIOPick = (games.FirstOrDefault(g => InstallIn(g.Flavor) != null).Folder) ?? games[0].Folder;
        var pick = games.First(g => g.Folder == _fishIOPick);
        var installed = InstallIn(pick.Flavor);
        string name = Integrations.FishIOAddon.GameName(pick.Flavor);
        string State(Integrations.FishIOInstall? i) => i == null ? "not installed" : i.DevCopy ? $"{i.Version}, development copy" : $"{i.Version} installed";

        void Run(FrameworkElement from, Func<string?> action)
        {
            if (action() is { } error) Ui.DarkDialog.Show(Window.GetWindow(from), "FishIO", error);
            changed();
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

        // The version dropdown, the app's own kind: a button with a chevron that opens a menu.
        var picker = new Button { Style = S("GhostButton"), Padding = new Thickness(12, 4, 10, 5), MinWidth = 200, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = pick.Folder };
        var face = new Grid();
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        face.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var chevron = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 9, Margin = new Thickness(12, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = B("MutedBrush") };
        Grid.SetColumn(chevron, 1);
        face.Children.Add(chevron);
        picker.Content = face;
        picker.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = picker, Placement = PlacementMode.Bottom };
            foreach (var g in games)
            {
                var item = new MenuItem
                {
                    Header = $"{Integrations.FishIOAddon.GameName(g.Flavor)}  ·  {State(InstallIn(g.Flavor))}",
                    FontWeight = g.Folder == pick.Folder ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = g.Folder,
                };
                item.Click += (_, _) => { _fishIOPick = g.Folder; changed(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        row.Children.Add(picker);

        // Install, or Update when IO Hub has newer. Installed and current (or a development copy): nothing to do.
        var main = new Button { Style = S("GhostButton"), Padding = new Thickness(14, 4, 14, 5), Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold };
        if (installed == null)
        {
            main.Content = "Install";
            main.ToolTip = $"Puts FishIO {bundled} in {pick.Folder}\\Interface\\AddOns\\FishIO.";
            main.Click += (_, _) => Run(main, () => Integrations.FishIOAddon.Install(pick.Folder));
        }
        else if (installed.Outdated)
        {
            main.Content = $"Update to {bundled}";
            main.Click += (_, _) => Run(main, () => Integrations.FishIOAddon.Install(pick.Folder));
        }
        else
        {
            main.Content = "Installed";
            main.IsEnabled = false;
        }
        row.Children.Add(main);

        // The FishIO folder if it's there, otherwise the AddOns folder it would go in.
        var addons = System.IO.Path.Combine(pick.Folder, "Interface", "AddOns");
        var openTarget = installed?.Folder ?? (Directory.Exists(addons) ? addons : pick.Folder);
        var open = new Button { Style = S("GhostButton"), Padding = new Thickness(14, 4, 14, 5), Margin = new Thickness(6, 0, 0, 0), Content = "Open folder", ToolTip = openTarget };
        open.Click += (_, _) => Run(open, () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(openTarget) { UseShellExecute = true }); return null; }
            catch (Exception ex) { return ex.Message; }
        });
        row.Children.Add(open);

        // "…": the less common things.
        var more = new Button { Style = S("IconButton"), Content = "", Margin = new Thickness(4, 0, 0, 0), ToolTip = "More", VerticalAlignment = VerticalAlignment.Center };
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            MenuItem Item(string header, bool enabled, Action click, string? tip = null)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
                ToolTipService.SetShowOnDisabled(item, true);
                item.Click += (_, _) => click();
                return item;
            }
            bool own = installed is { DevCopy: false };
            string devTip = "A development copy (it has .git): IO Hub leaves it alone.";
            menu.Items.Add(Item($"Reinstall {bundled}", own, () => Run(more, () => Integrations.FishIOAddon.Install(pick.Folder)),
                installed?.DevCopy == true ? devTip : installed == null ? "Not installed here." : "Writes IO Hub's copy over this one."));
            menu.Items.Add(Item("Remove", own, () => Run(more, () => Integrations.FishIOAddon.Remove(installed!)),
                installed?.DevCopy == true ? devTip : installed == null ? "Not installed here." : "Deletes the FishIO folder. FishIO puts your sound settings back when you log out, so remove it between sessions."));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Copy folder path", true, () => { try { Clipboard.SetText(installed?.Folder ?? System.IO.Path.Combine(addons, "FishIO")); } catch { } }));
            menu.IsOpen = true;
        };
        row.Children.Add(more);
        box.Children.Add(row);

        // The detail line: what's there, where, and anything to know.
        string detail = installed switch
        {
            null => $"Not installed in {name}. Install puts FishIO {bundled} in {System.IO.Path.Combine(addons, "FishIO")}.",
            { DevCopy: true } => $"{installed.Version} in {installed.Folder}. A development copy (it has .git), so IO Hub doesn't change or remove it.",
            { Outdated: true } => $"{installed.Version} in {installed.Folder}. IO Hub has {bundled}.",
            _ => $"{installed.Version} in {installed.Folder}.",
        };
        box.Children.Add(new TextBlock { Text = detail, Style = S("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 6, 0, 0) });
        if (!pick.Made)
            box.Children.Add(new TextBlock
            {
                Style = S("Muted"), Foreground = B("WarnBrush"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 4, 0, 0),
                Text = $"FishIO is made for WoW Forever. {name} may list it as out of date (tick Load out of date AddOns), and it hasn't been tried there.",
            });
        return box;
    }

    /// <summary>The WoW version picked to install FishIO in, by folder, kept while IO Hub runs.</summary>
    string? _fishIOPick;

}
