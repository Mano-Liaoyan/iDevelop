using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

public partial class InspectorView : UserControl
{
    private readonly InspectorState _state = new();

    /// <summary>The tallest row that the list's bottom edge covers rather than cuts. A taller one, such as a box, may show in part.</summary>
    private const double ShortRow = 40;

    public InspectorView()
    {
        InitializeComponent();
        InspectorState.SetCurrent(this, _state);
        InspectorState.SetCurrent(EditorHost, new InspectorState());
        AddHandler(KeyDownEvent, OnKeyDownInPanel, RoutingStrategies.Bubble);
        EditorHost.PropertyChanged += (_, change) =>
        {
            if (change.Property == ContentProperty)
            {
                EditorHost.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToHome();
            }
        };
    }

    /// <summary>
    /// Focuses the selected node's client picker and opens its list, as the person would: a folded Agent section opens,
    /// and a filter that hides the picker clears.
    /// </summary>
    internal void FocusClientPicker()
    {
        _state.Fold(["Agent"], folded: false);
        UpdateLayout();
        if (this.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(box => AutomationProperties.GetAutomationId(box) == "TaskClient") is not { } picker)
        {
            return;
        }

        if (!picker.IsEffectivelyVisible)
        {
            // The box reports its new text to the state only later.
            Filter.Text = "";
            _state.Filter("");
            UpdateLayout();
        }

        // A list opens against where its picker was last laid out, so the picker scrolls into view and is laid out first.
        // A hidden picker's list cannot open, so it opens only once the picker has the focus.
        picker.BringIntoView();
        UpdateLayout();
        if (picker.Focus(NavigationMethod.Tab))
        {
            picker.IsDropDownOpen = true;
        }
    }

    private void OnAgentChosen(object? sender, SelectionChangedEventArgs e) => AgentPicker.OnChosen(sender);

    private void OnConversationChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TaskNodeViewModel task, SelectedItem: ConversationChoice choice } picker && AgentPicker.IsChoice(picker))
        {
            task.ChooseConversation(choice);
        }
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => _state.Filter(Filter.Text);

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(Filter.Text))
        {
            Filter.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && FirstMatch() is { } match)
        {
            match.BringIntoView();
            match.Focus(NavigationMethod.Tab);
            e.Handled = true;
        }
    }

    // The first editor the filter leaves in view. A section's header folds rather than edits, so it does not count.
    private Control? FirstMatch() => SectionList.GetVisualDescendants().OfType<InspectorRow>()
        .Where(row => row.IsEffectivelyVisible)
        .SelectMany(row => row.GetVisualDescendants().OfType<Control>())
        .FirstOrDefault(element => element is TextBox or ComboBox or ToggleButton or Button && element is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true });

    private void OnKeyDownInPanel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None
            && this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => AutomationProperties.GetAutomationId(box) == "TaskTitle") is { } title)
        {
            title.Focus(NavigationMethod.Tab);
            title.SelectAll();
            e.Handled = true;
        }
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e) => _state.UnfoldAll();

    // A folded section opens, and its heading comes to the top of the list.
    private void OnGoToSection(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string key)
        {
            return;
        }

        _state.Fold([key], folded: false);
        UpdateLayout();
        if (SectionList.GetLogicalDescendants().OfType<InspectorSection>().FirstOrDefault(section => section.Key == key) is { IsEffectivelyVisible: true } section)
        {
            SectionScroller.Offset = SectionScroller.Offset.WithY(section.TranslatePoint(default, SectionList)?.Y ?? 0);
        }
    }

    private void OnCollapseAll(object? sender, RoutedEventArgs e) => _state.Fold(Sections(), folded: true);

    // The sections the panel shows now. A fold outlives them, so each key keeps its fold for the next node.
    private IEnumerable<string> Sections() =>
        this.GetLogicalDescendants().OfType<InspectorSection>().Where(section => !EditorHost.IsLogicalAncestorOf(section)).Select(section => section.Key);

    // A short row that the bottom edge would cut hides under a cover until it comes in whole.
    private void OnSectionsScrolled(object? sender, ScrollChangedEventArgs e)
    {
        var height = SectionScroller.Viewport.Height;
        var cut = SectionList.GetVisualDescendants().OfType<Control>()
            .Where(control => control is InspectorRow || control.Classes.Contains("sectionHeader"))
            .Where(control => control.IsEffectivelyVisible && control.Bounds.Height <= ShortRow)
            .Select(control => (Top: control.TranslatePoint(default, SectionScroller)?.Y ?? 0, control.Bounds.Height))
            .Where(row => row.Top < height - 0.5 && row.Top + row.Height > height + 0.5)
            .Select(row => height - row.Top).DefaultIfEmpty(0).Max();
        SectionsFoot.Height = cut;
        SectionsFoot.IsVisible = cut > 0;
    }
}
