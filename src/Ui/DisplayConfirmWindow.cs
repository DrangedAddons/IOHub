using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace IOHub.Ui;

/// <summary>
/// "Keep these display settings?" with a countdown, like Windows' own. Reverting on timeout matters: if the new
/// layout left every screen black, nobody can press anything.
/// </summary>
public sealed class DisplayConfirmWindow : Window
{
    const int Seconds = 15;
    readonly TaskCompletionSource<bool> _result = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int _left = Seconds;

    public static Task<bool> Ask(string profileName)
    {
        var w = new DisplayConfirmWindow(profileName);
        w.Show();
        w.Activate();
        return w._result.Task;
    }

    DisplayConfirmWindow(string profileName)
    {
        Title = "Keep these displays?";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = true;
        // Centre on whatever is the main display now, which may have just changed.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var countdown = new TextBlock { Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        void UpdateText() => countdown.Text = $"Going back to the previous layout in {_left} s.";
        UpdateText();

        var keep = new Button { Content = "Keep", Style = (Style)FindResource("AccentButton"), FontWeight = FontWeights.SemiBold, IsDefault = true, MinWidth = 90 };
        var revert = new Button { Content = "Revert", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 0, 10, 0), IsCancel = true, MinWidth = 90 };
        keep.Click += (_, _) => Finish(true);
        revert.Click += (_, _) => Finish(false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(revert);
        buttons.Children.Add(keep);

        var body = new StackPanel { Width = 380 };
        body.Children.Add(new TextBlock { Text = "Keep these displays?", Style = (Style)FindResource("SectionTitle"), FontSize = 17 });
        body.Children.Add(new TextBlock
        {
            Text = $"This is the first time {profileName} has switched the displays. If everything looks right, keep it and it won't ask again.",
            Foreground = (Brush)FindResource("SubtleBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        body.Children.Add(countdown);
        body.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x24)),
            BorderBrush = (Brush)FindResource("Line2Brush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(24),
            Margin = new Thickness(20), // room for the shadow
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.5 },
            Child = body,
        };
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;

        _timer.Tick += (_, _) =>
        {
            if (--_left <= 0) Finish(false);
            else UpdateText();
        };
        _timer.Start();
        Closed += (_, _) => Finish(false);
    }

    void Finish(bool keep)
    {
        _timer.Stop();
        if (_result.TrySetResult(keep) && IsVisible) Close();
    }
}
