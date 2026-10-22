using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Nodify;

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
        // A focused picker takes a wheel notch as a choice, and keeps the focus after a choice. Over a closed picker the wheel
        // is the canvas's, as it is over the rest of the card, so the canvas takes the focus before the picker sees the notch.
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<ComboBox>(includeSelf: true) is { IsDropDownOpen: false, IsKeyboardFocusWithin: true })
            {
                this.FindAncestorOfType<NodifyEditor>()?.Focus();
            }
        }, RoutingStrategies.Tunnel);
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
