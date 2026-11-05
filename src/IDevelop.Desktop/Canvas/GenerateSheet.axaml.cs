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

    private void OnAgentChosen(object? sender, SelectionChangedEventArgs e) => AgentPicker.OnChosen(sender);
}
