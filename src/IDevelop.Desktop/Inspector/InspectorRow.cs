using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace IDevelop.Desktop.Inspector;

/// <summary>How a row places its label and its editor.</summary>
public enum RowLayout
{
    /// <summary>The label in the label column and the value in the value column, at every width.</summary>
    Columns,

    /// <summary>As <see cref="Columns"/>, with the editor under its label once the row is narrower than <see cref="InspectorGrid.NarrowWidth"/>.</summary>
    Editor,


    /// <summary>The label on its own line, the editor under it from the glyph column to the value edge, for text of several lines.</summary>
    Stacked,

    /// <summary>No label shown. The content runs from the glyph column to the value edge, and the label, if any, only matches the filter.</summary>
    Full,

    /// <summary>The label alone across the label and value columns, as a list entry such as a blueprint, with its actions after it.</summary>
    Title,
}

/// <summary>
/// One property in an inspector section: a glyph or a kind tile, its label, its editor, a revert arrow while the value differs from the
/// blueprint's default, and an info glyph whose tooltip holds the property's note. While the filter has text, a row shows
/// when its label or its section's title matches. A row without a label shows only with its section's title.
/// </summary>
public sealed class InspectorRow : InspectorPart
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<InspectorRow, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<object?> LeadProperty = AvaloniaProperty.Register<InspectorRow, object?>(nameof(Lead));

    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<InspectorRow, object?>(nameof(Actions));

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> LabelTipProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(LabelTip));

    public static readonly StyledProperty<RowLayout> LayoutProperty = AvaloniaProperty.Register<InspectorRow, RowLayout>(nameof(Layout));

    public static readonly StyledProperty<string?> InfoProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(Info));

    public static readonly StyledProperty<string?> InfoNameProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(InfoName));

    public static readonly StyledProperty<string?> InfoIdProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(InfoId));

    public static readonly StyledProperty<ICommand?> RevertCommandProperty = AvaloniaProperty.Register<InspectorRow, ICommand?>(nameof(RevertCommand));

    public static readonly StyledProperty<bool> CanRevertProperty = AvaloniaProperty.Register<InspectorRow, bool>(nameof(CanRevert));

    public static readonly StyledProperty<string?> RevertIdProperty = AvaloniaProperty.Register<InspectorRow, string?>(nameof(RevertId));

    private InspectorSection? _section;

    /// <summary>The glyph in the row's first column, such as the gauge for Reasoning.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>What stands in the first column in place of a glyph, such as a kind tile.</summary>
    public object? Lead
    {
        get => GetValue(LeadProperty);
        set => SetValue(LeadProperty, value);
    }

    /// <summary>Buttons in the trailing column after the revert arrow and the info glyph, such as a blueprint's Place and More.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

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

    /// <summary>The info glyph's name for UI Automation: the note in one short line, whose whole text is <see cref="Info"/>.</summary>
    public string? InfoName
    {
        get => GetValue(InfoNameProperty);
        set => SetValue(InfoNameProperty, value);
    }

    /// <summary>The info glyph's automation id, such as PermissionNote.</summary>
    public string? InfoId
    {
        get => GetValue(InfoIdProperty);
        set => SetValue(InfoIdProperty, value);
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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        PseudoClasses.Set(":narrow", e.NewSize.Width < InspectorGrid.NarrowWidth);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty)
        {
            PseudoClasses.Set(":editor", Layout == RowLayout.Editor);
            PseudoClasses.Set(":stacked", Layout == RowLayout.Stacked);
            PseudoClasses.Set(":full", Layout == RowLayout.Full);
            PseudoClasses.Set(":title", Layout == RowLayout.Title);
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
