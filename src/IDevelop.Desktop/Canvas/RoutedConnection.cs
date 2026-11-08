using Avalonia;
using Avalonia.Media;
using Nodify;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A connection drawn along the curve <see cref="WireCurve"/> found. It takes every style a step connection takes, so its
/// stroke, dash, and emphasis stay in one place. Without a curve it draws the plain curve between its two ends.
/// </summary>
public sealed class RoutedConnection : StepConnection
{
    public static readonly StyledProperty<IReadOnlyList<WirePiece>?> CurveProperty =
        AvaloniaProperty.Register<RoutedConnection, IReadOnlyList<WirePiece>?>(nameof(Curve));

    public static readonly StyledProperty<bool> SourceOnEdgeProperty = AvaloniaProperty.Register<RoutedConnection, bool>(nameof(SourceOnEdge));

    public static readonly StyledProperty<bool> TargetOnEdgeProperty = AvaloniaProperty.Register<RoutedConnection, bool>(nameof(TargetOnEdge));

    static RoutedConnection()
    {
        AffectsGeometry<RoutedConnection>(SourceOnEdgeProperty, TargetOnEdgeProperty, StrokeThicknessProperty);
        AffectsGeometry<RoutedConnection>(CurveProperty);
        AffectsRender<RoutedConnection>(CurveProperty);
        NodifyEditor.CuttingConnectionTypes.Add(typeof(RoutedConnection));
    }

    private const double ArrowPitch = 48;

    // How many straight steps stand for one piece while the moving arrows find their places.
    private const int Steps = 12;

    // The socket's radius as a part of the wire's stroke, so it stays a dot at every zoom tier. A change of the stroke draws the geometry again.
    private const double SocketSize = 1.5;

    /// <summary>The wire's pieces, from its output port to its input port.</summary>
    public IReadOnlyList<WirePiece>? Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    /// <summary>The wire leaves at its own point on the card's edge, where no socket is, so it starts at the edge, past the offset.</summary>
    public bool SourceOnEdge
    {
        get => GetValue(SourceOnEdgeProperty);
        set => SetValue(SourceOnEdgeProperty, value);
    }

    /// <summary>The wire enters at its own point on the card's edge, so it ends at the edge.</summary>
    public bool TargetOnEdge
    {
        get => GetValue(TargetOnEdgeProperty);
        set => SetValue(TargetOnEdgeProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(StepConnection);

    protected override ((Point ArrowStartSource, Point ArrowStartTarget), (Point ArrowEndSource, Point ArrowEndTarget)) DrawLineGeometry(
        StreamGeometryContext context, Point source, Point target)
    {
        var pieces = Pieces(source, target);
        // A wire at its own point joins the card's edge with a short line to its socket, which stands in the column of the port's socket.
        context.BeginFigure(SourceOnEdge ? source - new Vector(SourceOffset.Width, 0) : pieces[0].Start, false);
        if (SourceOnEdge)
        {
            context.LineTo(pieces[0].Start);
        }

        foreach (var piece in pieces)
        {
            context.CubicBezierTo(piece.First, piece.Second, piece.End);
        }

        context.EndFigure(false);
        if (SourceOnEdge)
        {
            DrawSocket(context, pieces[0].Start);
        }

        return ((pieces[0].First, pieces[0].Start), (pieces[^1].Second, pieces[^1].End));
    }

    private void DrawSocket(StreamGeometryContext context, Point center)
    {
        var radius = StrokeThickness * SocketSize;
        context.BeginFigure(center - new Vector(radius, 0), true);
        context.ArcTo(center + new Vector(radius, 0), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        context.ArcTo(center - new Vector(radius, 0), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        context.EndFigure(true);
    }

    /// <summary>Draws the moving arrows along the curve, evenly spaced by length. Nodify's own arrows follow its step.</summary>
    protected override void DrawDirectionalArrowsGeometry(StreamGeometryContext context, Point source, Point target)
    {
        Point[] points = [.. Pieces(source, target).SelectMany(piece => Enumerable.Range(0, Steps).Select(step => piece.At((double)step / Steps))), target];
        var runs = points.Zip(points.Skip(1)).Select(run => (run.First, run.Second, Length: Point.Distance(run.First, run.Second))).Where(run => run.Length > 0).ToList();
        var total = runs.Sum(run => run.Length);
        // A short wire has room for fewer arrows, because arrows closer than this read as one mark.
        var count = Math.Clamp((int)(total / ArrowPitch), 1, DirectionalArrowsCount);
        for (var arrow = 0; arrow < count; arrow++)
        {
            var along = (arrow + DirectionalArrowsOffset) / count % 1 * total;
            foreach (var (first, second, length) in runs)
            {
                if (along <= length)
                {
                    var direction = (Vector)(first - second);
                    DrawDirectionalArrowheadGeometry(context, direction, first + (Vector)(second - first) * (along / length));
                    break;
                }

                along -= length;
            }
        }
    }

    // The curve meets the two ends Nodify gives, which sit at the edges of the port handles.
    private IReadOnlyList<WirePiece> Pieces(Point source, Point target)
    {
        if (Curve is not { Count: > 0 } curve)
        {
            return WireCurve.Plain(source, target);
        }

        WirePiece[] pieces = [.. curve];
        pieces[0] = pieces[0] with { Start = SourceOnEdge ? source - new Vector(SourceOffset.Width / 2, 0) : source };
        pieces[^1] = pieces[^1] with { End = TargetOnEdge ? target + new Vector(TargetOffset.Width, 0) : target };
        return pieces;
    }
}
