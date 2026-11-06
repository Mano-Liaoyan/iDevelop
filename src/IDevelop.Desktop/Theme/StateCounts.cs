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

/// <summary>A small ring beside a workflow's count that closes as its nodes complete.</summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<StateCounts> CountsProperty = AvaloniaProperty.Register<ProgressRing, StateCounts>(nameof(Counts));

    private const double Stroke = 2;

    static ProgressRing() => AffectsRender<ProgressRing>(CountsProperty);

    public ProgressRing() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

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

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (Math.Min(Bounds.Width, Bounds.Height) - Stroke) / 2;
        var line = new Pen(Brush("StateNeutralStrokeBrush"), Stroke);
        var share = (double)counts.Completed / counts.Total;
        context.DrawEllipse(null, new Pen(Brush("StateNeutral10Brush"), Stroke), center, radius, radius);
        if (share >= 1)
        {
            context.DrawEllipse(null, line, center, radius, radius);
        }
        else if (share > 0)
        {
            var angle = share * 2 * Math.PI;
            var end = center + new Vector(Math.Sin(angle), -Math.Cos(angle)) * radius;
            var arc = new StreamGeometry();
            using (var path = arc.Open())
            {
                path.BeginFigure(center - new Vector(0, radius), false);
                path.ArcTo(end, new Size(radius, radius), 0, share > 0.5, SweepDirection.Clockwise);
                path.EndFigure(false);
            }

            context.DrawGeometry(null, line, arc);
        }
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
}
