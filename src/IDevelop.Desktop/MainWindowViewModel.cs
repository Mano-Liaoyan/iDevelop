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
    private readonly RelayCommand _save;
    private readonly RelayCommand _addTask;
    private readonly RelayCommand _refreshAgents;
    private WorkflowDocument? _document;
    private ProjectRuns? _runs;
    private WorkflowCanvasViewModel? _canvas;
    private string? _status;
    private bool _refreshingAgents;

    public MainWindowViewModel(ClientDirectory clients)
    {
        _clients = clients;
        _save = new RelayCommand(() => TrySave(), () => _document is not null);
        _addTask = new RelayCommand(() => Canvas?.AddTaskCommand.Execute(null), () => Canvas is not null);
        _refreshAgents = new RelayCommand(RefreshAgents, () => !_refreshingAgents);
        clients.Changed += (_, _) => Dispatcher.UIThread.Post(OnClientsChanged);
    }

    public WorkflowCanvasViewModel? Canvas
    {
        get => _canvas;
        private set => SetProperty(ref _canvas, value);
    }

    public string? ProjectName => _document is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(_document.ProjectFolder));

    public string Title => ProjectName is null ? "iDevelop" : $"{ProjectName}{(HasUnsavedChanges ? "*" : "")} - iDevelop";

    public bool HasUnsavedChanges => _document?.HasUnsavedChanges ?? false;

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand SaveCommand => _save;

    public ICommand AddTaskCommand => _addTask;

    public IReadOnlyList<AgentRow> Agents => [.. Clients.All.Select(id => new AgentRow(id, _clients.Current[id]))];

    /// <summary>Probes every client again. Each row keeps its last status until its new answer arrives.</summary>
    public ICommand RefreshAgentsCommand => _refreshAgents;

    /// <summary>The task this window's project is running, or null.</summary>
    public AttemptRecord? ActiveRun => _runs?.Active;

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
            Status = e is ProjectException ? e.Message : $"Couldn't open {folder}: {e.Message}";
            return;
        }

        await LeaveProject();
        var runs = ProjectRuns.Open(document.ProjectFolder, _clients);
        _document = document;
        _runs = runs;
        document.Changed += (_, _) => OnDocumentChanged();
        Canvas = new WorkflowCanvasViewModel(document, runs, _clients, notice => Status = notice);
        Status = runs.Warnings.IsEmpty ? null : string.Join(" ", runs.Warnings);
        OnProjectChanged();
    }

    /// <summary>Closes the open project. A running task's client is stopped, and its attempt is recorded as interrupted.</summary>
    public async Task LeaveProject()
    {
        if (_runs is not { } runs)
        {
            return;
        }

        _runs = null;
        _document = null;
        Canvas = null;
        OnProjectChanged();
        await runs.DisposeAsync();
    }

    public bool TrySave()
    {
        if (_document is null)
        {
            return true;
        }

        try
        {
            _document.Save();
            Status = null;
            return true;
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status = e is ProjectException ? e.Message : $"Couldn't save: {e.Message}";
            return false;
        }
    }

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
        _save.NotifyCanExecuteChanged();
        _addTask.NotifyCanExecuteChanged();
    }

    private void OnDocumentChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }
}
