using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace IDevelop.Desktop.Inspector;

/// <summary>How a row places its label and its value.</summary>
public enum RowLayout
{
    /// <summary>
    /// The label in the label column and the value beside it in the value column.
    /// </summary>
    Columns,

    /// <summary>
    /// The label on its own line and the value under it from the glyph column to the value edge, for a value that needs the
    /// width: text of several lines, a sentence, a row of chips, or a segmented control.
    /// </summary>
    Stacked,

    /// <summary>No label shown. Content, such as a paragraph or a list, from the content edge to the action edge.</summary>
    Full,

    /// <summary>No label shown. A value from the content edge to the value edge, with the decorator column beside it.</summary>
    Value,

    /// <summary>No label shown. A row of buttons from the glyph column to the panel's inset, where the other actions end.</summary>
    Buttons,

    /// <summary>The label alone across the label and value columns, as a list entry such as a blueprint, with its actions or count after it.</summary>
    Title,
}

/// <summary>
/// One property on a page: its label, a glyph or a kind tile where the row names a thing, its editor, and in the decorator column the
/// override dot while the value differs from its blueprint or the saved value. The dot becomes a reset glyph on hover or focus. The
/// label's tooltip and help text hold the property's note.
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

    public static readonly StyledProperty<string> RevertTipProperty = AvaloniaProperty.Register<InspectorRow, string>(nameof(RevertTip), "Reset to blueprint default");

    private Panel? _labelLine;

    public InspectorRow() => UpdateShape();

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

    /// <summary>What stands in the decorator column beside the reset, such as a blueprint's More.</summary>
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

    /// <summary>The note on the property, which the label's tooltip and help text hold.</summary>
    public string? Info
    {
        get => GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    /// <summary>The label's name for UI Automation: the note in one short line, whose whole text is <see cref="Info"/>.</summary>
    public string? InfoName
    {
        get => GetValue(InfoNameProperty);
        set => SetValue(InfoNameProperty, value);
    }

    /// <summary>The label's automation id while it holds a note, such as PermissionNote.</summary>
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

    /// <summary>The value differs from a default it can go back to, so the override dot shows.</summary>
    public bool CanRevert
    {
        get => GetValue(CanRevertProperty);
        set => SetValue(CanRevertProperty, value);
    }

    /// <summary>The decorator's automation id, such as RevertInstructions.</summary>
    public string? RevertId
    {
        get => GetValue(RevertIdProperty);
        set => SetValue(RevertIdProperty, value);
    }

    /// <summary>The decorator's tooltip, which names the value it restores.</summary>
    public string RevertTip
    {
        get => GetValue(RevertTipProperty);
        set => SetValue(RevertTipProperty, value);
    }

    internal override void Refresh() => IsVisible = IsShown;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _labelLine = e.NameScope.Find<Panel>("PART_LabelLine");
        ShowNote();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty || change.Property == IconProperty || change.Property == LeadProperty)
        {
            UpdateShape();
        }
        else if (change.Property == InfoProperty || change.Property == InfoNameProperty || change.Property == InfoIdProperty || change.Property == LabelTipProperty)
        {
            ShowNote();
        }
        else if (change.Property == CanRevertProperty)
        {
            PseudoClasses.Set(":changed", CanRevert);
        }
    }

    private void ShowNote()
    {
        // A text block names itself by its text, so the note's automation name goes on the line around it.
        if (_labelLine is not null)
        {
            ToolTip.SetTip(_labelLine, Info ?? LabelTip);
            AutomationProperties.SetAutomationId(_labelLine, InfoId);
            AutomationProperties.SetName(_labelLine, InfoName);
            AutomationProperties.SetHelpText(_labelLine, Info);
            AutomationProperties.SetAccessibilityView(_labelLine, Info is null ? AccessibilityView.Default : AccessibilityView.Control);
        }
    }

    private void UpdateShape()
    {
        PseudoClasses.Set(":stacked", Layout == RowLayout.Stacked);
        PseudoClasses.Set(":full", Layout == RowLayout.Full);
        PseudoClasses.Set(":value", Layout == RowLayout.Value);
        PseudoClasses.Set(":buttons", Layout == RowLayout.Buttons);
        PseudoClasses.Set(":title", Layout == RowLayout.Title);
        PseudoClasses.Set(":bare", Icon is null && Lead is null);
    }
}
