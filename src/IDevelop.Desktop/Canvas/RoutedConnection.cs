using Avalonia;
using Avalonia.Media;
using Nodify;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// A step connection that turns at the corners <see cref="WireRouting"/> found. It takes every style a step connection
/// takes, so its stroke, dash, and emphasis stay the same.
/// </summary>
public sealed class RoutedConnection : StepConnection
{
    public static readonly StyledProperty<IReadOnlyList<Point>?> RouteProperty =
        AvaloniaProperty.Register<RoutedConnection, IReadOnlyList<Point>?>(nameof(Route));

    static RoutedConnection()
    {
        AffectsGeometry<RoutedConnection>(RouteProperty);
        AffectsRender<RoutedConnection>(RouteProperty);
        NodifyEditor.CuttingConnectionTypes.Add(typeof(RoutedConnection));
    }

    /// <summary>The corners from the end of the source stub to the start of the target stub. Without them it draws Nodify's step.</summary>
    public IReadOnlyList<Point>? Route
    {
        get => GetValue(RouteProperty);
        set => SetValue(RouteProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(StepConnection);

    protected override ((Point ArrowStartSource, Point ArrowStartTarget), (Point ArrowEndSource, Point ArrowEndTarget)) DrawLineGeometry(
        StreamGeometryContext context, Point source, Point target)
    {
        if (Route is not { Count: > 0 } route)
        {
            return base.DrawLineGeometry(context, source, target);
        }

        Point[] points = [source, .. route, target];
        context.BeginFigure(source, false);
        for (var corner = 1; corner < points.Length - 1; corner++)
        {
            AddSmoothCorner(context, points[corner - 1], points[corner], points[corner + 1], CornerRadius);
        }

        context.LineTo(target);
        context.EndFigure(false);
        return ((target, source), (source, target));
    }
}
