using System.Collections.ObjectModel;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using NodeState = IDevelop.Desktop.Canvas.NodeState;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop;

/// <summary>
/// One open project folder: a canvas for each of its workflow documents, each with its own engine. The engines live until
/// the project closes, so switching to another project or workflow stops nothing.
/// </summary>
public sealed class ProjectViewModel : ObservableObject
{
    private readonly Func<ProjectViewModel, WorkflowDocument, WorkflowCanvasViewModel> _newCanvas;

    // One window per project: this window holds the project's lock until it closes the project (ADR 0042).
    private readonly ProjectLock _lock;

    // Unsent drafts and reading positions live as long as the project stays open.
    private readonly Dictionary<ConversationKey, ConversationState> _conversations = [];

    internal ProjectViewModel(
        string folder, ProjectLock windowLock, IEnumerable<WorkflowDocument> documents, Func<ProjectViewModel, WorkflowDocument, WorkflowCanvasViewModel> newCanvas)
    {
        _lock = windowLock;
        Folder = folder;
        Name = ProjectFolders.Name(folder);
        _newCanvas = newCanvas;
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

    /// <summary>The window shows one of the project's workflows.</summary>
    public bool IsCurrent => Workflows.Any(canvas => canvas.IsSelected);

    /// <summary>The first letter of the project's name, which its tile in the left area shows.</summary>
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "";

    internal void RaiseCurrent() => OnPropertyChanged(nameof(IsCurrent));

    /// <summary>Whether the left area lists the project's workflows. The window remembers it.</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ExpandLabel));
            }
        }
    } = true;

    /// <summary>What the row's toggle does now, for a screen reader.</summary>
    public string ExpandLabel => $"{(IsExpanded ? "Fold" : "Unfold")} {Name}";

    /// <summary>A node of the project runs.</summary>
    public bool IsRunning => Workflows.Any(canvas => canvas.IsRunning);

    /// <summary>A node of the project waits for the person: a reply, a question, or an approval.</summary>
    public bool IsWaiting => WaitingCount > 0;

    /// <summary>How many of the project's nodes wait for the person.</summary>
    public int WaitingCount => Workflows.Sum(canvas => canvas.WaitingCount);

    /// <summary>How many of the project's nodes failed.</summary>
    public int FailedCount => Workflows.Sum(canvas => canvas.Counts.Failed);

    public bool HasFailed => FailedCount > 0;

    /// <summary>How many of its nodes run now, which a folded row sums too.</summary>
    public int RunningCount => Workflows.Sum(canvas => canvas.RunningCount);

    public bool HasRunningNodes => RunningCount > 0;

    public string RunningLabel => RunningCount == 1 ? "1 node runs" : $"{RunningCount} nodes run";

    public string WaitingLabel => WaitingCount == 1 ? "1 node needs you" : $"{WaitingCount} nodes need you";

    public string FailedLabel => FailedCount == 1 ? "1 node failed" : $"{FailedCount} nodes failed";

    public bool HasNoWorkflows => Workflows.Count == 0;

    /// <summary>The nodes that run now in every workflow of the project.</summary>
    internal IEnumerable<TaskNodeViewModel> RunningNodes => Workflows.SelectMany(canvas => canvas.Nodes).Where(node => node.State == NodeState.Running);

    internal IEnumerable<WorkflowDocument> UnsavedDocuments =>
        Workflows.Select(canvas => canvas.Document).Where(document => document.HasUnsavedChanges);

    /// <summary>The inspector and card attention ask the window to open a conversation through this one route.</summary>
    internal event Action<ConversationTarget>? ConversationRequested;

    internal void OpenConversation(ConversationTarget target) => ConversationRequested?.Invoke(target);

    /// <summary>A card or an Index row asks the window to open a node's Room through this one route.</summary>
    internal event Action<ConversationTarget>? RoomRequested;

    internal void OpenRoom(ConversationTarget target) => RoomRequested?.Invoke(target);

    /// <summary>The task's conversation state in the workflow, kept while the project stays open.</summary>
    internal ConversationState ConversationOf(WorkflowCanvasViewModel canvas, TaskId task)
    {
        var key = new ConversationKey(new WorkflowRef(Folder, canvas.Workflow.Id), task);
        if (!_conversations.TryGetValue(key, out var state))
        {
            _conversations.Add(key, state = new ConversationState());
        }

        return state;
    }

    internal WorkflowCanvasViewModel Add(WorkflowDocument document)
    {
        var canvas = _newCanvas(this, document);
        canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowCanvasViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(IsRunning));
            }
            else if (e.PropertyName == nameof(WorkflowCanvasViewModel.Summary))
            {
                OnPropertyChanged(nameof(IsWaiting));
                OnPropertyChanged(nameof(WaitingCount));
                OnPropertyChanged(nameof(FailedCount));
                OnPropertyChanged(nameof(HasFailed));
                OnPropertyChanged(nameof(WaitingLabel));
                OnPropertyChanged(nameof(FailedLabel));
                OnPropertyChanged(nameof(RunningCount));
                OnPropertyChanged(nameof(HasRunningNodes));
                OnPropertyChanged(nameof(RunningLabel));
            }
        };
        Workflows.Add(canvas);
        OnPropertyChanged(nameof(HasNoWorkflows));
        return canvas;
    }

    /// <summary>
    /// Closes each workflow's Generate sheet, which listens to the window's client directory until it closes, and stops
    /// each workflow's running nodes, which reopen as interrupted.
    /// </summary>
    internal async ValueTask CloseAsync()
    {
        foreach (var canvas in Workflows)
        {
            canvas.CloseSheet();
            await canvas.DisposeEngineAsync();
        }

        _lock.Dispose();
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
}
