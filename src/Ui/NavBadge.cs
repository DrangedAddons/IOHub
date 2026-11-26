using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IOHub.Ui;

/// <summary>
/// The little counter on a sidebar item (and on a card): how many things there want a look, such as an update. The
/// NavItem style shows it when it's above 0.
/// </summary>
public static class NavBadge
{
    public static readonly DependencyProperty CountProperty =
        DependencyProperty.RegisterAttached("Count", typeof(int), typeof(NavBadge), new FrameworkPropertyMetadata(0));

    public static int GetCount(DependencyObject o) => (int)o.GetValue(CountProperty);
    public static void SetCount(DependencyObject o, int value) => o.SetValue(CountProperty, value);

    /// <summary>The same counter as a standalone element, for a card's header.</summary>
    public static Border Pill(int count, string? tooltip = null) => new()
    {
        MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 0, 5, 0),
        Background = (Brush)Application.Current.FindResource("AccentBrush"),
        VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip,
        Child = new TextBlock
        {
            Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x14)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };
}
