using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Lays out the band along the canvas's bottom edge: the zoom controls at the left and the run bar after them. The run bar
/// centres on the canvas and moves aside only as far as it must to keep <see cref="CanvasChrome.Spacing"/> from the zoom
/// controls, and it never gets wider than the room left, so it never covers them. A bar that needs more than its width takes
/// the groupChips class, whose styles sum the reasons to wait, then the tightView class, whose styles move the view's own
/// controls to the overflow menu, and then the tightChips class, whose styles name what needs the person and sum the rest in
/// words, so no word is cut.
/// </summary>
public sealed class CanvasBottomBar : Panel
{
    public static readonly AttachedProperty<ChromeSlot> SlotProperty = AvaloniaProperty.RegisterAttached<CanvasBottomBar, Control, ChromeSlot>("Slot");

    static CanvasBottomBar() => AffectsParentMeasure<CanvasBottomBar>(SlotProperty);

    public static ChromeSlot GetSlot(Control control) => control.GetValue(SlotProperty);

    public static void SetSlot(Control control, ChromeSlot value) => control.SetValue(SlotProperty, value);

    // Each step gives the run bar less to show. The first step is no class.
    private static readonly string[] Steps = ["groupChips", "tightView", "tightChips"];

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

        var height = new[] { leading, center, trailing }.Max(child => child?.DesiredSize.Height ?? 0);
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
            center.Measure(infinite);
        }

        double Wanted() => center.DesiredSize.Width + (leading?.DesiredSize.Width ?? 0);
        double Room() => Math.Max(0, Bar.Room(width, leading, trailing).Right - Bar.Room(width, leading, trailing).Left) + (leading?.DesiredSize.Width ?? 0);

        Measure();
        while (_step > 0 && Room() >= _wanted[_step - 1])
        {
            _step--;
            Classes.Set(Steps[_step], false);
            Measure();
        }

        while (_step < Steps.Length && Wanted() > Room())
        {
            _wanted[_step] = Wanted();
            Classes.Set(Steps[_step], true);
            _step++;
            Measure();
        }

        var (left, right) = Bar.Room(width, leading, trailing);
        center.Measure(new Size(Math.Max(0, right - left), double.PositiveInfinity));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (leading, center, trailing) = Parts();
        var (left, right) = Bar.Room(finalSize.Width, leading, trailing);
        Bottom(leading, 0, finalSize.Height);
        Bottom(trailing, finalSize.Width - (trailing?.DesiredSize.Width ?? 0), finalSize.Height);
        if (center is not null)
        {
            var width = Math.Min(center.DesiredSize.Width, Math.Max(0, right - left));
            var centered = (finalSize.Width - width) / 2;
            center.Arrange(new Rect(Math.Max(left, Math.Min(centered, right - width)), finalSize.Height - center.DesiredSize.Height, width, center.DesiredSize.Height));
        }

        return finalSize;
    }

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
