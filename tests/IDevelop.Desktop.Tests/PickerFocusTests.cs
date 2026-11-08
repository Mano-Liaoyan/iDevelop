using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

namespace IDevelop.Desktop.Tests;

/// <summary>A picker that a key opened keeps the focus ring when its list closes, and a picker that the pointer opened gets none.</summary>
public sealed class PickerFocusTests
{
    private static (Window Window, ComboBox Box) Open()
    {
        var box = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0, Width = 200 };
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { box } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, box);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_choice_made_with_the_keys_keeps_the_focus_ring()
    {
        var (window, box) = Open();
        box.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.Down, RawInputModifiers.Alt);
        Assert.True(box.IsDropDownOpen);
        Press(window, Key.Down);
        Press(window, Key.Enter);

        Assert.False(box.IsDropDownOpen);
        Assert.Equal(1, box.SelectedIndex);
        Assert.Same(box, window.FocusManager!.GetFocusedElement());
        Assert.Contains(":focus-visible", box.Classes);
    }

    [AvaloniaFact]
    public void A_list_the_pointer_opened_leaves_no_focus_ring()
    {
        var (_, box) = Open();
        box.Focus(NavigationMethod.Pointer);
        Dispatcher.UIThread.RunJobs();

        box.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        box.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(":focus-visible", box.Classes);
    }
}
