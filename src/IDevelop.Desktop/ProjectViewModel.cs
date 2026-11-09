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

    /// <summary>"repo/harbor-api": the folder's last two segments. The Index cuts it at the start, and the row's tooltip holds the whole path.</summary>
    public string FolderTail
    {
        get
        {
            var parts = Folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('/', parts[Math.Max(0, parts.Length - 2)..]);
        }
    }

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

    /// <summary>How many of the project's nodes are in each state.</summary>
    public Theme.StateCounts Counts => Workflows.Aggregate(default(Theme.StateCounts), (sum, canvas) => sum + canvas.Counts);

    /// <summary>The project's one State Mark, or null when no mark shows.</summary>
    public NodeState? Rollup => Counts.Rollup;

    /// <summary>The reason inside a Needs you rollup mark.</summary>
    public WaitReason RollupWhy => NodeStates.CommonReason(Workflows.SelectMany(canvas => canvas.Nodes));

    public bool HasRollup => Rollup is not null;

    /// <summary>A node of the project runs.</summary>
    public bool IsRunning => Workflows.Any(canvas => canvas.IsRunning);

    /// <summary>How many of the project's nodes wait for the person.</summary>
    public int WaitingCount => Counts.Waiting;

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
                OnPropertyChanged(nameof(WaitingCount));
                OnPropertyChanged(nameof(Counts));
                OnPropertyChanged(nameof(Rollup));
                OnPropertyChanged(nameof(RollupWhy));
                OnPropertyChanged(nameof(HasRollup));
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
