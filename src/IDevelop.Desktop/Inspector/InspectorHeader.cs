using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// The inspector's pinned header: a 24 px tile, a title line, and a kind line, then on a node its status, its actions, and
/// its detail, one row each. The title ends before More. The kind line, with nothing after it, takes More's column too.
/// </summary>
public sealed class InspectorHeader : Grid
{
    /// <summary>The tile's size and the width of More.</summary>
    public const double TileSize = 24;

    /// <summary>The gap between the status, the actions, and the detail.</summary>
    public const double RowGap = 8;

    /// <summary>The header gives up its title's line and keeps only its state's, whose row then takes no room of its own.</summary>
    public static readonly StyledProperty<bool> IsBriefProperty = AvaloniaProperty.Register<InspectorHeader, bool>(nameof(IsBrief));

    public bool IsBrief
    {
        get => GetValue(IsBriefProperty);
        set => SetValue(IsBriefProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsBriefProperty)
        {
            RowDefinitions[1].MinHeight = IsBrief ? 0 : InspectorGrid.SubtitleHeight;
        }
    }

    public InspectorHeader()
    {
        ColumnDefinitions = new ColumnDefinitions($"{TileSize},{RowGap},*,{TileSize}");
        RowDefinitions =
        [
            new RowDefinition(GridLength.Auto) { MinHeight = TileSize },
            new RowDefinition(GridLength.Auto) { MinHeight = InspectorGrid.SubtitleHeight },
            new RowDefinition(GridLength.Auto),
            new RowDefinition(GridLength.Auto),
            new RowDefinition(GridLength.Auto),
        ];
        Margin = new Thickness(12, 12, 12, 8);
        AutomationProperties.SetAutomationId(this, "InspectorHeader");
    }
}
