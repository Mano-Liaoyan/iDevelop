using Avalonia.Controls;

namespace IDevelop.Desktop.Blueprints;

public partial class BlueprintEditorView : UserControl
{
    // A chip puts its value at the caret, and the caret and the focus stay in the template.
    private void OnInsertToken(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TemplateToken token)
        {
            var text = TemplateBox.Text ?? "";
            var at = Math.Clamp(TemplateBox.CaretIndex, 0, text.Length);
            TemplateBox.Text = text.Insert(at, token.Insert);
            TemplateBox.CaretIndex = at + token.Insert.Length;
            TemplateBox.Focus();
        }
    }

    public BlueprintEditorView() => InitializeComponent();
}
