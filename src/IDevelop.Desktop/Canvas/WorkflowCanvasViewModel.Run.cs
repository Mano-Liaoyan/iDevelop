using System.Collections.Immutable;
using System.Windows.Input;
using IDevelop.Clients;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Desktop.Theme;
using IDevelop.Engine;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The workflow's engine and the controls that act on it (ADR 0017): Run Workflow beside Generate, and the bar at the
/// bottom center that counts what runs and waits, with Stop Workflow, and Merge into my branch while the engine offers it.
/// The canvas keeps its engine while the project stays open, so switching to another workflow or project stops nothing.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private ImmutableArray<Blueprint> _placeable = [];
    private RelayCommand? _runWorkflow;
    private RelayCommand? _stopAll;
    private RelayCommand? _merge;
    private UiBatch _engineChanged = null!;
    private HashSet<TaskNodeViewModel> _running = [];

    /// <summary>Opens a folder in the system's file manager, and returns whether it opened. The window sets it.</summary>
    internal Func<string, Task<bool>> OpenFolder { get; init; } = _ => Task.FromResult(false);

    internal Func<string, Task<bool>> OpenFile { get; init; } = _ => Task.FromResult(false);

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
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(RunsNothing));
                OnPropertyChanged(nameof(CanStartRun));
            }
        }
    }

    /// <summary>No node runs, so Run Workflow is the bar's primary action. While one runs, Stop Workflow takes its place.</summary>
    public bool RunsNothing => RunningCount == 0;

    /// <summary>How many nodes Run Workflow would start. A node that needs the person keeps its turn, so it does not count.</summary>
    public int RunWork => Nodes.Count(node => node.State is NodeState.Idle or NodeState.Stale or NodeState.Failed);

    /// <summary>Run Workflow has work to start. With none its tooltip says why.</summary>
    public bool CanStartRun => !IsEmpty && RunsNothing && RunWork > 0;

    /// <summary>The toolbar offers Generate while the canvas's own Generate card does not. A plan in progress or under review keeps it, because it reports that state.</summary>
    public bool ShowsGenerate => !IsEmpty || IsPlanning || IsReviewing;

    /// <summary>Every node is completed and nothing is stale, so the bar offers Merge into my branch.</summary>
    public bool CanMergeIntoBranch
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>The bar shows while a node runs, waits for the person, or failed, or while Merge into my branch is offered.</summary>
    public bool ShowsBar => RunningCount > 0 || HasAttention || CanMergeIntoBranch;

    /// <summary>A node waits for the person or failed, so the bar offers Next.</summary>
    public bool HasAttention => Nodes.Any(NeedsAttention);

    /// <summary>"5/9 done · 3 need you · 1 failed · 1 running", with the parts that are not zero.</summary>
    public string BarSummary
    {
        get
        {
            var counts = Counts;
            return string.Join(" · ", new[]
            {
                counts.Completed > 0 ? BarProgress : null,
                counts.Waiting > 0 ? StateCounts.NeedYou(counts.Waiting) : null,
                counts.Failed > 0 ? $"{counts.Failed} failed" : null,
                counts.Running > 0 ? $"{counts.Running} running" : null,
            }.OfType<string>());
        }
    }

    /// <summary>How far the work went, in the one format of every bar width: "5/9 done". The chips after it hold the other states.</summary>
    public string BarProgress => $"{Counts.Completed}/{Counts.Total} done";

    /// <summary>What the band says with no run: "9 nodes", or how far the work went.</summary>
    public string BarIdle => Counts.Completed > 0 ? BarProgress : Counts.Total == 1 ? "1 node" : $"{Counts.Total} nodes";

    /// <summary>
    /// One chip for each group of nodes that the bar names, in the rollup order: Needs you, Failed, Running, Stale. What waits for the
    /// person is one group under one word, and its tooltip names the reasons.
    /// </summary>
    public IReadOnlyList<StateChip> BarChips
    {
        get
        {
            var counts = Counts;
            var reasons = Nodes.Where(node => node.IsWaiting).GroupBy(node => node.WaitReason).OrderBy(group => group.Key).ToList();
            var tip = string.Join(", ", reasons.Select(group => $"{group.Count()} {NodeStates.Need(group.Key).ToLowerInvariant()}"));
            return new[]
                {
                    StateChip.Of(NodeState.InputRequired, reasons is [var only] ? only.Key : WaitReason.Question, counts.Waiting, StateCounts.NeedYou(counts.Waiting), tip),
                    StateChip.Of(NodeState.Failed, WaitReason.None, counts.Failed, $"{counts.Failed} failed"),
                    StateChip.Of(NodeState.Running, WaitReason.None, counts.Running, $"{counts.Running} running"),
                    StateChip.Of(NodeState.Stale, WaitReason.None, counts.Stale, $"{counts.Stale} stale"),
                }
                .OfType<StateChip>().ToList();
        }
    }

    /// <summary>What Next selects: the group it goes to, so the button says where it leads.</summary>
    public string NextLabel => Counts.Waiting > 0 ? "Next that needs you" : "Next failed";

    public StatusTone BarTone => RunningCount > 0 ? StatusTone.Running : HasAttention ? StatusTone.Waiting : StatusTone.Complete;

    /// <summary>The running nodes' titles, or null.</summary>
    public string? BarActivity => RunningCount == 0 ? null
        : string.Join(", ", Nodes.Where(node => node.State == NodeState.Running).Select(node => $"\"{node.Title}\""));

    /// <summary>The bar's tooltip: the whole summary, then the running nodes' titles.</summary>
    public string BarTip => string.Join("\n", new[]
    {
        BarSummary,
        BarActivity,
    }.OfType<string>());

    /// <summary>While this is on, the view shows each node that starts to run. The view never moves without this choice.</summary>
    public bool IsFollowing
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(FollowLabel));
            }
        }
    }

    public string FollowLabel => IsFollowing ? "Stop following running nodes" : "Follow running nodes";

    /// <summary>The tooltip of Run Workflow: what it runs, or why nothing can run.</summary>
    public string RunWorkflowTip => (Nodes.Count, RunWork) switch
    {
        (0, _) => "Run Workflow\nAdd a node first. An empty workflow has nothing to run.",
        (_, 0) => "Run Workflow\nNothing to run. Each node has a current result or waits for you.",
        var (total, runs) => $"Run Workflow\n{CanvasKeys.Hint(CanvasKeys.RunWorkflow)}: run {(runs == total ? "every node" : $"{runs} of {total} nodes")} along the dependencies. Nodes with a current result keep it.",
    };

    private static bool NeedsAttention(TaskNodeViewModel node) => node.IsWaiting || node.State == NodeState.Failed;

    private void InitializeEngine(IReadOnlyDictionary<string, string> git)
    {
        Engine = new WorkflowEngine(Project.Folder, Document.Current, Clients, TimeSpan.FromSeconds(5), git) { Planning = Planning };
        // The engine tells of each change, often several at once and off the UI thread, so the canvas reads once per batch.
        _engineChanged = new UiBatch(() =>
        {
            if (Workflow.Tasks.Keys.All(id => Nodes.Any(node => node.Id == id)))
            {
                ShowEngine();
            }
        });
        Engine.NodeChanged += (_, _) => _engineChanged.Request();
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
            node.ShowReviewer(Nodes.FirstOrDefault(other => other is { IsReview: true, State: NodeState.Running } && Precedes(node.Id, other.Id))?.Title);
        }

        foreach (var connection in Connections)
        {
            connection.ShowFlow();
        }

        foreach (var node in Nodes)
        {
            ShowPorts(node);
        }

        var running = Nodes.Where(node => node.State == NodeState.Running).ToHashSet();
        if (IsFollowing && running.Except(_running).FirstOrDefault() is { } started)
        {
            View?.Reveal(started);
        }

        _running = running;
        RunningCount = running.Count;
        ArrangeTreeRows();
        CanMergeIntoBranch = Engine.CanMergeIntoBranch;
        IsRunning = RunningCount > 0;
        OnWaitingChanged();
        OnPropertyChanged(nameof(ShowsBar));
        OnPropertyChanged(nameof(BarSummary));
        OnPropertyChanged(nameof(BarProgress));
        OnPropertyChanged(nameof(BarIdle));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(BarChips));
        OnPropertyChanged(nameof(RunWork));
        OnPropertyChanged(nameof(CanStartRun));
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(BarTone));
        OnPropertyChanged(nameof(BarActivity));
        OnPropertyChanged(nameof(BarTip));
        OnPropertyChanged(nameof(RunWorkflowTip));
        OnSummaryChanged();
        _runWorkflow?.NotifyCanExecuteChanged();
        _stopAll?.NotifyCanExecuteChanged();
        _merge?.NotifyCanExecuteChanged();
        ShowGhosts();
    }

    // A filled dot says that an input is ready or that an output holds a current result. Each tooltip names what it says.
    private void ShowPorts(TaskNodeViewModel node)
    {
        var sources = Connections.Where(wire => wire.To == node && wire.Kind == ConnectionKind.Dependency).Select(wire => wire.From).ToList();
        node.Input.IsLive = sources.Count > 0 && sources.All(source => source.State == NodeState.Completed);
        node.Input.Tip = sources.Count == 0 ? "Input: no earlier node" : $"Input: {string.Join(", ", sources.Select(source => source.Title))}";
        node.Output.IsLive = node.HasResults && node.State != NodeState.Stale;
        node.Output.Tip = node.OutputLine;
        node.ShowInputCount(sources.Count);
    }

    /// <summary>
    /// What a planner may fill and place, which its prompt names: the palette's blueprints are its types, and the clients
    /// ready on this machine are the agents it chooses from for each task it adds, and those still being checked are left
    /// out. The engine asks for it when the planner starts, on any thread, and its log records the handles.
    /// </summary>
    private PlanningContext Planning(TaskId planner)
    {
        var clients = Clients.Current;
        return PlanningFor(planner) with
        {
            Agents = PlanningContext.Offers(clients),
            Checking = [.. ClientCatalog.All.Where(id => clients[id] is ClientStatus.Checking)],
        };
    }

    /// <summary>What a planner may fill and place in the workflow as it is now, which reading its proposal names again.</summary>
    internal PlanningContext PlanningFor(TaskId planner) => PlanningContext.For(Workflow, planner, _placeable, HasStarted);

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

    /// <summary>Whether <paramref name="before"/> is a dependency predecessor of <paramref name="after"/>.</summary>
    private bool Precedes(TaskId before, TaskId after) =>
        Workflow.Connections.TryGetValue(new ConnectionKey(before, after), out var kind) && kind == ConnectionKind.Dependency;
}
