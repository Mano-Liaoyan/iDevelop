using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IDevelop.Desktop.Canvas;

public partial class GenerateSheet : UserControl
{
    public GenerateSheet() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Prompt.Focus();
    }

    // As in the inspector, a picker whose list is replaced keeps an equal entry selected, so only a change in the open
    // list or by a key on the focused picker is a choice.
    private static bool IsChoice(ComboBox picker) => picker.IsDropDownOpen || picker.IsKeyboardFocusWithin;

    private void OnClientChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: GenerateWorkflowViewModel sheet, SelectedItem: PlannerClientChoice choice } picker && IsChoice(picker))
        {
            sheet.ChooseClient(choice);
        }
    }

    private void OnModelChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: GenerateWorkflowViewModel sheet, SelectedItem: PlannerChoice choice } picker && IsChoice(picker))
        {
            sheet.ChooseModel(choice);
        }
    }

    private void OnReasoningChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: GenerateWorkflowViewModel sheet, SelectedItem: PlannerChoice choice } picker && IsChoice(picker))
        {
            sheet.ChooseReasoning(choice);
        }
    }
}
