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

    /// <summary>At the centre of the bottom row, where the run bar is.</summary>
    Center,

    /// <summary>Under the top row's leading control, as the status is.</summary>
    Below,
}

/// <summary>
/// Lays out what floats along the canvas's top edge, so that nothing there covers anything else at any width. Every control
/// of the first row centres on one <see cref="RowHeight"/> line.
/// </summary>
public sealed class CanvasTopBar : Panel
{
    /// <summary>The first row's line, on which the breadcrumb and the buttons centre.</summary>
    private const double RowHeight = 32;

    /// <summary>The room between the first row and what sits under it.</summary>
    private const double LineSpacing = 8;

    public static readonly AttachedProperty<ChromeSlot> SlotProperty = AvaloniaProperty.RegisterAttached<CanvasTopBar, Control, ChromeSlot>("Slot");

    static CanvasTopBar() => AffectsParentMeasure<CanvasTopBar>(SlotProperty);

    public static ChromeSlot GetSlot(Control control) => control.GetValue(SlotProperty);

    public static void SetSlot(Control control, ChromeSlot value) => control.SetValue(SlotProperty, value);

    // What the last measure decided: each shown child's top left, its left edge given back from the right edge where
    // it keeps to that edge.
    private readonly List<(Control Child, double Left, bool FromRight, double Top)> _placements = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        _placements.Clear();
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        Control? In(ChromeSlot slot) => Children.FirstOrDefault(child => child.IsVisible && GetSlot(child) == slot);
        var (leading, trailing, below) = (In(ChromeSlot.Leading), In(ChromeSlot.Trailing), In(ChromeSlot.Below));
        var width = availableSize.Width;
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

        var rowHeight = new[] { RowHeight, leading?.DesiredSize.Height ?? 0, trailing?.DesiredSize.Height ?? 0 }.Max();
        below?.Measure(new Size(width, double.PositiveInfinity));
        var second = below?.DesiredSize.Height ?? 0;

        Centered(leading, 0, false, rowHeight);
        Centered(trailing, trailingWidth, true, rowHeight);
        if (below is not null)
        {
            _placements.Add((below, 0, false, rowHeight + LineSpacing));
        }

        return new Size(width, rowHeight + (second > 0 ? LineSpacing + second : 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, left, fromRight, top) in _placements)
        {
            child.Arrange(new Rect(new Point(fromRight ? finalSize.Width - left : left, top), child.DesiredSize));
        }

        return finalSize;
    }

    private void Centered(Control? child, double left, bool fromRight, double rowHeight)
    {
        if (child is not null)
        {
            _placements.Add((child, left, fromRight, (rowHeight - child.DesiredSize.Height) / 2));
        }
    }
}
