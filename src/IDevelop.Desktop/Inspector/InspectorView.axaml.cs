using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

public partial class InspectorView : UserControl
{
    private readonly InspectorState _state = new();

    public InspectorView()
    {
        InitializeComponent();
        InspectorState.SetCurrent(this, _state);
        InspectorState.SetCurrent(EditorHost, new InspectorState());
        // Every row of the panel, the blueprint editor's too, puts its value under its label below the default width.
        SizeChanged += (_, change) => InspectorGrid.SetIsNarrow(this, change.NewSize.Width < InspectorGrid.NarrowPanelWidth);
        Editor.PropertyChanged += (_, change) =>
        {
            if (change.Property == ContentProperty)
            {
                EditorHost.ScrollToHome();
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

    // A click, a key, and UI Automation each choose in a picker their own way, and a picker's binding only shows the
    // task's agent, so a change of selection is the choice. A picker whose list is replaced keeps an equal entry from its
    // old list selected, such as the old level when the new client's model also offers it, so only a change in the open
    // list or by a key on the focused picker is a choice.
    private static bool IsChoice(ComboBox picker) => picker.IsDropDownOpen || picker.IsKeyboardFocusWithin;

    private void OnClientChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TaskNodeViewModel task, SelectedItem: ClientChoice choice } picker && IsChoice(picker))
        {
            task.ChooseClient(choice);
        }
    }

    private void OnModelChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TaskNodeViewModel task, SelectedItem: Choice choice } picker && IsChoice(picker))
        {
            task.ChooseModel(choice);
        }
    }

    private void OnConversationChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TaskNodeViewModel task, SelectedItem: Choice choice } picker && IsChoice(picker))
        {
            task.ChooseConversation(choice);
        }
    }

    private void OnReasoningChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TaskNodeViewModel task, SelectedItem: Choice choice } picker && IsChoice(picker))
        {
            task.ChooseReasoning(choice);
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
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e) => _state.UnfoldAll();

    private void OnCollapseAll(object? sender, RoutedEventArgs e) => _state.Fold(Sections(), folded: true);

    // The sections the panel shows now. A fold outlives them, so each key keeps its fold for the next node.
    private IEnumerable<string> Sections() =>
        this.GetLogicalDescendants().OfType<InspectorSection>().Where(section => !EditorHost.IsLogicalAncestorOf(section)).Select(section => section.Key);
}
