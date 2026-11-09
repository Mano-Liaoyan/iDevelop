using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Conversation;

/// <summary>Shows an <see cref="ActionDockViewModel"/>.</summary>
public partial class ActionDock : UserControl
{
    public ActionDock() => InitializeComponent();

    private void OnChangeModel(object? sender, RoutedEventArgs e) => this.FindAncestorOfType<ConversationView>()?.OpenModelPicker();
}
