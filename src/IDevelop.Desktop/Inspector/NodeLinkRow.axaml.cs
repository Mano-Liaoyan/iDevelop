using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace IDevelop.Desktop.Inspector;

public partial class NodeLinkRow : UserControl
{
    public static readonly StyledProperty<string?> LinkIdProperty = AvaloniaProperty.Register<NodeLinkRow, string?>(nameof(LinkId));

    /// <summary>The row shows what the other agent handed on. A row of an agent's readers does not, because that text is the reader's own.</summary>
    public static readonly StyledProperty<bool> ShowsResultProperty = AvaloniaProperty.Register<NodeLinkRow, bool>(nameof(ShowsResult), true);

    /// <summary>The row shows the connection's kind on its two segments. A decision's inputs do not.</summary>
    public static readonly StyledProperty<bool> ShowsKindProperty = AvaloniaProperty.Register<NodeLinkRow, bool>(nameof(ShowsKind), true);

    public NodeLinkRow() => InitializeComponent();

    public bool ShowsResult
    {
        get => GetValue(ShowsResultProperty);
        set => SetValue(ShowsResultProperty, value);
    }

    public bool ShowsKind
    {
        get => GetValue(ShowsKindProperty);
        set => SetValue(ShowsKindProperty, value);
    }

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
