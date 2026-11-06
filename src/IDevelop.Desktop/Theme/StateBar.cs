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
    /// "5 of 9 · 1 running · 3 waiting · 1 failed · 2 stale", with the counts that are not zero, or "9 nodes" before any node ran,
    /// or "No nodes yet".
    /// </summary>
    public string Progress => !HasRun ? Summary() : $"{Completed} of {Total}";

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
            Waiting > 0 ? $"{Waiting} waiting" : null,
            Failed > 0 ? $"{Failed} failed" : null,
            Stale > 0 ? $"{Stale} stale" : null,
        }.OfType<string>());
    }
}

/// <summary>A thin line that shows how many of the nodes completed. The row beside it says what needs the person.</summary>
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
        context.DrawRectangle(Brush("StateNeutralStrokeBrush"), null, new Rect(0, 0, width * counts.Completed / counts.Total, height));
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
}
