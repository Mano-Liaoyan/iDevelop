using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The Agent panel's one set of columns, which every row shares: a glyph or kind tile, a gap, the label, the value, and the
/// decorator column (Blender's), which holds an override dot, a reset, or a row's own control. A label without a glyph spans the
/// first three columns, 96 px. A page has two right edges (ADR 0021): the value edge, 36 px from the page's edge, and the action
/// edge, 12 px from it.
/// </summary>
public sealed class InspectorGrid : Grid
{
    /// <summary>The label column of a row with a glyph. With the glyph and the gap it makes 96 px.</summary>
    public const double LabelWidth = 76;

    /// <summary>The decorator column: from the value edge to the action edge.</summary>
    public const double TrailWidth = 24;

    /// <summary>The first column, a field's glyph or a kind tile.</summary>
    public const double GlyphWidth = 14;

    /// <summary>The gap between the first column and the label.</summary>
    public const double GlyphGap = 6;

    /// <summary>The height of a field control. A row's glyph, label, buttons, and a short value centre on it, and a row is 30 px high.</summary>
    public const double LineHeight = 26;

    /// <summary>The height of a label's line over its value.</summary>
    public const double StackedLineHeight = 24;

    /// <summary>The height of a page's title row.</summary>
    public const double PageTitleHeight = 32;

    public InspectorGrid() => ColumnDefinitions =
    [
        new ColumnDefinition(GlyphWidth, GridUnitType.Pixel),
        new ColumnDefinition(GlyphGap, GridUnitType.Pixel),
        new ColumnDefinition(LabelWidth, GridUnitType.Pixel),
        new ColumnDefinition(1, GridUnitType.Star),
        new ColumnDefinition(GridLength.Auto) { MinWidth = TrailWidth },
    ];
}
