using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Execution;

namespace IDevelop.Desktop;

public partial class MainWindow : Window
{
    private bool _waitingForUser;
    private bool _closeConfirmed;

    /// <summary>For the XAML loader and the designer. Its directory searches no folder, so it finds no client.</summary>
    public MainWindow() : this(new ClientDirectory(CommandResolver.Create([], [])))
    {
    }

    /// <param name="clients">The app's one directory. The app starts its first refresh.</param>
    public MainWindow(ClientDirectory clients)
    {
        ViewModel = new MainWindowViewModel(clients);
        InitializeComponent();
        DataContext = ViewModel;
        PickFolder = PickFolderWithStorageProvider;
    }

    public MainWindowViewModel ViewModel { get; }

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

        if (!ViewModel.HasUnsavedChanges && ViewModel.ActiveRun is null)
        {
            return;
        }

        e.Cancel = true;
        _waitingForUser = true;
        var leave = await ConfirmLeaving();
        if (leave)
        {
            await ViewModel.LeaveProject();
        }

        _waitingForUser = false;
        if (leave)
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnTitleBandPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            BeginMoveDrag(e);
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

    private WorkflowCanvasView? CanvasView => CanvasHost.Presenter?.Child as WorkflowCanvasView;

    // The list also follows the canvas's selection, so only a change made while the list has focus is a choice in it.
    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SidebarTasks.IsKeyboardFocusWithin && SidebarTasks.SelectedItem is TaskNodeViewModel task)
        {
            CanvasView?.BringIntoViewIfHidden(task);
        }
    }

    // Choosing the task that is already selected changes no selection, so a tap on a row and Space or Enter on the
    // focused row also count as choices.
    private void OnSidebarTapped(object? sender, TappedEventArgs e) => BringRowIntoView(e.Source);

    private void OnSidebarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            BringRowIntoView(e.Source);
        }
    }

    // A tap between rows also reaches the list, and it chooses no task.
    private void BringRowIntoView(object? source)
    {
        if ((source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: TaskNodeViewModel task })
        {
            CanvasView?.BringIntoViewIfHidden(task);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        _waitingForUser = true;
        var folder = await ConfirmLeaving() ? await PickFolder() : null;
        if (folder is not null)
        {
            await ViewModel.Open(folder);
        }

        _waitingForUser = false;
    }

    // Both questions come before either answer acts, so a Cancel at the second leaves the run going. The run stops only
    // when the project is actually left.
    private async Task<bool> ConfirmLeaving() => await ConfirmStoppingRun() && await ConfirmLeavingDocument();

    private async Task<bool> ConfirmStoppingRun()
    {
        if (ViewModel.ActiveRun is not { } run)
        {
            return true;
        }

        return await new RunningTaskDialog(run.TaskTitle).ShowDialog<RunningTaskChoice?>(this) == RunningTaskChoice.StopAndLeave;
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
