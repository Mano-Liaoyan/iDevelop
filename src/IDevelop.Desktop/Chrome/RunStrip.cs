using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Chrome;

/// <summary>
/// The Run Strip's row: the progress bar, the count, and the pills, side by side and vertically centered. A strip that does not fit
/// its room first drops the pills' words, which their tooltips keep, and then shortens the bar.
/// </summary>
public sealed class RunStrip : Panel
{
    private const double Gap = 8;
    private const string TightPills = "tightPills";
    private const string TightBar = "tightBar";

    // The natural width at each step, measured once for each set of pills. A change in the pills measures them again.
    private readonly double[] _widths = new double[3];
    private string? _measured;
    private int _step;

    protected override Size MeasureOverride(Size availableSize)
    {
        var key = PillsKey();
        if (key != _measured)
        {
            _measured = key;
            for (var step = 0; step < _widths.Length; step++)
            {
                Apply(step);
                _widths[step] = Natural();
            }
        }

        var chosen = Array.FindIndex(_widths, width => width <= availableSize.Width);
        Apply(chosen < 0 ? _widths.Length - 1 : chosen);
        Natural();
        var shown = Children.Where(child => child.IsVisible && child.DesiredSize.Width > 0).ToList();
        return new Size(
            shown.Sum(child => child.DesiredSize.Width) + Gap * Math.Max(0, shown.Count - 1),
            Children.Max(child => child.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = 0.0;
        foreach (var child in Children.Where(child => child.IsVisible && child.DesiredSize.Width > 0))
        {
            child.Arrange(new Rect(left, (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
            left += child.DesiredSize.Width + Gap;
        }

        return finalSize;
    }

    // The pills' classes reach controls deep in the strip, and a control tells only its parent that it changed, so the strip asks each one again.
    private double Natural()
    {
        foreach (var control in this.GetVisualDescendants().OfType<Layoutable>())
        {
            control.InvalidateMeasure();
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children)
        {
            child.Measure(infinite);
        }

        var shown = Children.Where(child => child.IsVisible && child.DesiredSize.Width > 0).ToList();
        return shown.Sum(child => child.DesiredSize.Width) + Gap * Math.Max(0, shown.Count - 1);
    }

    private void Apply(int step)
    {
        if (step != _step)
        {
            _step = step;
            Classes.Set(TightPills, step >= 1);
            Classes.Set(TightBar, step >= 2);
        }
    }

    private string PillsKey() => string.Join("|", (DataContext as WorkflowCanvasViewModel)?.RunPills.Select(pill => pill.Label) ?? []) + Bounds.Height;
}
