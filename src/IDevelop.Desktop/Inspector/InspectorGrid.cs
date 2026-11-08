using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's one set of columns, which every row shares: a 14 px glyph, kind tile, or revert arrow, a gap, the label,
/// the value, and a slot for what a row adds after its value, such as a state glyph, a count, or a blueprint's buttons. The
/// label column keeps one width up to a wide panel, so values start at one place. The panel has one right edge: boxes,
/// paragraphs, and buttons end at the panel's inset. A count ends where the glyphs' drawing ends, <see cref="GlyphInkInset"/> inside it.
/// </summary>
public sealed class InspectorGrid : Grid
{
    /// <summary>The label column, which holds "Conversation", the longest label beside a value.</summary>
    public const double LabelWidth = 84;

    /// <summary>The widest a value's box grows. In a wider panel the label column takes the rest, so a box never stretches.</summary>
    public const double MaxValueWidth = 360;

    /// <summary>A 24 px glyph button, such as a revert arrow, in the trailing column of a stacked row.</summary>
    public const double TrailWidth = 24;

    /// <summary>The first column, a field's glyph or a kind tile.</summary>
    public const double GlyphWidth = 14;

    /// <summary>The gap between the first column and the label.</summary>
    public const double GlyphGap = 6;

    /// <summary>The height of a row's first line, a picker's height. A row's glyph, label, buttons, and a short value centre on it.</summary>
    public const double LineHeight = 24;

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

    public InspectorGrid() => ColumnDefinitions =
    [
        new ColumnDefinition(GlyphWidth, GridUnitType.Pixel),
        new ColumnDefinition(GlyphGap, GridUnitType.Pixel),
        new ColumnDefinition(LabelWidth, GridUnitType.Pixel),
        new ColumnDefinition(1, GridUnitType.Star),
        new ColumnDefinition(GridLength.Auto),
    ];

    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsFinite(constraint.Width))
        {
            var label = Math.Max(LabelWidth, constraint.Width - GlyphWidth - GlyphGap - MaxValueWidth);
            if (ColumnDefinitions[2].Width.Value != label)
            {
                ColumnDefinitions[2].Width = new GridLength(label, GridUnitType.Pixel);
            }
        }

        return base.MeasureOverride(constraint);
    }
}
