using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IDevelop.Desktop;

public enum UnsavedChangesChoice { Save, Discard, Cancel }

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog() => InitializeComponent();

    public UnsavedChangesDialog(string folder) : this()
    {
        Title = $"Save changes to {folder}?";
        Question.Text = Title;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SaveButton.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Save);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Cancel);
}
