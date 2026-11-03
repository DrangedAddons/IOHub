using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IOHub.Ui;

/// <summary>
/// The app's own message window: dark, styled like the rest of the app, and silent (unlike MessageBox, which plays
/// the Windows "ding"). Enter picks the main button, Esc the other (or closes). Same shape as the manager app's.
/// </summary>
public static class DarkDialog
{
    /// <summary>Returns true when the main button was chosen. <paramref name="danger"/> makes the main button red.</summary>
    public static bool Show(Window? owner, string heading, string message, string primary = "OK", string? secondary = null,
        bool showLogo = false, bool danger = false)
    {
        var res = Application.Current.Resources;
        var window = new Window
        {
            Title = AppInfo.Name,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner is { IsVisible: true } ? owner : null,
            Background = (Brush)res["BgBrush"],
            Foreground = (Brush)res["TextBrush"],
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 13,
            MinWidth = 380,
            MaxWidth = 580,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int on = 1, caption = 0x00141210; // #101214 as COLORREF
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));        // dark title bar
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // caption colour (Windows 11)
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (message.Length > 0)
            text.Children.Add(new TextBlock { Text = message, Foreground = (Brush)res["MutedBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        var body = new DockPanel();
        if (showLogo)
        {
            var logo = new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/logo.png")),
                Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(logo, Dock.Left);
            body.Children.Add(logo);
        }
        body.Children.Add(text);

        var result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        if (secondary is not null)
        {
            var other = new Button { Content = secondary, Style = (Style)res["GhostButton"], MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            other.Click += (_, _) => window.Close();
            buttons.Children.Add(other);
        }
        var main = new Button
        {
            Content = primary, Style = (Style)res[danger ? "DangerButton" : "AccentButton"], MinWidth = 90,
            FontWeight = FontWeights.SemiBold, IsDefault = true, IsCancel = secondary is null,
        };
        main.Click += (_, _) => { result = true; window.Close(); };
        buttons.Children.Add(main);

        var layout = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        window.Content = layout;
        window.Loaded += (_, _) => main.Focus();
        window.ShowDialog();
        return result;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
