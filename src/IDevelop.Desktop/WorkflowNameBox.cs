using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop;

/// <summary>
/// Renames a workflow on its sidebar row, as a task's card title box does. It opens with the name selected. Enter or a
/// click elsewhere keeps the new name, and Escape keeps the old one. Either way the row takes the focus again.
/// </summary>
public sealed class WorkflowNameBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    private WorkflowCanvasViewModel? Workflow => DataContext as WorkflowCanvasViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && Workflow is { } workflow)
        {
            Text = workflow.Name;
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
            var row = (Parent as Panel)?.Children.OfType<Button>().FirstOrDefault();
            Workflow?.EndRenameWorkflow(e.Key == Key.Enter ? Text : null);
            row?.Focus();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Keeps the typed name, as a click elsewhere does. A box whose rename ended keeps nothing.</summary>
    internal void Commit() => Workflow?.EndRenameWorkflow(Text);

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Commit();
    }
}
