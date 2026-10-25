using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Engine;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The workflow's engine and the controls that act on it (ADR 0017): Run Workflow beside Generate, and the bar at the
/// bottom center that counts what runs and waits, with Stop All, and Merge into my branch while the engine offers it.
/// The canvas keeps its engine while the project stays open, so switching to another workflow or project stops nothing.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private readonly ConcurrentDictionary<TaskId, ImmutableArray<ClientId>> _checkingAtStart = [];
    private readonly ConcurrentDictionary<TaskId, PlanningHandles> _handles = [];
    private ImmutableArray<Blueprint> _placeable = [];
    private RelayCommand? _runWorkflow;
    private RelayCommand? _stopAll;
    private RelayCommand? _merge;
    private int _engineChanged;
    private int _runningCount;
    private bool _canMerge;

    /// <summary>Opens a folder in the system's file manager, and returns whether it opened. The window sets it.</summary>
    internal Func<string, Task<bool>> OpenFolder { get; set; } = _ => Task.FromResult(false);

    /// <summary>The engine that runs this workflow's nodes and routes their results.</summary>
    internal WorkflowEngine Engine { get; private set; } = null!;

    /// <summary>Runs every node that has no current result, in dependency order. No node needs to be selected.</summary>
    public ICommand RunWorkflowCommand => _runWorkflow ??= new RelayCommand(RunWorkflow, () => Sheet is null && Nodes.Count > 0);

    /// <summary>Stops every running node. Finished results stay.</summary>
    public ICommand StopAllCommand => _stopAll ??= new RelayCommand(() => _ = Engine.StopAll(), () => RunningCount > 0);

    /// <summary>Merges the end nodes' commits into the checked-out branch, offered only while every node is completed and current.</summary>
    public ICommand MergeIntoBranchCommand => _merge ??= new RelayCommand(MergeIntoBranch, () => CanMergeIntoBranch);

    /// <summary>How many nodes run now.</summary>
    public int RunningCount
    {
        get => _runningCount;
        private set => SetProperty(ref _runningCount, value);
    }

    /// <summary>Every node is completed and nothing is stale, so the bar offers Merge into my branch.</summary>
    public bool CanMergeIntoBranch
    {
        get => _canMerge;
        private set => SetProperty(ref _canMerge, value);
    }

    /// <summary>The bar shows while a node runs or waits for the person, or while Merge into my branch is offered.</summary>
    public bool ShowsBar => RunningCount > 0 || WaitingCount > 0 || CanMergeIntoBranch;

    /// <summary>"2 running · 1 waits for you", "Every node is complete", or empty.</summary>
    public string BarSummary => string.Join(" · ", new[]
    {
        RunningCount > 0 ? $"{RunningCount} running" : null,
        WaitingCount switch { 0 => null, 1 => "1 waits for you", var count => $"{count} wait for you" },
        RunningCount == 0 && WaitingCount == 0 && CanMergeIntoBranch ? "Every node is complete" : null,
    }.OfType<string>());

    public StatusTone BarTone => RunningCount > 0 ? StatusTone.Running : WaitingCount > 0 ? StatusTone.Waiting : StatusTone.Complete;

    /// <summary>The running nodes' titles, or null.</summary>
    public string? BarActivity => RunningCount == 0 ? null
        : string.Join(", ", Nodes.Where(node => node.State == NodeState.Running).Select(node => $"\"{node.Title}\""));

    private void InitializeEngine(IReadOnlyDictionary<string, string> git)
    {
        Engine = new WorkflowEngine(Project.Folder, Document.Current, Clients, TimeSpan.FromSeconds(5), git) { Planning = Planning };
        Engine.NodeChanged += (_, _) => PostShowEngine();
    }

    /// <summary>Stops every node's turn, which reopens as interrupted, as the project's closing does.</summary>
    internal ValueTask DisposeEngineAsync()
    {
        foreach (var node in Nodes)
        {
            node.StopFollowingLog();
        }

        return Engine.DisposeAsync();
    }

    /// <summary>
    /// Reads every node from the engine again, and what the bar and the sidebar show. A node's change can change what the
    /// nodes after it may do, so every node reads.
    /// </summary>
    internal void ShowEngine()
    {
        _placeable = [.. Blueprints.Placeable];
        foreach (var node in Nodes)
        {
            node.ShowEngine();
        }

        foreach (var node in Nodes)
        {
            node.ShowReviewer(Nodes.FirstOrDefault(other => other.IsReview && other.State == NodeState.Running && Precedes(node.Id, other.Id))?.Title);
        }

        RunningCount = Nodes.Count(node => node.State == NodeState.Running);
        CanMergeIntoBranch = Engine.CanMergeIntoBranch;
        IsRunning = RunningCount > 0;
        OnWaitingChanged();
        OnPropertyChanged(nameof(ShowsBar));
        OnPropertyChanged(nameof(BarSummary));
        OnPropertyChanged(nameof(BarTone));
        OnPropertyChanged(nameof(BarActivity));
        _runWorkflow?.NotifyCanExecuteChanged();
        _stopAll?.NotifyCanExecuteChanged();
        _merge?.NotifyCanExecuteChanged();
        ShowGhosts();
    }

    /// <summary>
    /// What a planner may fill and place, which its prompt names: the palette's blueprints are its types, and the clients
    /// ready on this machine are the agents it chooses from for each task it adds. The engine asks for it when the planner
    /// starts, on any thread.
    /// </summary>
    internal PlanningContext? Planning(TaskId planner)
    {
        var clients = Clients.Current;
        _checkingAtStart[planner] = [.. IDevelop.Execution.Clients.All.Where(id => clients[id] is ClientStatus.Checking)];
        var context = PlanningFor(planner) with { Agents = IDevelop.Nodes.PlanningContext.Offers(clients) };
        _handles[planner] = context.Handles(planner.Value);
        return context;
    }

    /// <summary>
    /// The handles the planner's prompt named, which its proposal's slots and types refer to: those of its last start in
    /// this window, else those of the workflow as it is now.
    /// </summary>
    internal PlanningHandles HandlesOf(TaskId planner) => _handles.GetValueOrDefault(planner) ?? PlanningFor(planner).Handles(planner.Value);

    /// <summary>What a planner may fill and place in the workflow as it is now, which reading its proposal names again.</summary>
    internal PlanningContext PlanningFor(TaskId planner) => IDevelop.Nodes.PlanningContext.For(Workflow, planner, _placeable, HasStarted);

    /// <summary>
    /// The clients that this window was still checking when it last started the planner, which its prompt therefore left
    /// out, so the proposal can say so. Empty for a planner this window has not started.
    /// </summary>
    internal ImmutableArray<ClientId> CheckingAtStart(TaskId planner) => _checkingAtStart.GetValueOrDefault(planner, []);

    /// <summary>Whether the node has run: it runs, waits, or has a result.</summary>
    internal bool HasStarted(TaskId task) => Workflow.Tasks.ContainsKey(task) && Engine.State(task) is not IDevelop.Engine.NodeState.Idle;

    private void RunWorkflow() => Act(() =>
    {
        _ = Engine.RunWorkflow();
        return null;
    }, "Couldn't run the workflow.");

    private void MergeIntoBranch() => Act(() =>
    {
        _ = Engine.MergeIntoBranch();
        return "Merged the workflow's work into your branch.";
    }, "Couldn't merge into your branch.");

    /// <summary>
    /// Runs an engine command and shows its notice, or why the engine refused it, or what kept it from writing, as a node's
    /// own commands do. Nothing it throws reaches the window.
    /// </summary>
    private void Act(Func<string?> command, string failure)
    {
        try
        {
            Notice(command());
        }
        catch (InvalidOperationException e)
        {
            Notice(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice($"{failure} {e.Message}");
        }

        ShowEngine();
    }

    /// <summary>The engine tells of each change, often several at once and off the UI thread, so the canvas reads once per batch.</summary>
    private void PostShowEngine()
    {
        if (Interlocked.Exchange(ref _engineChanged, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _engineChanged, 0);
            if (Workflow.Tasks.Keys.All(id => Nodes.Any(node => node.Id == id)))
            {
                ShowEngine();
            }
        });
    }

    /// <summary>Whether <paramref name="before"/> is a dependency predecessor of <paramref name="after"/>.</summary>
    private bool Precedes(TaskId before, TaskId after) =>
        Workflow.Connections.TryGetValue(new ConnectionKey(before, after), out var kind) && kind == ConnectionKind.Dependency;
}
