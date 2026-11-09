using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Flow's wires, drawn under the bands and the cards. A wire is rounded at its corners; color and weight follow what it carries and
/// whether its node has the pointer or the selection (brief S2, execution language).
/// </summary>
public sealed class FlowWires : Control
{
    public static readonly StyledProperty<IReadOnlyList<FlowWire>?> WiresProperty = AvaloniaProperty.Register<FlowWires, IReadOnlyList<FlowWire>?>(nameof(Wires));

    private const double Corner = 6;

    static FlowWires() => AffectsRender<FlowWires>(WiresProperty);

    public FlowWires() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public IReadOnlyList<FlowWire>? Wires
    {
        get => GetValue(WiresProperty);
        set => SetValue(WiresProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        foreach (var wire in Wires ?? [])
        {
            context.DrawGeometry(null, PenOf(wire), Rounded(wire.Points));
        }
    }

    private Pen PenOf(FlowWire wire)
    {
        var (key, thickness) = wire switch
        {
            { Emphasis: WireEmphasis.Dimmed } => ("WireDimBrush", 1.5),
            { Flow: WireFlow.Feeding } => ("StateRunningBrush", 2.0),
            { Flow: WireFlow.Stopped } => ("StateFailedStrokeBrush", 1.5),
            { Emphasis: WireEmphasis.Highlighted } => ("TextMutedBrush", 2.0),
            _ => ("TextFaintBrush", 1.5),
        };
        var brush = this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
        return new Pen(brush ?? Brushes.Gray, thickness, wire.Kind == ConnectionKind.Context ? new DashStyle([4, 3], 0) : null, PenLineCap.Round);
    }

    private static StreamGeometry Rounded(ImmutableArray<Point> points)
    {
        var geometry = new StreamGeometry();
        using var line = geometry.Open();
        line.BeginFigure(points[0], false);
        for (var index = 1; index < points.Length - 1; index++)
        {
            var (before, at, after) = (points[index - 1], points[index], points[index + 1]);
            var radius = Math.Min(Corner, Math.Min(Distance(before, at), Distance(at, after)) / 2);
            line.LineTo(Along(at, before, radius));
            line.QuadraticBezierTo(at, Along(at, after, radius));
        }

        line.LineTo(points[^1]);
        line.EndFigure(false);
        return geometry;
    }

    private static double Distance(Point a, Point b) => Point.Distance(a, b);

    private static Point Along(Point from, Point to, double length)
    {
        var distance = Distance(from, to);
        return distance < 0.001 ? from : from + (to - from) * (length / distance);
    }
}
