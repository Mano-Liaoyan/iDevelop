using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's one set of columns, which every row shares: a 14 px glyph or kind tile, a gap, the label, the value,
/// and a 24 px slot for a revert arrow, a state glyph, or a count, which a row of two buttons widens. The label column
/// keeps one width, so values start at one place at every width. The panel has two right edges: glyph buttons and rows of
/// buttons end at the panel's inset, and boxes and paragraphs end before the trailing column. A count ends where the
/// glyphs' drawing ends, <see cref="GlyphInkInset"/> inside the first.
/// </summary>
public sealed class InspectorGrid : Grid
{
    /// <summary>The label column, which holds "Conversation", the longest label beside a value.</summary>
    public const double LabelWidth = 76;

    /// <summary>The trailing column: one 24 px glyph button, such as a revert arrow.</summary>
    public const double TrailWidth = 24;

    /// <summary>The first column, a field's glyph or a kind tile.</summary>
    public const double GlyphWidth = 14;

    /// <summary>The gap between the first column and the label.</summary>
    public const double GlyphGap = 6;

    /// <summary>The height of a row's first line, a picker's height. A row's glyph, label, buttons, and a short value centre on it.</summary>
    public const double LineHeight = 28;

    /// <summary>
    /// The height of a label's line over its value, a glyph button's height, so the label sits 4 px above the value rather
    /// than floating in a picker-tall line.
    /// </summary>
    public const double StackedLineHeight = 24;

    /// <summary>The height of the line under a header's title, such as "Implement · Built-in, version 1".</summary>
    public const double SubtitleHeight = 16;

    /// <summary>
    /// How far a 16 px glyph's drawing ends before its 24 px button's edge: 4 px of the button and the 2 px its 16 unit frame
    /// keeps clear. A count ends this far before the action edge, so its digits end where the glyphs' ink ends.
    /// </summary>
    public const double GlyphInkInset = 6;

    /// <summary>A trailing count's margin, which ends its digits <see cref="GlyphInkInset"/> before the action edge.</summary>
    public static Thickness GlyphInkMargin { get; } = new(0, 0, GlyphInkInset, 0);

    /// <summary>
    /// The inspector width under which every value moves under its label: only the 280 px minimum. From this width a value
    /// sits beside its label, and a picker too narrow for a long model name ends it in an ellipsis, with the whole name in
    /// its tooltip and in its wider list.
    /// </summary>
    public const double NarrowPanelWidth = 300;

    /// <summary>
    /// Whether the inspector is narrower than <see cref="NarrowPanelWidth"/>, so every row puts its value under its label.
    /// The panel sets it and its rows inherit it, so they switch together, even a row inside a group.
    /// </summary>
    public static readonly AttachedProperty<bool> IsNarrowProperty =
        AvaloniaProperty.RegisterAttached<InspectorGrid, Control, bool>("IsNarrow", inherits: true);

    public InspectorGrid() => ColumnDefinitions =
    [
        new ColumnDefinition(GlyphWidth, GridUnitType.Pixel),
        new ColumnDefinition(GlyphGap, GridUnitType.Pixel),
        new ColumnDefinition(LabelWidth, GridUnitType.Pixel),
        new ColumnDefinition(1, GridUnitType.Star),
        new ColumnDefinition(GridLength.Auto) { MinWidth = TrailWidth },
    ];

    public static bool GetIsNarrow(Control control) => control.GetValue(IsNarrowProperty);

    public static void SetIsNarrow(Control control, bool value) => control.SetValue(IsNarrowProperty, value);
}
