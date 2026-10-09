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
/// and the controls end at the row's right edge. Otherwise the text takes the first line, and the controls move under it
/// in their order, on as many lines as they need. Each control aligns itself in its line's height. A control marked
/// <see cref="PinnedProperty"/> stays on the first line at the right edge either way, as a header's close button does, and
/// one marked <see cref="BesideOnlyProperty"/> shows only while the controls share the first line. The first child is the
/// text, so the keyboard reaches the controls after it in the order they show.
/// The row decides all of this while it measures, and arranging only places what it decided.
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

    /// <summary>
    /// A control that repeats what another one says, such as a status that a picker's choice also names. It shows only
    /// while the controls share the first line, and gives way when they move under the text.
    /// </summary>
    public static readonly AttachedProperty<bool> BesideOnlyProperty = AvaloniaProperty.RegisterAttached<SpillRow, Control, bool>("BesideOnly");

    private readonly List<Placement> _placements = [];
    private double _leadInset;

    static SpillRow()
    {
        AffectsMeasure<SpillRow>(MinLeadWidthProperty, SpacingProperty, LineSpacingProperty, ItemsAlignmentProperty, SpillIndentProperty);
        AffectsParentMeasure<SpillRow>(PinnedProperty, BesideOnlyProperty);
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

    public static bool GetBesideOnly(Control control) => control.GetValue(BesideOnlyProperty);

    public static void SetBesideOnly(Control control, bool value) => control.SetValue(BesideOnlyProperty, value);

    /// <summary>Whether the controls left the first line at the last measure.</summary>
    public bool IsSpilled { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        _placements.Clear();
        var shown = Children.Where(child => child.IsVisible).ToArray();
        var lead = Children.Count > 0 && Children[0].IsVisible ? Children[0] : null;
        if (shown.Length == 0)
        {
            return default;
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var pinned = shown.Where(child => child != lead && GetPinned(child)).ToArray();
        var items = shown.Where(child => child != lead && !GetPinned(child)).ToArray();
        foreach (var child in pinned.Concat(items))
        {
            child.Measure(infinite);
        }

        var width = availableSize.Width;
        var open = double.IsInfinity(width);
        var leadNatural = 0.0;
        if (lead is not null)
        {
            lead.Measure(infinite);
            leadNatural = lead.DesiredSize.Width;
        }

        var (pinnedGap, itemsGap) = (Gap(Run(pinned)), Gap(Run(items)));
        IsSpilled = !open && items.Length > 0 && width - pinnedGap - itemsGap < Math.Min(MinLeadWidth, leadNatural);
        _leadInset = pinnedGap + (IsSpilled ? 0 : itemsGap);
        var leadWidth = open ? leadNatural : Math.Max(0, Math.Min(leadNatural, width - _leadInset));
        lead?.Measure(new Size(leadWidth, double.PositiveInfinity));

        var firstLine = new[] { lead }.Concat(pinned).Concat(IsSpilled ? [] : items).OfType<Control>().ToArray();
        var lineHeight = firstLine.Max(child => child.DesiredSize.Height);
        if (lead is not null)
        {
            _placements.Add(new(lead, 0, Placed.Lead, 0, lineHeight));
        }

        // Pinned controls, and the others while they share the first line, end at the row's right edge.
        var right = 0.0;
        foreach (var child in pinned.Reverse().Concat(IsSpilled ? [] : items.Reverse()))
        {
            right += child.DesiredSize.Width;
            _placements.Add(new(child, right, Placed.FromRight, 0, lineHeight));
            right += Spacing;
        }

        if (!IsSpilled)
        {
            // A row that is not stretched, such as a breadcrumb, is as wide as its content.
            var used = (lead?.DesiredSize.Width ?? 0) + _leadInset;
            return new Size(open ? used : Math.Min(width, used), lineHeight);
        }

        // The controls flow under the text in their order, each line as wide as the row, and one that only repeats
        // another gives way.
        foreach (var child in items.Where(GetBesideOnly))
        {
            _placements.Add(new(child, 0, Placed.Hidden, 0, 0));
        }

        var top = lineHeight;
        var start = ItemsAlignment == SpillAlignment.Start;
        var indent = start ? Math.Min(SpillIndent, width) : 0;
        foreach (var line in Lines([.. items.Where(child => !GetBesideOnly(child))], width - indent))
        {
            top += LineSpacing;
            var height = line.Max(child => child.DesiredSize.Height);
            var offset = start ? indent : Run(line);
            foreach (var child in line)
            {
                _placements.Add(new(child, offset, start ? Placed.FromLeft : Placed.FromRight, top, height));
                offset += (start ? 1 : -1) * (child.DesiredSize.Width + Spacing);
            }

            top += height;
        }

        return new Size(width, top);
    }

    // Each child gets its line's height and aligns itself in it, so a group that stretches, such as the breadcrumb's
    // buttons between their separators, fills the line. A row of one line gives it all the height it gets.
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, offset, placed, top, measured) in _placements)
        {
            var line = top == 0 && !IsSpilled ? Math.Max(measured, finalSize.Height) : measured;
            child.Arrange(placed switch
            {
                Placed.Hidden => default,
                Placed.Lead => new Rect(0, 0, Math.Max(0, finalSize.Width - _leadInset), line),
                Placed.FromRight => new Rect(finalSize.Width - offset, top, child.DesiredSize.Width, line),
                _ => new Rect(offset, top, child.DesiredSize.Width, line),
            });
        }

        return finalSize;
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

    private enum Placed
    {
        Lead,
        FromLeft,
        FromRight,
        Hidden,
    }

    /// <summary>
    /// Where a child goes: the text from the row's start, or a control by the distance of its left edge from the row's left
    /// or of its own left edge back from the row's right, on a line of the given top and height.
    /// </summary>
    private readonly record struct Placement(Control Child, double Offset, Placed Placed, double Top, double LineHeight);
}
