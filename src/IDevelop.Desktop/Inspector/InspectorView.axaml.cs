using Avalonia.Controls;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Inspector;

public partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();

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
}
