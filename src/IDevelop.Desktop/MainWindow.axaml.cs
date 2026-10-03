using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace IDevelop.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    public MainWindowViewModel ViewModel { get; } = new();

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open project folder" });
        if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
        {
            ViewModel.Open(path);
        }
    }
}
