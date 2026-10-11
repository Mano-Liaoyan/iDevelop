using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Canvas;

/// <summary>An expanded card's body. Its agent pickers choose as the inspector's do.</summary>
public partial class CardDetail : UserControl
{
    public CardDetail()
    {
        AvaloniaXamlLoader.Load(this);
        // A picker leaves its press unhandled and opens on the release, but the card's container takes an unhandled press
        // to select and drag the card and keeps the pointer, so the picker would never open. The press stays with it.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<ComboBox>(includeSelf: true) is not null)
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
    }

    // A picker whose list is replaced keeps an equal entry selected, so only a change in the open list or by a key on the
    // focused picker is the person's choice, as in the inspector.
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

    private void OnReasoningChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TaskNodeViewModel task, SelectedItem: Choice choice } picker && IsChoice(picker))
        {
            task.ChooseReasoning(choice);
        }
    }
}
