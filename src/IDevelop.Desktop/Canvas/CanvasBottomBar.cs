using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Lays out the band along the canvas's bottom edge: the zoom controls at the left and the run bar after them, at
/// <see cref="CanvasChrome.Spacing"/> from the zoom controls, and it never gets wider than the room left, so it never covers them.
/// A bar that needs more than its width takes the tightView class, whose styles move the view's own controls to the overflow
/// menu, then the tightFollow class, whose styles move Follow to the overflow menu, then the tightNext class, whose Next
/// says only "Next" and keeps its whole name in its tooltip and accessible name, and then the ownRow class: the run bar takes
/// a row of its own across the whole canvas above the zoom controls, which take back their view controls and Follow. Last the
/// tightOwnRow class moves Follow to the overflow menu again, and the bar wraps its groups to a second line when they still do
/// not fit. No step takes a chip's words: every count keeps its word, and the bar takes two lines before it gives one up
/// (ADR 0069). The run bar measures at its whole width, so a change in its text reaches this panel.
/// </summary>
public sealed class CanvasBottomBar : Panel
{
    public static readonly AttachedProperty<ChromeSlot> SlotProperty = AvaloniaProperty.RegisterAttached<CanvasBottomBar, Control, ChromeSlot>("Slot");

    static CanvasBottomBar() => AffectsParentMeasure<CanvasBottomBar>(SlotProperty);

    public static ChromeSlot GetSlot(Control control) => control.GetValue(SlotProperty);

    public static void SetSlot(Control control, ChromeSlot value) => control.SetValue(SlotProperty, value);

    // Each step gives the run bar less to show. The first step is no class.
    private static readonly string[] Steps = ["tightView", "tightFollow", "tightNext", OwnRow, "tightOwnRow"];

    private const string OwnRow = "ownRow";

    private static readonly int OwnRowStep = Array.IndexOf(Steps, OwnRow);

    /// <summary>The room between the run bar's own row and the zoom controls' row.</summary>
    private const double RowGap = 8;

    // The steps the bar has taken, and the width it wanted before each one, which it takes that step back at.
    private readonly double[] _wanted = new double[Steps.Length];
    private int _step;

    /// <summary>The run bar's chips changed, so it may fit with more words again.</summary>
    public void RefitChips()
    {
        Array.Clear(_wanted);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (leading, center, trailing) = Parts();
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        leading?.Measure(infinite);
        trailing?.Measure(infinite);
        if (center is not null)
        {
            Fit(center, leading, trailing, availableSize.Width);
        }

        var row = new[] { leading, trailing }.Max(child => child?.DesiredSize.Height ?? 0);
        var height = center is null ? row
            : IsOwnRow ? row + RowGap + center.DesiredSize.Height
            : Math.Max(row, center.DesiredSize.Height);
        var width = double.IsInfinity(availableSize.Width)
            ? (leading?.DesiredSize.Width ?? 0) + (center?.DesiredSize.Width ?? 0) + (trailing?.DesiredSize.Width ?? 0) + 2 * CanvasChrome.Spacing
            : availableSize.Width;
        return new Size(width, height);
    }

    private void Fit(Control center, Control? leading, Control? trailing, double width)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        void Measure()
        {
            leading?.Measure(infinite);
            center.Measure(IsOwnRow ? new Size(width, double.PositiveInfinity) : infinite);
        }

        // A class reaches controls deep in the run bar, and a control tells only its parent that it changed, so the bar asks each
        // one again. Without this the bar reads its old width, takes a step it does not need, and keeps it.
        void Remeasure()
        {
            foreach (var control in center.GetVisualDescendants().OfType<Layoutable>().Append(center))
            {
                control.InvalidateMeasure();
            }

            Measure();
        }

        // Beside the zoom controls the bar wants its width and theirs. On its own row it wants its width on one line, and its room is the canvas's.
        double Wanted()
        {
            if (!IsOwnRow)
            {
                return center.DesiredSize.Width + (leading?.DesiredSize.Width ?? 0);
            }

            center.Measure(infinite);
            var natural = center.DesiredSize.Width;
            center.Measure(new Size(width, double.PositiveInfinity));
            return natural;
        }

        double Room(int step) => step > OwnRowStep ? width
            : Math.Max(0, Bar.Room(width, leading, trailing).Right - Bar.Room(width, leading, trailing).Left) + (leading?.DesiredSize.Width ?? 0);

        Measure();
        while (_step > 0 && Room(_step - 1) >= _wanted[_step - 1])
        {
            _step--;
            Classes.Set(Steps[_step], false);
            Remeasure();
        }

        while (_step < Steps.Length && Wanted() > Room(_step))
        {
            _wanted[_step] = Wanted();
            Classes.Set(Steps[_step], true);
            _step++;
            Remeasure();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (leading, center, trailing) = Parts();
        var (left, right) = Bar.Room(finalSize.Width, leading, trailing);
        Bottom(leading, 0, finalSize.Height);
        Bottom(trailing, finalSize.Width - (trailing?.DesiredSize.Width ?? 0), finalSize.Height);
        if (center is not null && IsOwnRow)
        {
            // The run bar's own row spans the canvas above the zoom controls.
            center.Arrange(new Rect(0, 0, finalSize.Width, center.DesiredSize.Height));
        }
        else if (center is not null)
        {
            // The run bar sits right after the zoom controls at every width, so it never moves as the canvas grows.
            var width = Math.Min(center.DesiredSize.Width, Math.Max(0, right - left));
            center.Arrange(new Rect(left, finalSize.Height - center.DesiredSize.Height, width, center.DesiredSize.Height));
        }

        return finalSize;
    }

    private bool IsOwnRow => Classes.Contains(OwnRow);

    private (Control? Leading, Control? Center, Control? Trailing) Parts()
    {
        Control? In(ChromeSlot slot) => Children.FirstOrDefault(child => child.IsVisible && GetSlot(child) == slot);
        return (In(ChromeSlot.Leading), In(ChromeSlot.Center), In(ChromeSlot.Trailing));
    }

    private static void Bottom(Control? child, double left, double height) =>
        child?.Arrange(new Rect(new Point(left, height - child.DesiredSize.Height), child.DesiredSize));
}

file static class Bar
{
    /// <summary>The span between the bar's two ends, for the run bar.</summary>
    public static (double Left, double Right) Room(double width, Control? leading, Control? trailing) =>
        (leading is null ? 0 : leading.DesiredSize.Width + CanvasChrome.Spacing,
         width - (trailing is null ? 0 : trailing.DesiredSize.Width + CanvasChrome.Spacing));
}
