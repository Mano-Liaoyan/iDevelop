using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's one set of columns, which the header, the filter, each section header, and every row share: a field's
/// glyph or a kind tile, a gap, the label, the value, and two 24 px slots for a row's glyph buttons or a count. The label
/// column keeps one width, so values start at one place at every width and stay beside their labels in a wide panel. The
/// panel has two right edges: glyph buttons and rows of buttons end at the panel's inset, and boxes and paragraphs end
/// before the trailing column. A count ends where the glyphs' drawing ends, <see cref="GlyphInkInset"/> inside the first.
/// </summary>
public sealed class InspectorGrid : Grid
{
    /// <summary>The label column: "Conversation", the longest label beside a value, and an 8 px gap.</summary>
    public const double LabelWidth = 88;

    /// <summary>The trailing column: two 24 px glyph buttons 4 px apart, such as a revert arrow and an info glyph.</summary>
    public const double TrailWidth = 52;

    /// <summary>The height of a row's first line, a picker's height. A row's glyph, label, buttons, and a short value centre on it.</summary>
    public const double LineHeight = 28;

    /// <summary>
    /// The height of a label's line over its value, a glyph button's height, so the label sits 4 px above the value rather
    /// than floating in a picker-tall line.
    /// </summary>
    public const double StackedLineHeight = 24;

    /// <summary>The height of the line under a header's title, such as "Implement · Built-in, version 1".</summary>
    public const double SubtitleHeight = 18;

    /// <summary>
    /// How far a 16 px glyph's drawing ends before its 24 px button's edge: 4 px of the button and the 2 px its 16 unit frame
    /// keeps clear. A count ends this far before the action edge, so its digits end where the glyphs' ink ends.
    /// </summary>
    public const double GlyphInkInset = 6;

    /// <summary>The trailing column's width as a grid length, for a header's own grid, whose title stops before it.</summary>
    public static GridLength TrailColumn { get; } = new(TrailWidth);

    /// <summary>A trailing count's margin, which ends its digits <see cref="GlyphInkInset"/> before the action edge.</summary>
    public static Thickness GlyphInkMargin { get; } = new(0, 0, GlyphInkInset, 0);

    /// <summary>
    /// The inspector width under which every value moves under its label, the 320 px default among them. From this width
    /// the value column holds a picker showing the longest model name a client offers, Pi's "DeepSeek V4.1 Flash
    /// (deepseek)", about 190 px of text and 44 px of the picker's own, with room to spare for another platform's font.
    /// </summary>
    public const double NarrowPanelWidth = 440;

    /// <summary>
    /// Whether the inspector is narrower than <see cref="NarrowPanelWidth"/>, so every row puts its value under its label.
    /// The panel sets it and its rows inherit it, so they switch together, even a row inside a group.
    /// </summary>
    public static readonly AttachedProperty<bool> IsNarrowProperty =
        AvaloniaProperty.RegisterAttached<InspectorGrid, Control, bool>("IsNarrow", inherits: true);

    public InspectorGrid() => ColumnDefinitions = new ColumnDefinitions($"16,8,{LabelWidth},*,{TrailWidth}");

    public static bool GetIsNarrow(Control control) => control.GetValue(IsNarrowProperty);

    public static void SetIsNarrow(Control control, bool value) => control.SetValue(IsNarrowProperty, value);
}
