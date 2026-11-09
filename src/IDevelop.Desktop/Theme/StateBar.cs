using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// A workflow's progress as one thin bar: a segment per state, in the rollup order, in the State Mark's colors, on a muted track.
/// The widths come from <see cref="StateCounts"/>.
/// </summary>
public sealed class StateBar : Control
{
    public static readonly StyledProperty<StateCounts> CountsProperty = AvaloniaProperty.Register<StateBar, StateCounts>(nameof(Counts));

    static StateBar() => AffectsRender<StateBar>(CountsProperty);

    public StateBar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public StateCounts Counts
    {
        get => GetValue(CountsProperty);
        set => SetValue(CountsProperty, value);
    }

    /// <summary>The segments of a bar of <paramref name="width"/> pixels, in the rollup order, each with its brush's key and its width.</summary>
    public static IReadOnlyList<(string Brush, double Width)> Segments(StateCounts counts, double width)
    {
        if (counts.Total == 0)
        {
            return [];
        }

        return new (string, int)[]
            {
                ("StateWaitingStrongBrush", counts.Waiting),
                ("StateFailedStrongBrush", counts.Failed),
                ("StateRunningBrush", counts.Running),
                ("StateWarningStrongBrush", counts.Stale),
                ("StateSuccessStrongBrush", counts.Completed),
            }
            .Where(segment => segment.Item2 > 0)
            .Select(segment => (segment.Item1, segment.Item2 * width / counts.Total))
            .ToList();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(bounds, 3)))
        {
            context.FillRectangle(Brush("SurfaceMutedBrush"), bounds);
            var x = 0.0;
            foreach (var (key, width) in Segments(Counts, bounds.Width))
            {
                context.FillRectangle(Brush(key), new Rect(x, 0, width, bounds.Height));
                x += width;
            }
        }
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
}
