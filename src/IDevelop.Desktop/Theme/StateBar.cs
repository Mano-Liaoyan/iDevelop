using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Theme;

/// <summary>How many of a workflow's nodes are in each state.</summary>
public readonly record struct StateCounts(int Completed, int Running, int Waiting, int Failed, int Stale, int Total)
{
    public bool HasRun => Completed + Running + Waiting + Failed + Stale > 0;

    /// <summary>
    /// "5 of 9 · 1 running · 3 wait · 1 failed · 2 stale", with the counts that are not zero, or "9 nodes" before any node ran,
    /// or "No nodes yet".
    /// </summary>
    public string Summary()
    {
        if (Total == 0)
        {
            return "No nodes yet";
        }

        if (!HasRun)
        {
            return Total == 1 ? "1 node" : $"{Total} nodes";
        }

        return string.Join(" · ", new[]
        {
            $"{Completed} of {Total}",
            Running > 0 ? $"{Running} running" : null,
            Waiting switch { 0 => null, 1 => "1 waits", var count => $"{count} wait" },
            Failed > 0 ? $"{Failed} failed" : null,
            Stale > 0 ? $"{Stale} stale" : null,
        }.OfType<string>());
    }
}

/// <summary>A thin bar with one segment per state, in the order completed, running, waiting, failed, stale, and then what has not run.</summary>
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

    public override void Render(DrawingContext context)
    {
        var counts = Counts;
        if (counts.Total == 0)
        {
            return;
        }

        var (width, height) = (Bounds.Width, Bounds.Height);
        context.DrawRectangle(Brush("StateNeutral10Brush"), null, new Rect(0, 0, width, height));
        var left = 0.0;
        foreach (var (count, brush) in new[]
        {
            (counts.Completed, "StateSuccessStrokeBrush"), (counts.Running, "StateRunningBrush"), (counts.Waiting, "StateWaitingStrokeBrush"),
            (counts.Failed, "StateFailedStrokeBrush"), (counts.Stale, "StateWarningStrokeBrush"),
        })
        {
            if (count > 0)
            {
                var segment = Math.Max(2, width * count / counts.Total);
                context.DrawRectangle(Brush(brush), null, new Rect(left, 0, Math.Min(segment, width - left), height));
                left += segment;
            }
        }
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
}
