using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The Agent panel's 56 px header. A 24 px tile, a name on one line, and a second line under the name, with the actions at the
/// right edge across both lines. Each page shares it, so its height never changes.
/// </summary>
public sealed class AgentPanelHeader : Grid
{
    public const double Height56 = 56;

    public const double TileSize = 24;

    public AgentPanelHeader()
    {
        ColumnDefinitions = new ColumnDefinitions($"{TileSize},8,*,Auto");
        RowDefinitions = new RowDefinitions("20,16");
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Margin = new Thickness(12, 0, 8, 0);
        AutomationProperties.SetAutomationId(this, "InspectorHeader");
    }
}
