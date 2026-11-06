using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace IDevelop.Desktop;

/// <summary>
/// Renames an item in place, as a Finder rename does. It opens with the text selected. Enter or a click elsewhere keeps
/// the new text, and Escape keeps the old one. After Enter or Escape, the focus target takes the focus.
/// </summary>
public abstract class RenameBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>The text that the box opens with, or null while the box has no item. A box with no item takes no focus.</summary>
    protected abstract string? ItemText { get; }

    protected abstract Control? FocusTarget { get; }

    /// <summary>Ends the rename with the new text, or with null to keep the old one.</summary>
    protected abstract void End(string? text);

    /// <summary>Keeps the typed text, as a click elsewhere does. A box whose rename ended keeps nothing.</summary>
    internal void Commit() => End(Text);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && ItemText is { } text)
        {
            Text = text;
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
            // The box finds its target before the rename ends.
            var target = FocusTarget;
            End(e.Key == Key.Enter ? Text : null);
            target?.Focus();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Commit();
    }
}
