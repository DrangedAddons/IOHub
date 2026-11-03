using System.Windows;
using System.Windows.Controls;

namespace IOHub.Ui;

/// <summary>
/// Lays children out in as many equal columns as fit at <see cref="MinItemWidth"/>. Items in a row share the
/// row's height, so cards line up.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    double[] _rowHeights = [];

    (int cols, double colWidth) Columns(double width)
    {
        if (double.IsInfinity(width)) width = MinItemWidth * 2 + Spacing;
        int cols = Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));
        return (cols, Math.Max(0, (width - Spacing * (cols - 1)) / cols));
    }

    protected override Size MeasureOverride(Size available)
    {
        var (cols, colWidth) = Columns(available.Width);
        int rows = (InternalChildren.Count + cols - 1) / cols;
        _rowHeights = new double[rows];
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(colWidth, double.PositiveInfinity));
            _rowHeights[i / cols] = Math.Max(_rowHeights[i / cols], child.DesiredSize.Height);
        }
        double height = _rowHeights.Sum() + Spacing * Math.Max(0, rows - 1);
        double width = double.IsInfinity(available.Width) ? cols * colWidth + Spacing * (cols - 1) : available.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (cols, colWidth) = Columns(final.Width);
        double y = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int row = i / cols, col = i % cols;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + Spacing;
            double h = row < _rowHeights.Length ? _rowHeights[row] : InternalChildren[i].DesiredSize.Height;
            InternalChildren[i].Arrange(new Rect(col * (colWidth + Spacing), y, colWidth, h));
        }
        return final;
    }
}
