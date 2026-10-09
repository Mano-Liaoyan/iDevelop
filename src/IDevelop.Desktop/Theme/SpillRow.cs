using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Theme;

/// <summary>Where a <see cref="SpillRow"/>'s controls sit once they leave its first line.</summary>
public enum SpillAlignment
{
    /// <summary>Under the text's start, as a header's attempt picker sits under its title.</summary>
    Start,

    /// <summary>At the row's right edge, as a composer's buttons end where the composer's box ends.</summary>
    End,
}

/// <summary>
/// A text and the controls after it, such as a hint and its buttons, laid out so that neither is cut short. While the text
/// keeps <see cref="MinLeadWidth"/> beside the controls, or all of its own width when that is less, they share one line,
/// each centred on it, and the controls end at the row's right edge. Otherwise the text takes the first line, and the
/// controls move under it in their order, on as many lines as they need. A control marked <see cref="PinnedProperty"/>
/// stays on the first line at the right edge either way, as a header's close button does. The first child is the text.
/// </summary>
public sealed class SpillRow : Panel
{
    public static readonly StyledProperty<double> MinLeadWidthProperty = AvaloniaProperty.Register<SpillRow, double>(nameof(MinLeadWidth), 160);

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<SpillRow, double>(nameof(Spacing), 8);

    public static readonly StyledProperty<double> LineSpacingProperty = AvaloniaProperty.Register<SpillRow, double>(nameof(LineSpacing), 8);

    public static readonly StyledProperty<SpillAlignment> ItemsAlignmentProperty =
        AvaloniaProperty.Register<SpillRow, SpillAlignment>(nameof(ItemsAlignment), SpillAlignment.End);

    /// <summary>How far in from the row's start the controls begin once they spill, as under a title that follows a glyph.</summary>
    public static readonly StyledProperty<double> SpillIndentProperty = AvaloniaProperty.Register<SpillRow, double>(nameof(SpillIndent));

    public static readonly AttachedProperty<bool> PinnedProperty = AvaloniaProperty.RegisterAttached<SpillRow, Control, bool>("Pinned");

    static SpillRow()
    {
        AffectsMeasure<SpillRow>(MinLeadWidthProperty, SpacingProperty, LineSpacingProperty, ItemsAlignmentProperty, SpillIndentProperty);
        AffectsParentMeasure<SpillRow>(PinnedProperty);
    }

    public double MinLeadWidth
    {
        get => GetValue(MinLeadWidthProperty);
        set => SetValue(MinLeadWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    public SpillAlignment ItemsAlignment
    {
        get => GetValue(ItemsAlignmentProperty);
        set => SetValue(ItemsAlignmentProperty, value);
    }

    public double SpillIndent
    {
        get => GetValue(SpillIndentProperty);
        set => SetValue(SpillIndentProperty, value);
    }

    public static bool GetPinned(Control control) => control.GetValue(PinnedProperty);

    public static void SetPinned(Control control, bool value) => control.SetValue(PinnedProperty, value);

    /// <summary>Whether the controls left the first line at the last layout.</summary>
    public bool IsSpilled { get; private set; }

    protected override Size MeasureOverride(Size availableSize) => Plan(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Plan(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Plan(double width, bool arrange)
    {
        var shown = Children.Where(child => child.IsVisible).ToArray();
        if (shown.Length == 0)
        {
            return default;
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var lead = Children[0].IsVisible ? Children[0] : null;
        var pinned = shown.Where(child => child != lead && GetPinned(child)).ToArray();
        var items = shown.Where(child => child != lead && !GetPinned(child)).ToArray();
        foreach (var child in pinned.Concat(items))
        {
            child.Measure(infinite);
        }

        var pinnedWidth = Run(pinned);
        var itemsWidth = Run(items);
        var open = double.IsInfinity(width);
        var leadNatural = 0.0;
        if (lead is not null)
        {
            lead.Measure(infinite);
            leadNatural = lead.DesiredSize.Width;
        }

        var after = Gap(pinnedWidth) + Gap(itemsWidth);
        var spilled = !open && items.Length > 0 && width - after < Math.Min(MinLeadWidth, leadNatural);
        IsSpilled = spilled;
        var leadWidth = open ? leadNatural : Math.Max(0, width - Gap(pinnedWidth) - (spilled ? 0 : Gap(itemsWidth)));
        lead?.Measure(new Size(leadWidth, double.PositiveInfinity));
        var firstLine = new[] { lead }.Concat(pinned).Concat(spilled ? [] : items).OfType<Control>().ToArray();
        var lineHeight = firstLine.Max(child => child.DesiredSize.Height);
        var rowWidth = open ? (lead?.DesiredSize.Width ?? 0) + after : width;

        if (arrange)
        {
            var right = rowWidth;
            foreach (var child in pinned.Reverse())
            {
                right -= child.DesiredSize.Width;
                Place(child, right, 0, lineHeight);
                right -= Spacing;
            }

            if (!spilled)
            {
                foreach (var child in items.Reverse())
                {
                    right -= child.DesiredSize.Width;
                    Place(child, right, 0, lineHeight);
                    right -= Spacing;
                }
            }

            if (lead is not null)
            {
                lead.Arrange(new Rect(0, (lineHeight - lead.DesiredSize.Height) / 2, leadWidth, lead.DesiredSize.Height));
            }
        }

        if (!spilled)
        {
            return new Size(rowWidth, lineHeight);
        }

        // The controls flow under the text in their order, each line as wide as the row.
        var top = lineHeight;
        var indent = ItemsAlignment == SpillAlignment.Start ? Math.Min(SpillIndent, width) : 0;
        foreach (var line in Lines(items, width - indent))
        {
            top += LineSpacing;
            var height = line.Max(child => child.DesiredSize.Height);
            if (arrange)
            {
                var left = ItemsAlignment == SpillAlignment.Start ? indent : width - Run(line);
                foreach (var child in line)
                {
                    Place(child, left, top, height);
                    left += child.DesiredSize.Width + Spacing;
                }
            }

            top += height;
        }

        return new Size(width, top);
    }

    private IEnumerable<Control[]> Lines(Control[] items, double width)
    {
        var line = new List<Control>();
        var used = 0.0;
        foreach (var child in items)
        {
            if (child.DesiredSize.Width > width)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
            }

            var needed = line.Count == 0 ? child.DesiredSize.Width : used + Spacing + child.DesiredSize.Width;
            if (line.Count > 0 && needed > width)
            {
                yield return [.. line];
                line.Clear();
                needed = child.DesiredSize.Width;
            }

            line.Add(child);
            used = needed;
        }

        if (line.Count > 0)
        {
            yield return [.. line];
        }
    }

    private double Run(IReadOnlyCollection<Control> children) =>
        children.Count == 0 ? 0 : children.Sum(child => child.DesiredSize.Width) + Spacing * (children.Count - 1);

    private double Gap(double run) => run > 0 ? run + Spacing : 0;

    private static void Place(Control child, double left, double top, double lineHeight) =>
        child.Arrange(new Rect(left, top + (lineHeight - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
}
