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

    // The curve meets the two ends Nodify gives, the sockets' centers.
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
