using System.Collections.Immutable;
using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Projects;

namespace IDevelop.Desktop;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ClientDirectory _clients;
    private readonly Func<string, Task> _copy;
    private readonly string? _personalBlueprints;
    private readonly RelayCommand _save;
    private readonly RelayCommand _undo;
    private readonly RelayCommand _redo;
    private readonly RelayCommand _refreshAgents;
    private WorkflowCanvasViewModel? _canvas;
    private string? _status;
    private bool _refreshingAgents;

    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="personalBlueprints">The personal blueprint library's folder, or null for none.</param>
    public MainWindowViewModel(ClientDirectory clients, Func<string, Task> copy, string? personalBlueprints = null)
    {
        _clients = clients;
        _copy = copy;
        _personalBlueprints = personalBlueprints;
        _save = new RelayCommand(() => TrySave(), () => EditableCanvas is not null);
        _undo = new RelayCommand(() => EditableCanvas?.Document.Undo(), () => EditableCanvas?.Document.CanUndo ?? false);
        _redo = new RelayCommand(() => EditableCanvas?.Document.Redo(), () => EditableCanvas?.Document.CanRedo ?? false);
        _refreshAgents = new RelayCommand(RefreshAgents, () => !_refreshingAgents);
        clients.Changed += (_, _) => Dispatcher.UIThread.Post(OnClientsChanged);
    }

    public WorkflowCanvasViewModel? Canvas
    {
        get => _canvas;
        private set => SetProperty(ref _canvas, value);
    }

    // The Generate sheet is modal, so a key or a button under its scrim neither saves nor takes an edit back.
    private WorkflowCanvasViewModel? EditableCanvas => Canvas is { Sheet: null } canvas ? canvas : null;

    public string? ProjectName => Canvas is { } canvas ? Path.GetFileName(Path.TrimEndingDirectorySeparator(canvas.Document.ProjectFolder)) : null;

    public string Title => ProjectName is null ? "iDevelop" : $"{ProjectName}{(HasUnsavedChanges ? "*" : "")} - iDevelop";

    public bool HasUnsavedChanges => Canvas?.Document.HasUnsavedChanges ?? false;

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand SaveCommand => _save;

    /// <summary>Takes back the latest workflow edit. A run of typing in one box is one edit.</summary>
    public ICommand UndoCommand => _undo;

    public ICommand RedoCommand => _redo;

    public IReadOnlyList<AgentRow> Agents => [.. Clients.All.Select(id => new AgentRow(id, _clients.Current[id]))];

    /// <summary>Probes every client again. Each row keeps its last status until its new answer arrives.</summary>
    public ICommand RefreshAgentsCommand => _refreshAgents;

    /// <summary>The tasks this window's project is running, oldest first.</summary>
    public ImmutableArray<AttemptRecord> ActiveRuns => Canvas?.Runs.Active ?? [];

    /// <summary>
    /// Reads the folder's workflow before it leaves the open project, so a folder that fails to open leaves that project
    /// and its run alone. Completes at once when no task runs.
    /// </summary>
    public async Task Open(string folder)
    {
        WorkflowDocument document;
        try
        {
            document = WorkflowDocument.Open(folder);
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status = Describe(e, $"Couldn't open {folder}");
            return;
        }

        await LeaveProject();
        var runs = ProjectRuns.Open(document.ProjectFolder, _clients);
        document.Changed += (_, _) => OnDocumentChanged();
        Canvas = new WorkflowCanvasViewModel(document, runs, _clients, notice => Status = notice, _copy, _personalBlueprints);
        Canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowCanvasViewModel.Sheet))
            {
                OnEditableChanged();
            }
        };
        Status = string.Join(" ", [.. document.Converted is { } converted ? [converted] : Array.Empty<string>(), .. runs.Warnings]) is { Length: > 0 } notice
            ? notice
            : null;
        OnProjectChanged();
    }

    /// <summary>Closes the open project. A running task's client is stopped, and its attempt is recorded as interrupted.</summary>
    public async Task LeaveProject()
    {
        if (Canvas is not { } canvas)
        {
            return;
        }

        Canvas = null;
        OnProjectChanged();
        await canvas.Runs.DisposeAsync();
    }

    public bool TrySave()
    {
        if (Canvas is not { } canvas)
        {
            return true;
        }

        try
        {
            canvas.Document.Save();
            Status = null;
            return true;
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status = Describe(e, "Couldn't save");
            return false;
        }
    }

    // A ProjectException's message is already written for the user. A file system error's message needs the action.
    private static string Describe(Exception e, string action) => e is ProjectException ? e.Message : $"{action}: {e.Message}";

    private void OnClientsChanged()
    {
        OnPropertyChanged(nameof(Agents));
        Canvas?.OnClientsChanged();
    }

    private async void RefreshAgents()
    {
        _refreshingAgents = true;
        _refreshAgents.NotifyCanExecuteChanged();
        try
        {
            await _clients.RefreshAsync();
        }
        finally
        {
            _refreshingAgents = false;
            _refreshAgents.NotifyCanExecuteChanged();
        }
    }

    private void OnProjectChanged()
    {
        OnPropertyChanged(nameof(ProjectName));
        OnDocumentChanged();
    }

    private void OnDocumentChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnEditableChanged();
    }

    private void OnEditableChanged()
    {
        _save.NotifyCanExecuteChanged();
        _undo.NotifyCanExecuteChanged();
        _redo.NotifyCanExecuteChanged();
    }
}
