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

    private const double ArrowPitch = 48;

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

    /// <summary>Draws the moving arrows along the corners, evenly spaced by length. Nodify's own arrows follow its step.</summary>
    protected override void DrawDirectionalArrowsGeometry(StreamGeometryContext context, Point source, Point target)
    {
        if (Route is not { Count: > 0 } route)
        {
            base.DrawDirectionalArrowsGeometry(context, source, target);
            return;
        }

        Point[] points = [source, .. route, target];
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
}
