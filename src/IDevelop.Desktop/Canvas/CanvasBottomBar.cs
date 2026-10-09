using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Lays out what floats along the canvas's bottom edge, which every control there shares: the zoom controls at the left,
/// the minimap at the right, and the run bars between them. The run bars centre on the canvas and move aside only as far
/// as they must to keep <see cref="CanvasChrome.Spacing"/> from either corner, and they never get wider than the room
/// between the corners, so they never leave the bottom edge or cover a corner.
/// </summary>
public sealed class CanvasBottomBar : Panel
{
    public static readonly AttachedProperty<ChromeSlot> SlotProperty = AvaloniaProperty.RegisterAttached<CanvasBottomBar, Control, ChromeSlot>("Slot");

    static CanvasBottomBar() => AffectsParentMeasure<CanvasBottomBar>(SlotProperty);

    public static ChromeSlot GetSlot(Control control) => control.GetValue(SlotProperty);

    public static void SetSlot(Control control, ChromeSlot value) => control.SetValue(SlotProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var (leading, center, trailing) = Parts();
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        leading?.Measure(infinite);
        trailing?.Measure(infinite);
        var (left, right) = Room(availableSize.Width, leading, trailing);
        center?.Measure(new Size(Math.Max(0, right - left), double.PositiveInfinity));
        var height = new[] { leading, center, trailing }.Max(child => child?.DesiredSize.Height ?? 0);
        var width = double.IsInfinity(availableSize.Width)
            ? (leading?.DesiredSize.Width ?? 0) + (center?.DesiredSize.Width ?? 0) + (trailing?.DesiredSize.Width ?? 0) + 2 * CanvasChrome.Spacing
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (leading, center, trailing) = Parts();
        var (left, right) = Room(finalSize.Width, leading, trailing);
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

    private static (double Left, double Right) Room(double width, Control? leading, Control? trailing) =>
        (leading is null ? 0 : leading.DesiredSize.Width + CanvasChrome.Spacing,
         width - (trailing is null ? 0 : trailing.DesiredSize.Width + CanvasChrome.Spacing));

    private static void Bottom(Control? child, double left, double height) =>
        child?.Arrange(new Rect(new Point(left, height - child.DesiredSize.Height), child.DesiredSize));
}
