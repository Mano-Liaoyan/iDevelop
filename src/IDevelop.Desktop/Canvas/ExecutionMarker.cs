using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The triangle over a running node: 14 by 8 on screen, 4 above the card, at every zoom. It draws from its own origin, the
/// card's top center, and takes no layout space.
/// </summary>
public sealed class ExecutionMarker : Control
{
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<ExecutionMarker, double>(nameof(Zoom), 1);

    private static readonly Geometry Triangle = Geometry.Parse("M-7,-12 L7,-12 L0,-4 Z");

    static ExecutionMarker() => AffectsRender<ExecutionMarker>(ZoomProperty);

    public ExecutionMarker() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    /// <summary>The canvas's zoom, which the marker undoes.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Zoom > 0 && this.TryFindResource("StateRunningBrush", ActualThemeVariant, out var found) && found is IBrush brush)
        {
            using (context.PushTransform(Matrix.CreateScale(1 / Zoom, 1 / Zoom)))
            {
                context.DrawGeometry(brush, null, Triangle);
            }
        }
    }
}
