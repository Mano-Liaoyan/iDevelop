using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;

namespace IDevelop.Desktop.Sidebar;

/// <summary>The Find palette (Ctrl+K): a field and its results over the whole window. Enter picks, Esc closes, and the arrows move.</summary>
public partial class FindPalette : UserControl
{
    private IInputElement? _before;

    public FindPalette()
    {
        InitializeComponent();
        FindBox.AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        // A click anywhere else leaves Find. A result takes no focus, so a click on one stays here.
        FindBox.LostFocus += (_, _) =>
        {
            if (ViewModel.Find.IsOpen && FindBox.IsEffectivelyVisible)
            {
                Close();
            }
        };
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private MainWindow? Window => TopLevel.GetTopLevel(this) as MainWindow;

    /// <summary>Opens the palette and puts the focus in its field. <paramref name="before"/> takes it back on Esc.</summary>
    internal void Focus(IInputElement? before)
    {
        if (!ViewModel.Find.IsOpen)
        {
            _before = before;
        }

        ViewModel.Find.Open();
        Window?.UpdateLayout();
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void Close()
    {
        ViewModel.Find.Close();
        _before = null;
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => Close();

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        var find = ViewModel.Find;
        switch (e.Key)
        {
            case Key.Down:
                find.Move(1);
                FindResults.ScrollIntoView(find.Index);
                e.Handled = true;
                break;
            case Key.Up:
                find.Move(-1);
                FindResults.ScrollIntoView(find.Index);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = Choose();
                break;
            case Key.Escape:
                var before = _before;
                Close();
                (before ?? Window?.CanvasFocusTarget)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            Choose();
        }
    }

    // The chosen node's card takes the keys next, so Enter goes on to its Chat page. The canvas of another workflow only takes
    // the keys after the next layout.
    private bool Choose()
    {
        if (ViewModel.Find.Choose() is null)
        {
            return false;
        }

        _before = null;
        Window?.CanvasFocusTarget?.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.Post(() => Window?.CanvasFocusTarget?.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
        return true;
    }
}
