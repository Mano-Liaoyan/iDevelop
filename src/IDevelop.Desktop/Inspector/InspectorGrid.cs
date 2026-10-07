using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's one set of columns, which the header, the filter, each section header, and every row share, so the
/// panel has two right edges: actions end at the panel's inset, and values end before the trailing column. The columns
/// are a field's glyph or a kind tile, a gap, the label, the value, and two 24 px slots for a row's glyph buttons.
/// </summary>
public sealed class InspectorGrid : Grid
{
    /// <summary>
    /// The width under which an editor moves below its label: a 320 px inspector, the default, less its two 12 px insets.
    /// Narrower, the value column would clip a picker's choice.
    /// </summary>
    public const double NarrowWidth = 296;

    public InspectorGrid() => ColumnDefinitions = new ColumnDefinitions("16,8,2*,3*,52");
}
