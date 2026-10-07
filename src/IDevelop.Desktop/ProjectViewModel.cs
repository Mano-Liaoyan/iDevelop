using System.Collections.ObjectModel;
using Avalonia.Threading;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop;

/// <summary>
/// One open project folder: its one runner, a canvas for each of its workflow documents, and which workflow holds each
/// task. Its runner lives until the project closes, so switching to another project or workflow stops nothing.
/// </summary>
public sealed class ProjectViewModel : ObservableObject
{
    private readonly Func<ProjectViewModel, WorkflowDocument, WorkflowCanvasViewModel> _newCanvas;

    // Every task any of this project's documents held this session. A task id never moves between workflows, because
    // a new task gets a fresh id and opening refuses ids that two files share, so the first holder is the only one. A
    // running task deleted from its workflow keeps its owner, so its run stays in that workflow's run bar.
    private readonly Dictionary<TaskId, WorkflowCanvasViewModel> _owners = [];

    internal ProjectViewModel(
        string folder, ProjectRuns runs, IEnumerable<WorkflowDocument> documents, Func<ProjectViewModel, WorkflowDocument, WorkflowCanvasViewModel> newCanvas)
    {
        Folder = folder;
        Name = ProjectFolders.Name(folder);
        Runs = runs;
        _newCanvas = newCanvas;
        runs.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsRunning)));
        foreach (var document in documents)
        {
            Add(document);
        }
    }

    /// <summary>The full path without a trailing separator, which identifies the project in this window.</summary>
    public string Folder { get; }

    public string Name { get; }

    /// <summary>One canvas per workflow, kept until the project closes, in the order the project lists them.</summary>
    public ObservableCollection<WorkflowCanvasViewModel> Workflows { get; } = [];

    public bool IsRunning => !Runs.Active.IsEmpty;

    internal ProjectRuns Runs { get; }

    internal IEnumerable<WorkflowDocument> UnsavedDocuments =>
        Workflows.Select(canvas => canvas.Document).Where(document => document.HasUnsavedChanges);

    /// <summary>Whether the canvas's workflow holds or held the task this session.</summary>
    internal bool Owns(WorkflowCanvasViewModel canvas, TaskId task) => _owners.GetValueOrDefault(task) == canvas;

    internal WorkflowCanvasViewModel Add(WorkflowDocument document)
    {
        WorkflowCanvasViewModel? canvas = null;
        // Subscribed before the canvas subscribes, so the runner follows each change before the canvas rechecks its
        // tasks, whose reasons not to start depend on the workflows the runner follows.
        document.Changed += (_, _) =>
        {
            Runs.Follow(document.Current);
            Attribute(canvas!);
        };
        Runs.Follow(document.Current);
        canvas = _newCanvas(this, document);
        Attribute(canvas);
        // A review that rested when the project opened may have started its next run before the canvas listened.
        canvas.ShowAttempts();
        Workflows.Add(canvas);
        return canvas;
    }

    /// <summary>
    /// Closes each workflow's Generate sheet, which listens to the window's client directory until it closes, and stops
    /// the project's running tasks.
    /// </summary>
    internal ValueTask CloseAsync()
    {
        foreach (var canvas in Workflows)
        {
            canvas.CloseSheet();
        }

        return Runs.DisposeAsync();
    }

    /// <summary>A review whose next step waited for a client goes on once the clients change.</summary>
    internal void FollowAll()
    {
        foreach (var canvas in Workflows)
        {
            Runs.Follow(canvas.Document.Current);
        }
    }

    /// <summary>
    /// "Workflow N" with the smallest N that no workflow of this project shows as its name. A workflow shown as "Workflow"
    /// counts as "Workflow 1", so the next one reads as its second.
    /// </summary>
    internal string NextWorkflowName()
    {
        var taken = Workflows.Select(canvas => canvas.Name == Workflow.UnnamedName ? $"{Workflow.UnnamedName} 1" : canvas.Name).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(1, taken.Count + 1).Select(number => $"Workflow {number}").First(name => !taken.Contains(name));
    }

    private void Attribute(WorkflowCanvasViewModel canvas)
    {
        foreach (var task in canvas.Document.Current.Tasks.Keys)
        {
            _owners.TryAdd(task, canvas);
        }
    }
}
