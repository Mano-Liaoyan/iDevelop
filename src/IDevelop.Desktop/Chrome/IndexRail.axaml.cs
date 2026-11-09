using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IDevelop.Desktop.Chrome;

/// <summary>The Rail: Projects, Inbox with its count, and Find, each of which opens the Index as a flyout over the Map.</summary>
public partial class IndexRail : UserControl
{
    public IndexRail() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnProjects(object? sender, RoutedEventArgs e) => ViewModel?.ToggleIndexFlyout();

    private void OnInbox(object? sender, RoutedEventArgs e) => ViewModel?.OpenInbox();

    private void OnFind(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OpenFind();
}
