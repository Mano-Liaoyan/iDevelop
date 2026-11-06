using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Lays out the band along the canvas's bottom edge: the zoom controls at the left and the run bar after them. The run bar
/// centres on the canvas and moves aside only as far as it must to keep <see cref="CanvasChrome.Spacing"/> from the zoom
/// controls, and it never gets wider than the room left, so it never covers them. A run bar that needs more than that room
/// takes the tightChips class, whose styles leave its chips their counts and its Next its glyph, so no word is cut.
/// </summary>
public sealed class CanvasBottomBar : Panel
{
    public static readonly AttachedProperty<ChromeSlot> SlotProperty = AvaloniaProperty.RegisterAttached<CanvasBottomBar, Control, ChromeSlot>("Slot");

    static CanvasBottomBar() => AffectsParentMeasure<CanvasBottomBar>(SlotProperty);

    public static ChromeSlot GetSlot(Control control) => control.GetValue(SlotProperty);

    public static void SetSlot(Control control, ChromeSlot value) => control.SetValue(SlotProperty, value);

    private const string TightChips = "tightChips";

    // The width the run bar wanted when it last gave up its words, which it takes them back at.
    private double _wordsWidth;

    /// <summary>The run bar's chips changed, so it may fit with its words again.</summary>
    public void RefitChips()
    {
        _wordsWidth = 0;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (leading, center, trailing) = Parts();
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        leading?.Measure(infinite);
        trailing?.Measure(infinite);
        var (left, right) = Room(availableSize.Width, leading, trailing);
        if (center is not null)
        {
            Fit(center, Math.Max(0, right - left));
        }

        var height = new[] { leading, center, trailing }.Max(child => child?.DesiredSize.Height ?? 0);
        var width = double.IsInfinity(availableSize.Width)
            ? (leading?.DesiredSize.Width ?? 0) + (center?.DesiredSize.Width ?? 0) + (trailing?.DesiredSize.Width ?? 0) + 2 * CanvasChrome.Spacing
            : availableSize.Width;
        return new Size(width, height);
    }

    private void Fit(Control center, double room)
    {
        var tight = center.Classes.Contains(TightChips);
        if (tight && room >= _wordsWidth)
        {
            center.Classes.Set(TightChips, false);
            tight = false;
        }

        center.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (!tight && center.DesiredSize.Width > room)
        {
            _wordsWidth = center.DesiredSize.Width;
            center.Classes.Set(TightChips, true);
        }

        center.Measure(new Size(room, double.PositiveInfinity));
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
