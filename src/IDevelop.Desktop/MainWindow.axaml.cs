using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace IDevelop.Desktop;

public partial class MainWindow : Window
{
    private bool _waitingForUser;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        PickFolder = PickFolderWithStorageProvider;
    }

    public MainWindowViewModel ViewModel { get; } = new();

    /// <summary>Asks for a project folder and returns its path, or null when the user cancels.</summary>
    internal Func<Task<string?>> PickFolder { get; set; }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed)
        {
            return;
        }

        if (_waitingForUser)
        {
            e.Cancel = true;
            return;
        }

        if (!ViewModel.HasUnsavedChanges)
        {
            return;
        }

        e.Cancel = true;
        _waitingForUser = true;
        var leave = await ConfirmLeavingDocument();
        _waitingForUser = false;
        if (leave)
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // A click, a key, and UI Automation each check a segment their own way, so the checked segment is the choice.
    private void OnThemeChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: ThemeVariant variant })
        {
            ((App)Application.Current!).Choose(variant);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        _waitingForUser = true;
        var folder = await ConfirmLeavingDocument() ? await PickFolder() : null;
        _waitingForUser = false;
        if (folder is not null)
        {
            ViewModel.Open(folder);
        }
    }

    private async Task<bool> ConfirmLeavingDocument()
    {
        if (ViewModel is not { HasUnsavedChanges: true, ProjectName: { } folder })
        {
            return true;
        }

        return await new UnsavedChangesDialog(folder).ShowDialog<UnsavedChangesChoice?>(this) switch
        {
            UnsavedChangesChoice.Save => ViewModel.TrySave(),
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Cancel or null => false,
        };
    }

    private async Task<string?> PickFolderWithStorageProvider()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open project folder" });
        return folders is [var folder] ? folder.TryGetLocalPath() : null;
    }
}
