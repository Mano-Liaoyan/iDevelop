using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

public partial class NodeLinkRow : UserControl
{
    public static readonly StyledProperty<string?> LinkIdProperty = AvaloniaProperty.Register<NodeLinkRow, string?>(nameof(LinkId));

    public NodeLinkRow() => InitializeComponent();

    /// <summary>The name button's automation id: InputLink or OutputLink.</summary>
    public string? LinkId
    {
        get => GetValue(LinkIdProperty);
        set => SetValue(LinkIdProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinkIdProperty)
        {
            AutomationProperties.SetAutomationId(Link, LinkId);
        }
    }
}
