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

    static RoutedConnection()
    {
        AffectsGeometry<RoutedConnection>(CurveProperty);
        AffectsRender<RoutedConnection>(CurveProperty);
        NodifyEditor.CuttingConnectionTypes.Add(typeof(RoutedConnection));
    }

    private const double ArrowPitch = 48;

    // How many straight steps stand for one piece while the moving arrows find their places.
    private const int Steps = 12;

    /// <summary>The wire's pieces, from its output port to its input port.</summary>
    public IReadOnlyList<WirePiece>? Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(StepConnection);

    protected override ((Point ArrowStartSource, Point ArrowStartTarget), (Point ArrowEndSource, Point ArrowEndTarget)) DrawLineGeometry(
        StreamGeometryContext context, Point source, Point target)
    {
        var pieces = Pieces(source, target);
        context.BeginFigure(pieces[0].Start, false);
        foreach (var piece in pieces)
        {
            context.CubicBezierTo(piece.First, piece.Second, piece.End);
        }

        context.EndFigure(false);
        return ((pieces[0].First, pieces[0].Start), (pieces[^1].Second, pieces[^1].End));
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
        pieces[0] = pieces[0] with { Start = source };
        pieces[^1] = pieces[^1] with { End = target };
        return pieces;
    }
}
