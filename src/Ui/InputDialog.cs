using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IOHub.Ui;

/// <summary>A small dark "type a value" window, styled like <see cref="DarkDialog"/>. Returns the text, or null if cancelled.</summary>
public static class InputDialog
{
    public static string? Ask(Window? owner, string heading, string hint, string initial = "")
    {
        var res = Application.Current.Resources;
        var window = new Window
        {
            Title = AppInfo.Name, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null, Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = (Brush)res["BgBrush"], Foreground = (Brush)res["TextBrush"], FontFamily = (FontFamily)res["UiFont"], FontSize = 13,
            Width = 420,
        };

        var box = new TextBox { Style = (Style)res["Field"], Text = initial, Margin = new Thickness(0, 14, 0, 0) };
        string? result = null;
        var ok = new Button { Content = "OK", Style = (Style)res["AccentButton"], MinWidth = 90, FontWeight = FontWeights.SemiBold, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)res["GhostButton"], MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        cancel.Click += (_, _) => window.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var layout = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        layout.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeights.SemiBold });
        layout.Children.Add(new TextBlock { Text = hint, Foreground = (Brush)res["MutedBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        layout.Children.Add(box);
        layout.Children.Add(buttons);
        window.Content = layout;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();
        return result;
    }
}
