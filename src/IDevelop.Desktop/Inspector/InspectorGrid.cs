using Avalonia;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's one set of columns, which the header, the filter, each section header, and every row share: a field's
/// glyph or a kind tile, a gap, the label, the value, and two 24 px slots for a row's glyph buttons or a count. The label
/// column keeps one width, so values start at one place at every width and stay beside their labels in a wide panel. The
/// panel has two right edges: glyph buttons and counts end at the panel's inset, and boxes end before the trailing column.
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
