using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IOHub.Ui;

/// <summary>
/// A small card near the bottom of the main screen for "Switched to TV" and the profile picker. It never takes
/// focus and clicks pass straight through, so it's safe to show over a game.
/// </summary>
public sealed class Toast : Window
{
    readonly StackPanel _body = new();
    readonly DispatcherTimer _hide = new();

    public Toast()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x1B, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x5B, 0xB8, 0xF5)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14, 22, 14),
            Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.55 },
            Child = _body,
        };

        _hide.Tick += (_, _) => { _hide.Stop(); FadeOut(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExTransparent | WsExToolWindow);
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>A one-line notice with an optional detail line. <paramref name="color"/> tints the dot (a profile's colour).</summary>
    public void Show(string title, string detail, double seconds = 2.2, string? color = null)
    {
        _body.Children.Clear();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Dot(color));
        row.Children.Add(new TextBlock { Text = title, FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(11, 0, 0, 0) });
        _body.Children.Add(row);
        if (detail.Length > 0)
            _body.Children.Add(new TextBlock { Text = detail, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(19, 4, 0, 0), MaxWidth = 420, TextWrapping = TextWrapping.Wrap });
        Present(seconds);
    }

    /// <summary>The numbered profile list shown while the picker waits for a digit.</summary>
    public void ShowPicker(IReadOnlyList<(string Name, Color Color, bool Active)> profiles, double seconds)
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = "Pick a profile", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        _body.Children.Add(new TextBlock { Text = "Press its number. Esc cancels.", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 2, 0, 10) });
        for (int i = 0; i < profiles.Count && i < 9; i++)
        {
            var (name, color, active) = profiles[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = (i + 1).ToString(), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            row.Children.Add(new TextBlock { Text = name, FontSize = 14, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            if (active)
                row.Children.Add(new TextBlock { Text = "current", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11.5, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _body.Children.Add(row);
        }
        Present(seconds);
    }

    public void HideNow() { _hide.Stop(); FadeOut(); }

    void Present(double seconds)
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (!IsVisible) base.Show();
        Place();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        _hide.Stop();
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Start();
    }

    void FadeOut()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (Opacity == 0) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 40;
    }

    static Ellipse Dot(string? color) => new()
    {
        Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center,
        Fill = new SolidColorBrush(color != null ? (Color)ColorConverter.ConvertFromString(color) : Color.FromRgb(0x5B, 0xB8, 0xF5)),
    };

    const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExTransparent = 0x20, WsExToolWindow = 0x80;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
