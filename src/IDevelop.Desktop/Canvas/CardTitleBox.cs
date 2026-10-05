using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Renames a task on its card, as a Finder rename does. It opens with the title selected. Enter or a click elsewhere
/// keeps the new title, and Escape keeps the old one. Either way the canvas takes the keys again.
/// </summary>
public sealed class CardTitleBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    private TaskNodeViewModel? Node => DataContext as TaskNodeViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && Node is { } node)
        {
            Text = node.Title;
            // The box takes the focus once it is laid out, after the change that showed it.
            Dispatcher.UIThread.Post(() =>
            {
                Focus();
                SelectAll();
            });
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            Finish(e.Key == Key.Enter ? Text : null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Node?.EndRename(Text);
    }

    private void Finish(string? title)
    {
        Node?.EndRename(title);
        this.FindAncestorOfType<NodifyEditor>()?.Focus();
    }
}
