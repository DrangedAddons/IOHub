using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace IOHub.Ui;

/// <summary>
/// The tray icon, so triggers keep working with the window closed. Left click opens the window; the right-click
/// menu switches profiles, pauses triggers, checks for updates and quits. Dark menu, like the manager app's.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    Action? _balloonClicked;
    static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xEC, 0xEE, 0xF2);

    public TrayIcon(
        Action open,
        Action exit,
        Action checkForUpdates,
        Func<IEnumerable<(string Name, bool Active, Action Switch)>> profiles,
        Func<bool> paused,
        Action<bool> setPaused)
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()),
            BackColor = Drawing.Color.FromArgb(0x1F, 0x23, 0x2B),
            ForeColor = Text,
            Font = new Drawing.Font("Segoe UI", 9f),
        };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            Add(menu, new Forms.ToolStripMenuItem($"Open {AppInfo.Name}", null, (_, _) => open()) { Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold) });
            menu.Items.Add(new Forms.ToolStripSeparator());
            var list = profiles().ToList();
            if (list.Count == 0) Add(menu, new Forms.ToolStripMenuItem("No profiles yet") { Enabled = false });
            foreach (var (name, active, switchTo) in list)
                Add(menu, new Forms.ToolStripMenuItem($"Switch to {name}", null, (_, _) => switchTo()) { Checked = active });
            menu.Items.Add(new Forms.ToolStripSeparator());
            Add(menu, new Forms.ToolStripMenuItem("Pause triggers", null, (_, _) => setPaused(!paused())) { Checked = paused() });
            Add(menu, new Forms.ToolStripMenuItem("Check for updates", null, (_, _) => checkForUpdates()));
            menu.Items.Add(new Forms.ToolStripSeparator());
            Add(menu, new Forms.ToolStripMenuItem("Quit", null, (_, _) => exit()));
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = AppInfo.Name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => (_balloonClicked ?? open)();
    }

    static void Add(Forms.ContextMenuStrip menu, Forms.ToolStripMenuItem item)
    {
        item.ForeColor = Text;
        menu.Items.Add(item);
    }

    /// <summary>A Windows notification from the tray icon. Clicking it runs <paramref name="clicked"/> (default: open the window).</summary>
    public void Notify(string title, string text, Action? clicked = null)
    {
        _balloonClicked = clicked;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    // The app icon at the size Windows uses for tray icons; app.ico holds hand-tuned small frames.
    static Drawing.Icon LoadIcon()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
        return stream is null ? Drawing.SystemIcons.Application : new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        static readonly Drawing.Color Panel = Drawing.Color.FromArgb(0x1F, 0x23, 0x2B);
        static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x28, 0x2D, 0x37);
        static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x32, 0x38, 0x44);

        public override Drawing.Color ToolStripDropDownBackground => Panel;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Panel;
        public override Drawing.Color ImageMarginGradientMiddle => Panel;
        public override Drawing.Color ImageMarginGradientEnd => Panel;
        public override Drawing.Color CheckBackground => Hover;
        public override Drawing.Color CheckSelectedBackground => Hover;
        public override Drawing.Color CheckPressedBackground => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Panel;
    }
}
