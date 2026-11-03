using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IOHub.Devices;

namespace IOHub.Ui;

/// <summary>Draws the active displays at their real desktop positions, like Windows' display settings.</summary>
public sealed class DisplayLayoutView : FrameworkElement
{
    IReadOnlyList<DisplayDevice> _displays = [];

    public void SetDisplays(IEnumerable<DisplayDevice> displays)
    {
        _displays = displays.Where(d => d.IsActive && d.Width > 0).ToList();
        InvalidateVisual();
    }

    static readonly Typeface NumberFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var text = Brush("TextBrush", Colors.White);
        var muted = Brush("MutedBrush", Colors.Gray);
        var faint = Brush("FaintBrush", Colors.DimGray);
        var accent = Brush("AccentBrush", Colors.SlateBlue);
        var line = Brush("Line2Brush", Colors.DimGray);

        if (_displays.Count == 0)
        {
            var empty = Text("No active displays", LabelFace, 13, muted, pixelsPerDip);
            dc.DrawText(empty, new Point((ActualWidth - empty.Width) / 2, (ActualHeight - empty.Height) / 2));
            return;
        }

        double minX = _displays.Min(d => d.X), minY = _displays.Min(d => d.Y);
        double maxX = _displays.Max(d => d.X + d.Width), maxY = _displays.Max(d => d.Y + d.Height);
        double scale = Math.Min(ActualWidth / (maxX - minX), ActualHeight / (maxY - minY));
        double offX = (ActualWidth - (maxX - minX) * scale) / 2, offY = (ActualHeight - (maxY - minY) * scale) / 2;

        var fill = new LinearGradientBrush(Color.FromRgb(0x23, 0x27, 0x31), Color.FromRgb(0x1A, 0x1D, 0x25), 90);
        fill.Freeze();
        var primaryFill = new LinearGradientBrush(Color.FromArgb(0x40, 0x7B, 0x8C, 0xFF), Color.FromArgb(0x18, 0xB0, 0x7B, 0xFF), 45);
        primaryFill.Freeze();

        foreach (var d in _displays)
        {
            var r = new Rect(offX + (d.X - minX) * scale, offY + (d.Y - minY) * scale, d.Width * scale, d.Height * scale);
            r.Inflate(-3, -3); // visible gap between touching displays
            if (r.Width <= 0 || r.Height <= 0) continue;

            dc.DrawRoundedRectangle(d.IsPrimary ? primaryFill : fill, new Pen(d.IsPrimary ? accent : line, d.IsPrimary ? 1.5 : 1), r, 8, 8);

            var number = Text(d.Number.ToString(CultureInfo.InvariantCulture), NumberFace, Math.Clamp(r.Height * 0.28, 14, 30),
                d.IsPrimary ? accent : text, pixelsPerDip);
            var name = Text(d.Name, LabelFace, 11.5, muted, pixelsPerDip, r.Width - 16);
            var res = Text($"{d.Width} × {d.Height}", LabelFace, 11, faint, pixelsPerDip, r.Width - 16);

            bool roomForLabels = r.Height > number.Height + name.Height + res.Height + 16 && r.Width > 90;
            double blockHeight = number.Height + (roomForLabels ? name.Height + res.Height + 4 : 0);
            double y = r.Top + (r.Height - blockHeight) / 2;
            dc.DrawText(number, new Point(r.Left + (r.Width - number.Width) / 2, y));
            if (!roomForLabels) continue;
            y += number.Height + 2;
            dc.DrawText(name, new Point(r.Left + (r.Width - name.Width) / 2, y));
            dc.DrawText(res, new Point(r.Left + (r.Width - res.Width) / 2, y + name.Height + 2));
        }
    }

    static FormattedText Text(string s, Typeface face, double size, Brush brush, double pixelsPerDip, double maxWidth = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
