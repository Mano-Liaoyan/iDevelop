using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Projects;

namespace IDevelop.Desktop;

/// <summary>
/// The window's open projects and which workflow it shows. Choosing a project or a workflow only changes what the
/// window shows. A project's runs stop only when the project or the window closes, after the person agreed.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ClientDirectory _clients;
    private readonly Func<string, Task> _copy;
    private readonly string? _personalBlueprints;
    private readonly string? _sessionFile;
    private readonly RelayCommand _save;
    private readonly RelayCommand _undo;
    private readonly RelayCommand _redo;
    private readonly RelayCommand _refreshAgents;
    // Remembered folders the last start could not open, such as a drive that was not mounted. They stay remembered, so a
    // later start opens them once they are back.
    private readonly List<string> _unopened = [];
    private WorkflowCanvasViewModel? _canvas;
    private string? _status;
    private bool _refreshingAgents;
    private bool _restoring;

    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="personalBlueprints">The personal blueprint library's folder, or null for none.</param>
    /// <param name="sessionFile">The file that remembers the open projects between runs, or null to remember nothing.</param>
    public MainWindowViewModel(ClientDirectory clients, Func<string, Task> copy, string? personalBlueprints = null, string? sessionFile = null)
    {
        _clients = clients;
        _copy = copy;
        _personalBlueprints = personalBlueprints;
        _sessionFile = sessionFile;
        _save = new RelayCommand(() => TrySave(), () => EditableCanvas is not null);
        _undo = new RelayCommand(() => EditableCanvas?.Document.Undo(), () => EditableCanvas?.Document.CanUndo ?? false);
        _redo = new RelayCommand(() => EditableCanvas?.Document.Redo(), () => EditableCanvas?.Document.CanRedo ?? false);
        _refreshAgents = new RelayCommand(RefreshAgents, () => !_refreshingAgents);
        clients.Changed += (_, _) => Dispatcher.UIThread.Post(OnClientsChanged);
    }

    /// <summary>The open projects, in the order they were opened.</summary>
    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    /// <summary>The workflow the window shows, or null when no project is open.</summary>
    public WorkflowCanvasViewModel? Canvas
    {
        get => _canvas;
        private set => SetProperty(ref _canvas, value);
    }

    // The Generate sheet is modal, so a key or a button under its scrim neither saves nor takes an edit back.
    private WorkflowCanvasViewModel? EditableCanvas => Canvas is { Sheet: null } canvas ? canvas : null;

    public string? ProjectName => Canvas?.Project.Name;

    public string? WorkflowName => Canvas?.Name;

    public string Title => ProjectName is null ? "iDevelop" : $"{ProjectName}{(HasUnsavedChanges ? "*" : "")} - iDevelop";

    /// <summary>Whether the shown workflow has edits that are not saved.</summary>
    public bool HasUnsavedChanges => Canvas?.Document.HasUnsavedChanges ?? false;

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Saves the shown workflow.</summary>
    public ICommand SaveCommand => _save;

    /// <summary>Takes back the shown workflow's latest edit. A run of typing in one box is one edit.</summary>
    public ICommand UndoCommand => _undo;

    public ICommand RedoCommand => _redo;

    public IReadOnlyList<AgentRow> Agents => [.. Clients.All.Select(id => new AgentRow(id, _clients.Current[id]))];

    /// <summary>Probes every client again. Each row keeps its last status until its new answer arrives.</summary>
    public ICommand RefreshAgentsCommand => _refreshAgents;

    /// <summary>The tasks this window runs in every open project, oldest first within each project.</summary>
    public ImmutableArray<AttemptRecord> ActiveRuns => [.. Projects.SelectMany(project => project.Runs.Active)];

    /// <summary>
    /// Adds the folder as a project and shows its first workflow. A folder that is already open is shown instead. A folder
    /// that fails to open leaves every open project as it was.
    /// </summary>
    public void Open(string folder)
    {
        if (TryAdd(folder, $"Couldn't open {folder}", out var notices) is not { } project)
        {
            Status = notices;
            return;
        }

        if (Canvas?.Project != project)
        {
            Select(project.Workflows[0]);
        }

        Status = notices;
        Persist();
    }

    /// <summary>Shows the workflow.</summary>
    public void Select(WorkflowCanvasViewModel canvas)
    {
        if (canvas == Canvas)
        {
            return;
        }

        Show(canvas);
        Persist();
    }

    /// <summary>Adds an unsaved, empty workflow to the project, named "Workflow N", shows it, and returns it.</summary>
    public WorkflowCanvasViewModel? NewWorkflow(ProjectViewModel project)
    {
        WorkflowDocument document;
        try
        {
            document = WorkflowDocument.Create(project.Folder, project.NextWorkflowName());
        }
        catch (ProjectException e)
        {
            Status = e.Message;
            return null;
        }

        var canvas = project.Add(document);
        Select(canvas);
        return canvas;
    }

    /// <summary>
    /// Closes the project without saving. The window shows a neighbour's workflow, or nothing, at once, and the project's
    /// running tasks then stop, which records them as interrupted. The next start does not reopen it.
    /// </summary>
    public async Task Close(ProjectViewModel project)
    {
        var index = Projects.IndexOf(project);
        if (index < 0)
        {
            return;
        }

        Projects.RemoveAt(index);
        if (Canvas?.Project == project)
        {
            Show(Projects.Count == 0 ? null : Projects[Math.Min(index, Projects.Count - 1)].Workflows[0]);
        }

        Persist();
        await project.CloseAsync();
    }

    /// <summary>Stops every running task, as closing the window does. The open projects stay remembered for the next start.</summary>
    public Task Leave() => Task.WhenAll(Projects.Select(project => project.CloseAsync().AsTask()));

    /// <summary>
    /// Opens the projects the last session left open, with the workflow rows it left expanded and the workflow it showed,
    /// then the folder, if any. A folder that is missing or fails to open is skipped with a status line, and stays
    /// remembered, so a later start opens it once it is back.
    /// </summary>
    public void Restore(string? folder)
    {
        var session = _sessionFile is null ? WorkspaceSession.Empty : WorkspaceSession.Read(_sessionFile);
        List<string> notes = [];
        _restoring = true;
        try
        {
            foreach (var saved in session.Projects)
            {
                if (TryAdd(saved, $"Couldn't reopen {saved}", out var notice) is null)
                {
                    _unopened.Add(saved);
                }

                if (notice is not null)
                {
                    notes.Add(notice);
                }
            }

            var canvases = Projects.SelectMany(project => project.Workflows).ToList();
            foreach (var canvas in canvases)
            {
                canvas.IsExpanded = session.Expanded.Any(saved => saved.Is(canvas.Project.Folder, canvas.Workflow.Id));
            }

            var selected = session.Selected is { } choice
                ? canvases.FirstOrDefault(canvas => choice.Is(canvas.Project.Folder, canvas.Workflow.Id))
                  ?? Projects.FirstOrDefault(project => ProjectFolders.Comparer.Equals(project.Folder, choice.Folder))?.Workflows[0]
                : null;
            if ((selected ?? canvases.FirstOrDefault()) is { } shown)
            {
                Select(shown);
            }
        }
        finally
        {
            _restoring = false;
        }

        if (folder is null)
        {
            Status = null;
            Persist();
        }
        else
        {
            Open(folder);
        }

        Status = string.Join(" ", [.. notes, .. Status is { } status ? [status] : Array.Empty<string>()]) is { Length: > 0 } all ? all : null;
    }

    /// <summary>Saves the shown workflow.</summary>
    public bool TrySave() => Canvas is not { } canvas || TrySave([canvas.Document]);

    /// <summary>Saves each document in turn and stops at the first that fails, whose reason the status shows.</summary>
    internal bool TrySave(IEnumerable<WorkflowDocument> documents)
    {
        try
        {
            foreach (var document in documents)
            {
                document.Save();
            }

            Status = null;
            return true;
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status = Describe(e, "Couldn't save");
            return false;
        }
    }

    /// <summary>Adds the folder as a project, or finds it open. Returns null when it fails to open, with the reason.</summary>
    private ProjectViewModel? TryAdd(string folder, string failure, out string? notices)
    {
        notices = null;
        ImmutableArray<WorkflowDocument> documents;
        string identity;
        try
        {
            identity = ProjectFolders.Identity(folder);
            if (Projects.FirstOrDefault(project => ProjectFolders.Comparer.Equals(project.Folder, identity)) is { } open)
            {
                return open;
            }

            documents = WorkflowDocument.OpenProject(identity);
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            notices = Describe(e, failure);
            return null;
        }

        var runs = ProjectRuns.Open(identity, _clients);
        var project = new ProjectViewModel(identity, runs, documents, NewCanvas);
        Projects.Add(project);
        _unopened.RemoveAll(unopened => ProjectFolders.Comparer.Equals(unopened, identity));
        notices = string.Join(" ", [.. documents.Select(document => document.Converted).OfType<string>(), .. runs.Warnings]) is { Length: > 0 } notice
            ? notice
            : null;
        return project;
    }

    private WorkflowCanvasViewModel NewCanvas(ProjectViewModel project, WorkflowDocument document)
    {
        var canvas = new WorkflowCanvasViewModel(project, document, _clients, notice => Status = notice, _copy, _personalBlueprints);
        document.Changed += (_, _) =>
        {
            if (canvas == Canvas)
            {
                OnDocumentChanged();
            }
        };
        canvas.PropertyChanged += (_, e) => OnCanvasChanged(canvas, e);
        return canvas;
    }

    private void OnCanvasChanged(WorkflowCanvasViewModel canvas, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkflowCanvasViewModel.IsExpanded):
                Persist();
                break;
            case nameof(WorkflowCanvasViewModel.Sheet) when canvas == Canvas:
                OnEditableChanged();
                break;
            case nameof(WorkflowCanvasViewModel.Name) when canvas == Canvas:
                OnPropertyChanged(nameof(WorkflowName));
                break;
        }
    }

    // Every switch comes here, so the shown palette reads the libraries again, which a sibling may have changed.
    private void Show(WorkflowCanvasViewModel? canvas)
    {
        if (Canvas is { } shown)
        {
            shown.IsSelected = false;
        }

        Canvas = canvas;
        if (canvas is not null)
        {
            canvas.IsSelected = true;
            canvas.Blueprints.Reload();
        }

        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WorkflowName));
        OnDocumentChanged();
    }

    private void Persist()
    {
        if (_sessionFile is not { } file || _restoring)
        {
            return;
        }

        var session = new WorkspaceSession(
            [.. Projects.Select(project => project.Folder), .. _unopened],
            Canvas is { } canvas ? new WorkflowRef(canvas.Project.Folder, canvas.Workflow.Id) : null,
            [.. Projects.SelectMany(project => project.Workflows).Where(canvas => canvas.IsExpanded).Select(canvas => new WorkflowRef(canvas.Project.Folder, canvas.Workflow.Id))]);
        try
        {
            session.Write(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(this, "Couldn't remember the open projects in {File}: {Message}", file, e.Message);
        }
    }

    // A ProjectException's message is already written for the user. A file system error's message needs the action.
    private static string Describe(Exception e, string action) => e is ProjectException ? e.Message : $"{action}: {e.Message}";

    private void OnClientsChanged()
    {
        OnPropertyChanged(nameof(Agents));
        foreach (var project in Projects)
        {
            foreach (var canvas in project.Workflows)
            {
                canvas.OnClientsChanged();
            }

            project.FollowAll();
        }
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
