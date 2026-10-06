using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's one set of columns, which the header, the filter, each section header, and every row share, so the
/// panel has two right edges: actions end at the panel's inset, and values end before the trailing column.
/// </summary>
public sealed class InspectorGrid : Grid
{
    /// <summary>A field's glyph or a kind tile.</summary>
    public const int Glyph = 0;

    public const int Label = 2;

    public const int Value = 3;

    /// <summary>Two 24 px slots, such as a revert arrow and an info glyph, or Place and More.</summary>
    public const int Trail = 4;

    /// <summary>
    /// The width under which an editor moves below its label: a 320 px inspector, the default, less its two 12 px insets.
    /// Narrower, the value column would clip a picker's choice.
    /// </summary>
    public const double NarrowWidth = 296;

    public InspectorGrid() => ColumnDefinitions = new ColumnDefinitions("16,8,2*,3*,52");
}
