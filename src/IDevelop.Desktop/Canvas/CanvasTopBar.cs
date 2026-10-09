using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Canvas;

/// <summary>The part of a <see cref="CanvasTopBar"/> or a <see cref="CanvasBottomBar"/> that a control takes.</summary>
public enum ChromeSlot
{
    /// <summary>At the left: the breadcrumb, which gives up width so that the top row's other controls keep theirs, or the zoom controls.</summary>
    Leading,

    /// <summary>At the right: the workflow's own commands, Run Workflow and Generate, or the minimap.</summary>
    Trailing,

    /// <summary>
    /// At the centre, moved aside only as far as it must to keep <see cref="CanvasChrome.Spacing"/> from both neighbours.
    /// When the top row has no room for it, the waiting pill moves under the trailing controls, its right edge on theirs.
    /// </summary>
    Center,

    /// <summary>Under the top row's leading control, as the status is, and never under or past the centre control.</summary>
    Below,
}

/// <summary>
/// Lays out what floats along the canvas's top edge, so that nothing there covers anything else at any width. Every control
/// of the first row centres on one <see cref="RowHeight"/> line.
/// </summary>
public sealed class CanvasTopBar : Panel
{
    /// <summary>The first row's line: the breadcrumb's height, on which the 32 px buttons and the 24 px pill centre.</summary>
    public const double RowHeight = 36;

    /// <summary>The room between the first row and what sits under it.</summary>
    public const double LineSpacing = 8;

    public static readonly AttachedProperty<ChromeSlot> SlotProperty = AvaloniaProperty.RegisterAttached<CanvasTopBar, Control, ChromeSlot>("Slot");

    static CanvasTopBar() => AffectsParentMeasure<CanvasTopBar>(SlotProperty);

    public static ChromeSlot GetSlot(Control control) => control.GetValue(SlotProperty);

    public static void SetSlot(Control control, ChromeSlot value) => control.SetValue(SlotProperty, value);

    /// <summary>Whether the centre control sits in the first row, rather than under the trailing controls, at the last layout.</summary>
    public bool IsInRow { get; private set; }

    protected override Size MeasureOverride(Size availableSize) => Plan(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Plan(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Plan(double width, bool arrange)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        Control? In(ChromeSlot slot) => Children.FirstOrDefault(child => child.IsVisible && GetSlot(child) == slot);
        var (leading, trailing, center, below) = (In(ChromeSlot.Leading), In(ChromeSlot.Trailing), In(ChromeSlot.Center), In(ChromeSlot.Below));
        var open = double.IsInfinity(width);

        trailing?.Measure(infinite);
        var trailingWidth = trailing?.DesiredSize.Width ?? 0;
        var leadingRoom = open ? double.PositiveInfinity : Math.Max(0, width - (trailingWidth > 0 ? trailingWidth + CanvasChrome.Spacing : 0));
        leading?.Measure(new Size(leadingRoom, double.PositiveInfinity));
        var leadingWidth = leading?.DesiredSize.Width ?? 0;
        if (open)
        {
            width = leadingWidth + CanvasChrome.Spacing + trailingWidth;
        }

        // The centre control centres on the canvas, and moves aside only as far as it must to clear its neighbours.
        center?.Measure(infinite);
        var centerWidth = center?.DesiredSize.Width ?? 0;
        var (first, last) = (leading is null ? 0 : leadingWidth + CanvasChrome.Spacing,
            width - (trailingWidth > 0 ? trailingWidth + CanvasChrome.Spacing : 0) - centerWidth);
        var centerLeft = Math.Clamp((width - centerWidth) / 2, first, Math.Max(first, last));
        IsInRow = center is not null && first <= last;
        var under = center is not null && !IsInRow;

        var rowHeight = new[] { RowHeight, leading?.DesiredSize.Height ?? 0, trailing?.DesiredSize.Height ?? 0, IsInRow ? center!.DesiredSize.Height : 0 }.Max();
        var belowRoom = Math.Max(0, width - (under ? centerWidth + CanvasChrome.Spacing : 0));
        below?.Measure(new Size(belowRoom, double.PositiveInfinity));
        var second = Math.Max(below?.DesiredSize.Height ?? 0, under ? center!.DesiredSize.Height : 0);

        if (arrange)
        {
            Centered(leading, 0, rowHeight);
            Centered(trailing, width - trailingWidth, rowHeight);
            if (IsInRow)
            {
                Centered(center, centerLeft, rowHeight);
            }
            else if (under)
            {
                center!.Arrange(new Rect(new Point(width - centerWidth, rowHeight + LineSpacing), center.DesiredSize));
            }

            below?.Arrange(new Rect(new Point(0, rowHeight + LineSpacing), below.DesiredSize));
        }

        return new Size(width, rowHeight + (second > 0 ? LineSpacing + second : 0));
    }

    private static void Centered(Control? child, double left, double rowHeight) =>
        child?.Arrange(new Rect(new Point(left, (rowHeight - child.DesiredSize.Height) / 2), child.DesiredSize));
}
