using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace IDevelop.Desktop.Inspector;

/// <summary>How a row places its label and its editor.</summary>
public enum RowLayout
{
    /// <summary>The label on the left at 40 percent, the editor on the right.</summary>
    Columns,

    /// <summary>The label on its own line, the editor under it at full width, for text of several lines.</summary>
    Stacked,

    /// <summary>No label shown. The content takes the full width, and the label, if any, only matches the filter.</summary>
    Full,
}

/// <summary>
/// One property in an inspector section: its label, its editor, a revert arrow while the value differs from the
/// blueprint's default, and an info glyph whose tooltip holds what a paragraph used to say. While the filter has text,
/// a row shows when its label or its section's title matches. A row without a label shows only with its section's title.
/// </summary>
public sealed class InspectorRow : InspectorPart
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> LabelTipProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(LabelTip));

    public static readonly StyledProperty<RowLayout> LayoutProperty = AvaloniaProperty.Register<InspectorRow, RowLayout>(nameof(Layout));

    public static readonly StyledProperty<string?> InfoProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(Info));

    public static readonly StyledProperty<ICommand?> RevertCommandProperty = AvaloniaProperty.Register<InspectorRow, ICommand?>(nameof(RevertCommand));

    public static readonly StyledProperty<bool> CanRevertProperty = AvaloniaProperty.Register<InspectorRow, bool>(nameof(CanRevert));

    public static readonly StyledProperty<string?> RevertIdProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(RevertId));

    private InspectorSection? _section;

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The label's tooltip, such as "Instructions · Required".</summary>
    public string? LabelTip
    {
        get => GetValue(LabelTipProperty);
        set => SetValue(LabelTipProperty, value);
    }

    public RowLayout Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>The tooltip of the info glyph after the editor. No glyph shows without it.</summary>
    public string? Info
    {
        get => GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    public ICommand? RevertCommand
    {
        get => GetValue(RevertCommandProperty);
        set => SetValue(RevertCommandProperty, value);
    }

    /// <summary>The value differs from a default it can go back to, so the revert arrow shows.</summary>
    public bool CanRevert
    {
        get => GetValue(CanRevertProperty);
        set => SetValue(CanRevertProperty, value);
    }

    /// <summary>The revert arrow's automation id, such as RevertInstructions.</summary>
    public string? RevertId
    {
        get => GetValue(RevertIdProperty);
        set => SetValue(RevertIdProperty, value);
    }

    /// <summary>While the filter has text, whether it matches the label.</summary>
    internal bool LabelMatches => !IsFiltering || Label is { } label && InspectorFilter.Matches(State!.Query, label);

    internal override void Refresh() => IsVisible = IsShown && (!IsFiltering || _section?.TitleMatches == true || LabelMatches);

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        _section = this.FindLogicalAncestorOfType<InspectorSection>();
        base.OnAttachedToLogicalTree(e);
        _section?.Track(this);
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        _section?.Untrack(this);
        _section = null;
        base.OnDetachedFromLogicalTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty)
        {
            PseudoClasses.Set(":stacked", Layout == RowLayout.Stacked);
            PseudoClasses.Set(":full", Layout == RowLayout.Full);
        }
        else if (change.Property == LabelProperty)
        {
            Refresh();
            _section?.Refresh();
        }
        else if (change.Property == IsShownProperty)
        {
            _section?.Refresh();
        }
    }
}
