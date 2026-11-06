using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's pinned header: a tile in the first column, then a title line and a kind line of fixed heights, so every
/// header is as tall until a long title wraps. The title ends at the value edge, before the trailing column, where More sits
/// on its line. The kind line, with nothing after it, takes the trailing column too.
/// </summary>
public sealed class InspectorHeader : Grid
{
    public InspectorHeader()
    {
        ColumnDefinitions = new ColumnDefinitions($"Auto,10,*,{InspectorGrid.TrailWidth}");
        RowDefinitions =
        [
            new RowDefinition(GridLength.Auto) { MinHeight = InspectorGrid.LineHeight },
            new RowDefinition(GridLength.Auto) { MinHeight = InspectorGrid.SubtitleHeight },
        ];
        Margin = new Thickness(12, 12, 12, 8);
        AutomationProperties.SetAutomationId(this, "InspectorHeader");
    }
}
