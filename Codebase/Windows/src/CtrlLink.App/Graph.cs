using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CtrlLink.Core;
using static CtrlLink.App.Ui;

namespace CtrlLink.App;

/// <summary>A node in the Devices graph: this PC, a paired peer, or the "+N more" fold (More &gt; 0).</summary>
sealed record GraphNode(string Name, string Detail, string Pf, bool Online, bool Current = false, int More = 0);

/// <summary>Figma Device-connection graph (69:3849): this PC and its peers as 40 px tiles joined by dashed connectors with
/// green dots at both ends (grey when offline). Online links carry a sky-blue pulse travelling towards this PC, unless
/// reduce motion is on. Offline peers are dimmed. Layouts as on Android (ui/Graph.kt), one peer as in the Windows frame.</summary>
sealed class DeviceGraph : Panel
{
    const double Tile = 40, DotSize = 12;
    static readonly Stopwatch Clock = Stopwatch.StartNew();   // the pulse phase survives the screen being rebuilt
    readonly GraphNode me;
    readonly IReadOnlyList<GraphNode> peers;
    readonly DispatcherTimer? timer;
    readonly LineLayer lines;
    Point mePos;
    Point[] peerPos = [];

    public DeviceGraph(GraphNode me, IReadOnlyList<GraphNode> peers)
    {
        this.me = me;
        this.peers = peers;
        ClipToBounds = true;
        lines = new LineLayer(this);
        Children.Add(lines);
        Children.Add(Node(me));
        foreach (var p in peers) Children.Add(Node(p));
        Children.Add(new DotLayer(this));
        Avalonia.Automation.AutomationProperties.SetName(this, $"This PC, {me.Name}" + string.Concat(peers.Select(p =>
            p.More > 0 ? $", and {p.More} more" : $", {p.Name}, {(p.Online ? "online" : "offline")}")));
        if (!ReduceMotion && peers.Any(p => p.Online && p.More == 0))
        {
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => lines.InvalidateVisual());
            AttachedToVisualTree += (_, _) => timer.Start();
            DetachedFromVisualTree += (_, _) => timer.Stop();
        }
    }

    /// <summary>Tile centres from the box centre for this PC and up to four other nodes, chosen so labels (to the right of
    /// each tile) never collide. One peer: stacked (Windows frame 52:1459); two: peers left, this PC right.</summary>
    static (Point Me, Point[] Peers) Layout(int n) => n switch
    {
        0 => (new Point(-55, 0), []),
        1 => (new Point(-55, -53), [new Point(-55, 54)]),
        2 => (new Point(17, 0), [new Point(-115, -100), new Point(-115, 100)]),
        3 => (new Point(-100, 0), [new Point(-120, -150), new Point(20, -75), new Point(-120, 150)]),
        _ => (new Point(-100, 0), [new Point(-120, -150), new Point(20, -75), new Point(20, 75), new Point(-120, 150)]),
    };

    protected override Avalonia.Size MeasureOverride(Avalonia.Size available)
    {
        foreach (var c in Children) c.Measure(Avalonia.Size.Infinity);
        return new Avalonia.Size(double.IsInfinity(available.Width) ? 378 : available.Width, double.IsInfinity(available.Height) ? 380 : available.Height);
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size size)
    {
        var (m, ps) = Layout(peers.Count);
        var sy = Math.Clamp((size.Height - 24) / 340, 0.4, 1);
        var c = new Point(size.Width / 2, size.Height / 2);
        Point At(Point o) => new(c.X + o.X, c.Y + o.Y * sy);
        mePos = At(m);
        peerPos = [.. ps.Select(At)];
        Children[0].Arrange(new Rect(size));
        for (var i = 1; i < Children.Count - 1; i++)
        {
            var child = Children[i];
            var pos = i == 1 ? mePos : peerPos[i - 2];
            var left = pos.X - Tile / 2;
            if (peers.Count == 0) left = Math.Max(16, (size.Width - child.DesiredSize.Width) / 2);   // alone: the whole node is centred
            var w = Math.Max(Tile, Math.Min(child.DesiredSize.Width, size.Width - left - 16));
            child.Arrange(new Rect(left, pos.Y - Tile / 2, w, Tile));
        }
        Children[^1].Arrange(new Rect(size));
        return size;
    }

    /// <summary>The side of the tile at c a connector to `to` leaves from, and its outward direction: the left middle when the
    /// other node is mostly to the left, else the top or bottom middle. Never the right side, where the label is.</summary>
    static (Point P, Vector D) Exit(Point c, Point to)
    {
        var d = to - c;
        if (d.X < 0 && Math.Abs(d.Y) < Math.Abs(d.X)) return (new Point(c.X - Tile / 2, c.Y), new Vector(-1, 0));
        return d.Y < 0 ? (new Point(c.X, c.Y - Tile / 2), new Vector(0, -1)) : (new Point(c.X, c.Y + Tile / 2), new Vector(0, 1));
    }

    /// <summary>Connector i as a cubic (leaves each tile straight out of its side, then bends towards the other).</summary>
    (Point A, Point C1, Point C2, Point B) Link(int i)
    {
        var (a, da) = Exit(mePos, peerPos[i]);
        var (b, db) = Exit(peerPos[i], mePos);
        var k = Math.Sqrt((b - a).X * (b - a).X + (b - a).Y * (b - a).Y) * 0.45;
        return (a, a + da * k, b + db * k, b);
    }

    static Point Bezier((Point A, Point C1, Point C2, Point B) l, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * u * l.A.X + 3 * u * u * t * l.C1.X + 3 * u * t * t * l.C2.X + t * t * t * l.B.X,
            u * u * u * l.A.Y + 3 * u * u * t * l.C1.Y + 3 * u * t * t * l.C2.Y + t * t * t * l.B.Y);
    }

    /// <summary>Connectors and the pulse, behind the tiles.</summary>
    sealed class LineLayer(DeviceGraph g) : Control
    {
        public override void Render(DrawingContext ctx) => g.DrawLines(ctx);
    }

    void DrawLines(DrawingContext ctx)
    {
        var phase = Clock.Elapsed.TotalMilliseconds % 1800 / 1800;
        for (var i = 0; i < peers.Count && i < peerPos.Length; i++)
        {
            var l = Link(i);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(l.A, false);
                g.CubicBezierTo(l.C1, l.C2, l.B);
                g.EndFigure(false);
            }
            var alpha = 0.68 * (peers[i].Online ? 1 : 0.4);
            ctx.DrawGeometry(null, new Pen(B(P.Text, alpha), 1, new DashStyle([5.7, 3.8], 0)), geo);
            if (!peers[i].Online || peers[i].More > 0 || timer is null) continue;
            // Peer → this PC, a short fading trail behind the head (equal-length steps along the curve).
            var pts = Enumerable.Range(0, 41).Select(j => Bezier(l, j / 40.0)).ToArray();
            var cum = new double[pts.Length];
            for (var j = 1; j < pts.Length; j++) cum[j] = cum[j - 1] + Math.Sqrt(Math.Pow(pts[j].X - pts[j - 1].X, 2) + Math.Pow(pts[j].Y - pts[j - 1].Y, 2));
            Point AtLen(double f)
            {
                var target = Math.Clamp(f, 0, 1) * cum[^1];
                var j = Math.Max(1, Array.FindIndex(cum, x => x >= target));
                var seg = cum[j] - cum[j - 1];
                var t = seg <= 0 ? 0 : (target - cum[j - 1]) / seg;
                return new Point(pts[j - 1].X + (pts[j].X - pts[j - 1].X) * t, pts[j - 1].Y + (pts[j].Y - pts[j - 1].Y) * t);
            }
            for (var k = 5; k >= 0; k--)
            {
                var f = 1 - phase + k * 0.035;   // trail lies behind the head, towards the peer
                if (f > 1) continue;
                var r = (k == 0 ? 4 : 3 - k * 0.4) * 2.2;
                var sky = Palette.Sky;
                ctx.DrawEllipse(new RadialGradientBrush
                {
                    GradientStops = { new GradientStop(Color.FromArgb((byte)(255 * (0.9 - k * 0.15)), sky.R, sky.G, sky.B), 0), new GradientStop(Color.FromArgb(0, sky.R, sky.G, sky.B), 1) },
                }, null, AtLen(f), r, r);
            }
            ctx.DrawEllipse(B(Colors.White, 0.9), null, AtLen(1 - phase), 1.5, 1.5);
        }
    }

    /// <summary>Connector dots, drawn above the tiles where the connector meets them.</summary>
    sealed class DotLayer(DeviceGraph g) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            // Offline links first, so a lit dot is never covered by a grey one where two connectors share an exit.
            foreach (var i in Enumerable.Range(0, Math.Min(g.peers.Count, g.peerPos.Length)).OrderBy(i => g.peers[i].Online))
            {
                var l = g.Link(i);
                foreach (var e in new[] { l.A, l.B })
                    ctx.DrawEllipse(B(g.peers[i].Online ? P.Positive : P.Hairline), new Pen(B(P.Bg), 1), e, DotSize / 2 - 0.5, DotSize / 2 - 0.5);
            }
        }
    }

    static Control Node(GraphNode n)
    {
        var tile = new Border
        {
            Width = Tile, Height = Tile, CornerRadius = new CornerRadius(12),
            Background = B(n.Current ? Palette.Sky : P.Bg),
            BorderBrush = n.Current ? null : B(n.Online ? P.Positive : P.Hairline), BorderThickness = new Thickness(n.Current ? 0 : 1),
            BoxShadow = n.Current ? BoxShadows.Parse("0 0 4 0 #9BCCF1") : n.Online ? new BoxShadows(new BoxShadow { Blur = 4, Color = P.Positive }) : default,
            Child = n.More > 0 ? Body($"+{n.More}", 15).Center() : Icon(n.Current ? WindowsGlyph : DeviceGlyph(n.Pf, n.Name), 24, n.Current ? Colors.White : P.Text),
        };
        tile.Child.HorizontalAlignment = HorizontalAlignment.Center;
        var labels = Stack(4, Body(n.Name, 15).Trim(), Label(n.Detail, size: 11));
        labels.VerticalAlignment = VerticalAlignment.Center;
        labels.Margin = new Thickness(8, 0, 0, 0);
        var row = new DockPanel { Opacity = n.Online || n.Current ? 1 : 0.4, LastChildFill = true };
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);
        row.Children.Add(labels);
        return row;
    }
}

/// <summary>Figma Transfer Graph (75:3491): MB/s on the left (auto-scaled to a 1/2/5 step), progress 0–100 % along the
/// bottom, a filled area under the line and a dot at the current point.</summary>
sealed class TransferGraph : Control
{
    const double AxisW = 24, Gap = 8, PlotTop = 8, PlotH = 84, XLabelsGap = 4, LabelH = 10;
    readonly IReadOnlyList<SpeedSample> samples;

    public TransferGraph(IReadOnlyList<SpeedSample> samples)
    {
        this.samples = samples;
        Height = PlotTop + PlotH + XLabelsGap + LabelH;
        Avalonia.Automation.AutomationProperties.SetName(this, "Transfer speed graph");
    }

    /// <summary>A "nice" axis step (1, 2 or 5 × 10ⁿ) so four steps cover max.</summary>
    public static double NiceStep(double max)
    {
        var raw = Math.Max(max / 4, 0.25);
        var p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return new[] { 1.0, 2, 5, 10 }.Select(m => m * p).First(s => s >= raw - 1e-9);
    }

    static string Tick(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture) : v.ToString("0.0", CultureInfo.CurrentCulture);

    FormattedText Text(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Sans, FontStyle.Normal, FontWeight.Medium), 9, B(P.Text2));

    public override void Render(DrawingContext ctx)
    {
        var mbps = samples.Select(s => s.BytesPerSecond / (1 << 20)).ToList();
        var top = NiceStep(mbps.Count > 0 ? mbps.Max() : 1) * 4;
        double x0 = AxisW + Gap, w = Math.Max(1, Bounds.Width - x0), y0 = PlotTop + 5, y1 = PlotTop + PlotH - 5;
        var grid = new Pen(P.Dark ? B(Color.Parse("#141414")) : B(P.Hairline), 1);
        ctx.DrawRectangle(null, grid, new Rect(x0 + 0.5, PlotTop + 0.5, w - 1, PlotH - 1));
        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(y0 + (y1 - y0) * i / 4) + 0.5;
            ctx.DrawLine(grid, new Point(x0, y), new Point(x0 + w, y));
            var t = Text(Tick(top * (4 - i) / 4));
            ctx.DrawText(t, new Point(AxisW - t.Width, y - t.Height / 2));
        }
        for (var i = 1; i <= 3; i++)
        {
            var x = Math.Round(x0 + w * i / 4) + 0.5;
            ctx.DrawLine(grid, new Point(x, PlotTop), new Point(x, PlotTop + PlotH));
        }
        string[] xs = ["0%", "25%", "50%", "75%", "100%"];
        for (var i = 0; i < 5; i++)
        {
            var t = Text(xs[i]);
            var x = i == 0 ? x0 : i == 4 ? x0 + w - t.Width : x0 + w * i / 4 - t.Width / 2;
            ctx.DrawText(t, new Point(x, PlotTop + PlotH + XLabelsGap));
        }
        if (samples.Count == 0) return;
        var pts = samples.Select((s, i) => new Point(x0 + Math.Clamp(s.Progress, 0, 1) * w, y1 - (y1 - y0) * Math.Clamp(mbps[i] / top, 0, 1))).ToList();
        using (ctx.PushClip(new Rect(x0, PlotTop, w, PlotH)))
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(pts[0].X, PlotTop + PlotH), true);
                foreach (var p in pts) g.LineTo(p);
                g.LineTo(new Point(pts[^1].X, PlotTop + PlotH));
                g.EndFigure(true);
            }
            var minY = pts.Min(p => p.Y);
            ctx.DrawGeometry(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, minY, RelativeUnit.Absolute), EndPoint = new RelativePoint(0, y1, RelativeUnit.Absolute),
                GradientStops = { new GradientStop(P.Dark ? Color.Parse("#383838") : P.Hairline, 0), new GradientStop(P.Bg, 1) },
            }, null, area);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(pts[0], false);
                foreach (var p in pts.Skip(1)) g.LineTo(p);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(B(P.Text, 0.68), 1), line);
        }
        ctx.DrawEllipse(B(P.Dark ? Color.Parse("#DFDFDF") : P.Text), null, pts[^1], 4, 4);
    }
}
