using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IOHub.Ui;

public enum TopologyLayout
{
    /// <summary>Host on the left, profiles stacked on the right.</summary>
    Flow,
    /// <summary>Host in the middle, profiles split either side.</summary>
    Hub,
    /// <summary>Host on top, profiles in rows underneath.</summary>
    Tree,
}

/// <summary>
/// The host card wired to each profile card, arranged by <see cref="Layout"/>.
/// The wire to <see cref="ActiveIndex"/> carries a stream of particles.
/// </summary>
public sealed class TopologyPanel : Panel
{
    readonly WireLayer _wires = new();
    UIElement? _host;
    readonly List<UIElement> _nodes = [];
    TopologyLayout _layout = TopologyLayout.Flow;

    public double Padding { get; set; } = 32;
    public double NodeGap { get; set; } = 22;
    public int ActiveIndex { get; private set; } = -1;

    // Wires stop just short of each card so the port circles aren't drawn underneath it.
    const double PortGap = 7;

    public TopologyLayout Layout
    {
        get => _layout;
        set { _layout = value; InvalidateMeasure(); }
    }

    public TopologyPanel()
    {
        Children.Add(_wires);
        ClipToBounds = true;
    }

    public void SetContent(UIElement host, IEnumerable<UIElement> nodes, int activeIndex)
    {
        Children.Clear();
        Children.Add(_wires);
        _host = host;
        Children.Add(host);
        _nodes.Clear();
        _nodes.AddRange(nodes);
        foreach (var n in _nodes) Children.Add(n);
        ActiveIndex = activeIndex;
        InvalidateMeasure();
    }

    sealed record Plan(Size Size, Rect Host, List<Rect> Nodes, List<Wire> Wires);

    protected override Size MeasureOverride(Size available)
    {
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _wires.Measure(available);
        _host?.Measure(inf);
        foreach (var n in _nodes) n.Measure(inf);
        if (_host == null) return new Size(0, 0);

        // A horizontal ScrollViewer measures with infinite width; ask for just what the layout needs then.
        var size = Compute(new Size(double.IsInfinity(available.Width) ? 0 : available.Width, 0)).Size;
        return new Size(double.IsInfinity(available.Width) ? size.Width : Math.Max(size.Width, available.Width), size.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _wires.Arrange(new Rect(final));
        if (_host == null) return final;

        var plan = Compute(final);
        _host.Arrange(plan.Host);
        for (int i = 0; i < _nodes.Count; i++) _nodes[i].Arrange(plan.Nodes[i]);
        _wires.SetWires(plan.Wires);
        return final;
    }

    /// <summary>The narrowest width the current layout fits in without overlapping cards.</summary>
    public double RequiredWidth => _host == null ? 0 : Compute(new Size(0, 0)).Size.Width;

    /// <summary>Positions for everything. <paramref name="space"/>.Height may be 0 when only the needed size is wanted.</summary>
    Plan Compute(Size space) => _layout switch
    {
        TopologyLayout.Hub => Hub(space),
        TopologyLayout.Tree => Tree(space),
        _ => Flow(space),
    };

    double NodeWidth => _nodes.Count == 0 ? 0 : _nodes.Max(n => n.DesiredSize.Width);
    double ColumnHeight(IEnumerable<int> indices) =>
        indices.Sum(i => _nodes[i].DesiredSize.Height) + NodeGap * Math.Max(0, indices.Count() - 1);

    Plan Flow(Size space)
    {
        var host = _host!.DesiredSize;
        double nodeW = NodeWidth, nodesH = ColumnHeight(Enumerable.Range(0, _nodes.Count));
        double height = Math.Max(space.Height, Math.Max(host.Height, nodesH) + Padding * 2);

        double wire = Math.Clamp(space.Width - Padding * 2 - host.Width - nodeW, 120, 320);
        double left = Math.Max(Padding, (space.Width - (host.Width + wire + nodeW)) / 2);
        var hostRect = new Rect(left, (height - host.Height) / 2, host.Width, host.Height);

        var rects = new List<Rect>();
        var wires = new List<Wire>();
        var from = new Point(hostRect.Right + PortGap, hostRect.Top + host.Height / 2);
        double y = (height - nodesH) / 2, x = hostRect.Right + wire;
        for (int i = 0; i < _nodes.Count; i++)
        {
            var r = new Rect(x, y, nodeW, _nodes[i].DesiredSize.Height);
            rects.Add(r);
            wires.Add(new Wire(from, Right, new Point(r.Left - PortGap, r.Top + r.Height / 2), Left, i == ActiveIndex));
            y += r.Height + NodeGap;
        }
        return new Plan(new Size(host.Width + nodeW + 120 + Padding * 2, height), hostRect, rects, wires);
    }

    Plan Hub(Size space)
    {
        var host = _host!.DesiredSize;
        double nodeW = NodeWidth;
        // Alternate sides so the first profile is on the right and the two columns stay balanced.
        var right = Enumerable.Range(0, _nodes.Count).Where(i => i % 2 == 0).ToList();
        var left = Enumerable.Range(0, _nodes.Count).Where(i => i % 2 == 1).ToList();
        double height = Math.Max(space.Height, Math.Max(host.Height, Math.Max(ColumnHeight(right), ColumnHeight(left))) + Padding * 2);

        double wire = Math.Clamp((space.Width - Padding * 2 - host.Width - nodeW * 2) / 2, 80, 240);
        double startX = Math.Max(Padding, (space.Width - (nodeW * 2 + wire * 2 + host.Width)) / 2);
        var hostRect = new Rect(startX + nodeW + wire, (height - host.Height) / 2, host.Width, host.Height);

        var rects = new Rect[_nodes.Count];
        var wires = new List<Wire>();
        void Column(List<int> indices, bool onRight)
        {
            double y = (height - ColumnHeight(indices)) / 2;
            double x = onRight ? hostRect.Right + wire : hostRect.Left - wire - nodeW;
            var from = onRight
                ? new Point(hostRect.Right + PortGap, hostRect.Top + host.Height / 2)
                : new Point(hostRect.Left - PortGap, hostRect.Top + host.Height / 2);
            foreach (var i in indices)
            {
                var r = new Rect(x, y, nodeW, _nodes[i].DesiredSize.Height);
                rects[i] = r;
                var to = onRight ? new Point(r.Left - PortGap, r.Top + r.Height / 2) : new Point(r.Right + PortGap, r.Top + r.Height / 2);
                wires.Add(new Wire(from, onRight ? Right : Left, to, onRight ? Left : Right, i == ActiveIndex));
                y += r.Height + NodeGap;
            }
        }
        Column(right, onRight: true);
        Column(left, onRight: false);
        return new Plan(new Size(host.Width + nodeW * 2 + 160 + Padding * 2, height), hostRect, [.. rects], wires);
    }

    Plan Tree(Size space)
    {
        const double drop = 110; // vertical run of the wires
        var host = _host!.DesiredSize;
        double nodeW = NodeWidth;
        int cols = Math.Max(1, Math.Min(_nodes.Count, (int)((space.Width - Padding * 2 + NodeGap) / (nodeW + NodeGap))));
        int rows = (_nodes.Count + cols - 1) / cols;
        var rowHeights = Enumerable.Range(0, rows)
            .Select(r => _nodes.Skip(r * cols).Take(cols).Max(n => n.DesiredSize.Height)).ToList();
        double contentH = host.Height + (rows > 0 ? drop + rowHeights.Sum() + NodeGap * (rows - 1) : 0);
        double height = Math.Max(space.Height, contentH + Padding * 2);

        double top = (height - contentH) / 2;
        var hostRect = new Rect((space.Width - host.Width) / 2, top, host.Width, host.Height);
        var from = new Point(hostRect.Left + host.Width / 2, hostRect.Bottom + PortGap);

        var rects = new List<Rect>();
        var wires = new List<Wire>();
        double y = hostRect.Bottom + drop;
        for (int r = 0; r < rows; r++)
        {
            int count = Math.Min(cols, _nodes.Count - r * cols);
            double x = (space.Width - (count * nodeW + (count - 1) * NodeGap)) / 2;
            for (int c = 0; c < count; c++)
            {
                int i = r * cols + c;
                var rect = new Rect(x, y, nodeW, _nodes[i].DesiredSize.Height);
                rects.Add(rect);
                wires.Add(new Wire(from, Down, new Point(rect.Left + nodeW / 2, rect.Top - PortGap), Up, i == ActiveIndex));
                x += nodeW + NodeGap;
            }
            y += rowHeights[r] + NodeGap;
        }
        return new Plan(new Size(Math.Max(host.Width, nodeW) + Padding * 2, height), hostRect, rects, wires);
    }

    static readonly Vector Right = new(1, 0), Left = new(-1, 0), Down = new(0, 1), Up = new(0, -1);
}

/// <param name="FromDir">Direction the wire leaves its start in.</param>
/// <param name="ToDir">Direction from the end point back along the wire (away from the card it plugs into).</param>
public readonly record struct Wire(Point From, Vector FromDir, Point To, Vector ToDir, bool Active);

/// <summary>
/// Draws the wires. Inactive ones are static; the active one animates, so this layer redraws every frame while
/// it's visible and there's an active wire. Only this element re-renders: the cards aren't touched. Each wire's
/// curve, pen and length are worked out once when the layout changes, so a frame only places particles.
/// </summary>
public sealed class WireLayer : FrameworkElement
{
    sealed record Drawn(Wire Wire, Geometry Curve, Pen Core, Point C1, Point C2, double Length);

    List<Drawn> _wires = [];
    bool _animating;
    readonly DateTime _start = DateTime.Now;

    static readonly Color Blue = Color.FromRgb(0x5B, 0xB8, 0xF5);
    static readonly Color Violet = Color.FromRgb(0x7B, 0x8C, 0xFF);

    static readonly Pen IdlePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)), 1.5) { DashStyle = new DashStyle([3, 3], 0) });
    static readonly Pen GlowPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0x5B, 0xB8, 0xF5)), 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Brush ParticleGlow = Frozen(new RadialGradientBrush(
        [new GradientStop(Color.FromArgb(0x80, 0x7F, 0xCB, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0x5B, 0xB8, 0xF5), 1)]));
    static readonly Brush ParticleCore = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xF4, 0xFF)));
    static readonly Brush PortFill = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1F)));
    static readonly Brush PortDot = Frozen(new SolidColorBrush(Blue));
    static readonly Pen PortIdlePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C)), 1.5));
    static readonly Pen PortActivePen = Frozen(new Pen(new SolidColorBrush(Blue), 2));

    const double PixelsPerParticle = 6;  // density along the wire
    const double BaseSpeed = 80;         // pixels per second, before each particle's own variation
    const double Spread = 7;             // how far particles stray either side of the line, at the middle

    public WireLayer()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        Loaded += (_, _) => UpdateAnimation();
    }

    public void SetWires(List<Wire> wires)
    {
        _wires = wires.Select(Prepare).ToList();
        UpdateAnimation();
        InvalidateVisual();
    }

    static Drawn Prepare(Wire w)
    {
        var span = w.To - w.From;
        var c1 = w.From + w.FromDir * Math.Max(40, Math.Abs(span * w.FromDir) * 0.5);
        var c2 = w.To + w.ToDir * Math.Max(40, Math.Abs(span * w.ToDir) * 0.5);

        var curve = new StreamGeometry();
        using (var ctx = curve.Open())
        {
            ctx.BeginFigure(w.From, false, false);
            ctx.BezierTo(c1, c2, w.To, true, true);
        }
        curve.Freeze();

        // A dimmer core than before, so the particles read against it rather than disappearing into it.
        var core = Frozen(new Pen(new LinearGradientBrush(
            Color.FromArgb(0x90, Blue.R, Blue.G, Blue.B), Color.FromArgb(0x90, Violet.R, Violet.G, Violet.B), w.From, w.To)
            { MappingMode = BrushMappingMode.Absolute }, 1.4));

        double length = 0;
        var prev = w.From;
        for (int s = 1; s <= 32; s++)
        {
            var next = Bezier(w.From, c1, c2, w.To, s / 32.0);
            length += (next - prev).Length;
            prev = next;
        }
        return new Drawn(w, curve, core, c1, c2, Math.Max(1, length));
    }

    void UpdateAnimation()
    {
        bool want = IsVisible && IsLoaded && _wires.Any(d => d.Wire.Active);
        if (want && !_animating) { CompositionTarget.Rendering += OnFrame; _animating = true; }
        else if (!want) StopAnimation();
    }

    void StopAnimation()
    {
        if (!_animating) return;
        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
    }

    // 30 frames a second is plenty for drifting particles and halves the redraw cost of the 60 Windows offers.
    TimeSpan _lastFrame;
    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now - _lastFrame < TimeSpan.FromMilliseconds(30)) return;
        _lastFrame = now;
        InvalidateVisual();
    }

    /// <summary>The soft halo drawn behind each particle, rendered once as an image: much cheaper than a gradient per particle.</summary>
    static readonly ImageSource GlowSprite = MakeGlowSprite();

    static ImageSource MakeGlowSprite()
    {
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
            ctx.DrawEllipse(ParticleGlow, null, new Point(32, 32), 32, 32);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double t = (DateTime.Now - _start).TotalSeconds;

        // Idle wires first so the active one draws over any crossing.
        foreach (var d in _wires.Where(d => !d.Wire.Active))
        {
            dc.DrawGeometry(null, IdlePen, d.Curve);
            DrawPort(dc, d.Wire.To, active: false);
        }

        foreach (var d in _wires.Where(d => d.Wire.Active))
        {
            var w = d.Wire;
            dc.DrawGeometry(null, GlowPen, d.Curve);
            dc.DrawGeometry(null, d.Core, d.Curve);

            // Dense and random: the count scales with the wire's length, and every particle has its own start point,
            // speed, size, offset from the line and twinkle. With this many, clumps and gaps even out on their own.
            int count = Math.Max(16, (int)(d.Length / PixelsPerParticle));
            var across = new Vector(-w.FromDir.Y, w.FromDir.X);
            for (int i = 0; i < count; i++)
            {
                double speed = BaseSpeed * (0.55 + 0.9 * Hash(i, 2)) / d.Length; // path lengths per second
                double phase = Frac(Hash(i, 1) + t * speed);
                var p = Bezier(w.From, d.C1, d.C2, w.To, phase);
                // Mostly fine dust with the odd bigger spark.
                double r = Hash(i, 3);
                double size = 1.1 + 2.2 * r * r * r;
                double twinkle = 0.7 + 0.3 * Math.Sin(t * (2 + 4 * Hash(i, 4)) + i * 1.7);
                double fade = Math.Min(1, Math.Min(phase, 1 - phase) * 10); // ease in/out at the ends
                // Spread across the beam, widest in the middle so the ends stay pinned to the ports.
                p += across * ((Hash(i, 5) - 0.5) * 2 * Spread * (1 - Math.Abs(phase - 0.5) * 2));

                dc.PushOpacity(fade * twinkle);
                double halo = size * 3.4;
                dc.DrawImage(GlowSprite, new Rect(p.X - halo, p.Y - halo, halo * 2, halo * 2));
                dc.DrawEllipse(ParticleCore, null, p, size, size);
                dc.Pop();
            }
            DrawPort(dc, w.To, active: true);
        }

        // Host ports: one per distinct start point (the hub layout has two).
        foreach (var g in _wires.GroupBy(d => d.Wire.From))
            DrawPort(dc, g.Key, active: g.Any(d => d.Wire.Active));
    }

    static void DrawPort(DrawingContext dc, Point p, bool active)
    {
        if (active) dc.DrawEllipse(ParticleGlow, null, p, 12, 12);
        dc.DrawEllipse(PortFill, active ? PortActivePen : PortIdlePen, p, 5, 5);
        if (active) dc.DrawEllipse(PortDot, null, p, 2.2, 2.2);
    }

    static Point Bezier(Point a, Point c1, Point c2, Point b, double t)
    {
        double u = 1 - t;
        double k0 = u * u * u, k1 = 3 * u * u * t, k2 = 3 * u * t * t, k3 = t * t * t;
        return new Point(k0 * a.X + k1 * c1.X + k2 * c2.X + k3 * b.X, k0 * a.Y + k1 * c1.Y + k2 * c2.Y + k3 * b.Y);
    }

    /// <summary>A stable 0..1 random number per particle and purpose, the same every frame.</summary>
    static double Hash(int i, int salt) => Frac(Math.Sin(i * 12.9898 + salt * 78.233) * 43758.5453);

    static double Frac(double v) => v - Math.Floor(v);

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
