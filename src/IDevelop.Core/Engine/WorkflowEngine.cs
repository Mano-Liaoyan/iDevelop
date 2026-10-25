using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>
/// Runs one workflow's nodes and routes their results. Every ready node starts at once, where ready means every dependency
/// predecessor is completed and current. A node's completion starts each successor that is ready and has no current
/// result, in a fresh session given the predecessors' latest results. A person's message and a send back continue the
/// node's own session. When a node starts a turn, every running node after it stops and goes stale, and it runs again once
/// its inputs are complete. When the person edits a running node's basis, the node stops, goes stale, and waits for their
/// Run. Each node's state is its log replayed, whether a turn runs, and its current basis, so nothing else is stored.
/// A node that edits the project works in its own worktree, which a fresh session resets to the merge of its predecessors'
/// commits, and its completed turn is one commit there. A node that only reads runs in the project folder and hands on
/// the commits it was given.
/// </summary>
public sealed class WorkflowEngine : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly string _project;
    private readonly CliAgent _cli;
    private readonly Worktrees _worktrees;
    private readonly Dictionary<TaskId, NodeLog> _logs = [];
    private readonly Dictionary<TaskId, Live> _live = [];
    private readonly Lock _dispatch = new();
    private readonly Queue<(TaskId Node, NodeState State)> _events = [];
    private Workflow _workflow;
    private ImmutableDictionary<TaskId, NodeState> _published;

    public WorkflowEngine(string projectFolder, Workflow workflow, ClientDirectory clients)
        : this(projectFolder, workflow, clients, TimeSpan.FromSeconds(5), new Dictionary<string, string>()) { }

    internal WorkflowEngine(string projectFolder, Workflow workflow, ClientDirectory clients, TimeSpan shutdownTime,
        IReadOnlyDictionary<string, string> gitEnvironment)
    {
        _project = projectFolder;
        _cli = new CliAgent(clients, projectFolder) { ShutdownTime = shutdownTime };
        _worktrees = new Worktrees(projectFolder, workflow.Id, gitEnvironment);
        _workflow = workflow;
        _published = States();
    }

    /// <summary>Raised after a node's state changes, in the order the changes happened, one at a time.</summary>
    public event Action<TaskId, NodeState>? NodeChanged;

    private enum Halt { None, Person, Message, Upstream, Edited, Closing }

    /// <summary>Follows the person's edit of the workflow. A running node whose basis the edit changes stops and goes stale.</summary>
    public void Update(Workflow workflow) => Change(() =>
    {
        foreach (var (id, live) in _live)
        {
            if (!workflow.Tasks.ContainsKey(id))
            {
                RequestStop(live, Halt.Person);
            }
            else if (BasisIn(workflow, id) != BasisIn(_workflow, id))
            {
                RequestStop(live, Halt.Edited);
            }
        }

        _workflow = workflow;
    });

    public NodeState State(TaskId node)
    {
        lock (_gate) return StateOf(node);
    }

    public IReadOnlyList<NodeResult> History(TaskId node)
    {
        lock (_gate) return Log(node).Record.History;
    }

    /// <summary>The node's log as it stands: what the agent and the person said in each turn, and how each turn ended.</summary>
    internal ImmutableList<NodeEntry> Entries(TaskId node)
    {
        lock (_gate) return Log(node).Entries;
    }

    /// <summary>Null when the node can start a fresh turn now, or the reason it cannot.</summary>
    public string? CanRun(TaskId node)
    {
        lock (_gate) return Refusal(node);
    }

    /// <summary>Starts a fresh session of the node on its predecessors' latest results.</summary>
    public Task Run(TaskId node)
    {
        Change(() => StartFresh(node));
        return Task.CompletedTask;
    }

    public Task Retry(TaskId node) => Run(node);

    /// <summary>Starts every ready node that has no current result and waits for nobody. The rest follow as their inputs complete.</summary>
    public Task RunWorkflow()
    {
        Change(() =>
        {
            foreach (var id in _workflow.Tasks.Keys)
            {
                if (StateOf(id) is NodeState.Idle or NodeState.Stale or NodeState.Failed && Refusal(id) is null)
                {
                    StartFresh(id);
                }
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Stops the node's turn and returns once it ended. The node keeps its latest result.</summary>
    public Task Stop(TaskId node)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(node, out var live)) return Task.CompletedTask;
            RequestStop(live, Halt.Person);
            return live.Done;
        }
    }

    public Task StopAll()
    {
        lock (_gate)
        {
            foreach (var live in _live.Values) RequestStop(live, Halt.Person);
            return Task.WhenAll(_live.Values.Select(live => live.Done));
        }
    }

    /// <summary>
    /// Continues the node's session with the person's message. While a turn runs, the message waits for its end, or with
    /// <paramref name="interrupt"/> it stops the turn first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node has no session to continue.</exception>
    public Task Send(TaskId node, string text, bool interrupt)
    {
        Change(() =>
        {
            if (_live.TryGetValue(node, out var live))
            {
                // The log records the message when its turn starts.
                live.Queued = live.Queued is { } queued ? $"{queued}\n\n{text}" : text;
                if (interrupt) RequestStop(live, Halt.Message);
                return;
            }

            Continue(node, text);
        });
        return Task.CompletedTask;
    }

    /// <summary>Accepts what a waiting node offers as its result: an Approval node's inputs, or a Chat node's last reply.</summary>
    /// <exception cref="InvalidOperationException">The node waits for nothing the person can approve.</exception>
    public Task Approve(TaskId node)
    {
        Change(() =>
        {
            var log = Log(node);
            if (Waiting(node) is not { Why: NodeState.Approval or NodeState.Reply } waiting || log.Record.SessionBasis is not { } basis)
            {
                throw new InvalidOperationException("The node waits for no approval.");
            }

            log.Append(Finish(_workflow.Tasks[node], node, ResultBlock.Prose(waiting.Text), basis));
            if (log.Record.End is NodeEntry.Completed) Route(node);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Commits the person's resolution of the conflict the node waits for, on the conflict's parents, and starts the node's
    /// fresh turn from there.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node waits for no conflict, or a conflicting file still holds markers.</exception>
    public Task MarkResolved(TaskId node)
    {
        Change(() =>
        {
            if (Waiting(node) is not { Why: NodeState.Conflict } || Log(node).Record.TurnBasis is not { } basis)
            {
                throw new InvalidOperationException("The node waits for no conflict.");
            }

            if (_worktrees.MarkersRemain(node) is { IsEmpty: false } remaining)
            {
                throw new InvalidOperationException($"Conflict markers remain in {string.Join(", ", remaining)}.");
            }

            Begin(node, basis, () => _worktrees.Commit(node, "Resolve the conflicting merge", InputCommits(node)));
        });
        return Task.CompletedTask;
    }

    /// <summary>Whether Merge into my branch is offered: every node is completed and nothing is stale.</summary>
    public bool CanMergeIntoBranch
    {
        get
        {
            lock (_gate) return _workflow.Tasks.Count > 0 && _workflow.Tasks.Keys.All(id => StateOf(id) is NodeState.Completed);
        }
    }

    /// <summary>Merges every end node's commit, the commits of the nodes with no successors, into the checked-out branch.</summary>
    /// <exception cref="InvalidOperationException">
    /// A node is not completed or is stale, the end nodes conflict with each other, or the branch moved and the merge conflicts.
    /// </exception>
    public Task MergeIntoBranch()
    {
        lock (_gate)
        {
            if (!CanMergeIntoBranch) throw new InvalidOperationException("Every node must be completed and current first.");
            _worktrees.MergeIntoBranch([.. _workflow.Tasks.Keys.Where(id => !Successors(id).Any()).SelectMany(Handed).Distinct()]);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends an Approval node's inputs back with the person's note, which continues each predecessor's own session. The
    /// Approval node asks again once they complete.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node is no Approval node that waits.</exception>
    public Task SendBack(TaskId node, string note)
    {
        Change(() =>
        {
            if (_workflow.Tasks[node].Blueprint.Work is not WorkSpec.Person || Waiting(node) is null)
            {
                throw new InvalidOperationException("The node waits for no approval.");
            }

            foreach (var predecessor in Predecessors(_workflow, node))
            {
                if (_live.ContainsKey(predecessor)) throw new InvalidOperationException($"{Title(predecessor)} is running.");
            }

            Log(node).Append(new NodeEntry.PersonMessage(note));
            Log(node).Append(new NodeEntry.Stopped());
            foreach (var predecessor in Predecessors(_workflow, node).Where(id => Log(id).Record.Session is not null))
            {
                Continue(predecessor, note);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Stops every turn without ending it in the log, so each reopens as interrupted.</summary>
    public async ValueTask DisposeAsync()
    {
        Task all;
        lock (_gate)
        {
            foreach (var live in _live.Values) RequestStop(live, Halt.Closing);
            all = Task.WhenAll(_live.Values.Select(live => live.Done));
        }

        await all;
    }

    private void StartFresh(TaskId id)
    {
        if (Refusal(id) is { } reason) throw new InvalidOperationException(reason);
        var basis = BasisIn(_workflow, id);
        if (Edits(_workflow.Tasks[id]))
        {
            Begin(id, basis, () => _worktrees.Reset(id, InputCommits(id)));
        }
        else
        {
            Start(id, basis, Prompt(id, null), session: null);
        }
    }

    /// <summary>
    /// Starts an editing node's fresh turn once <paramref name="prepare"/> readied its worktree. A conflict goes to a Merge
    /// node's agent, or waits for the person. A Merge node whose inputs merged cleanly completes without a turn.
    /// </summary>
    private void Begin(TaskId id, Basis basis, Func<WorktreeStart> prepare)
    {
        var node = _workflow.Tasks[id];
        WorktreeStart start;
        try
        {
            start = prepare();
        }
        catch (GitFailure e)
        {
            Settle(id, basis, new NodeEntry.Failed(e.Message));
            return;
        }

        switch (start)
        {
            case WorktreeStart.Conflict conflict when node.Blueprint.Work is WorkSpec.Merge:
                Start(id, basis, Prompt(id, conflict), session: null);
                break;
            case WorktreeStart.Conflict conflict:
                Settle(id, basis, new NodeEntry.Waiting(NodeState.Conflict, conflict.Description));
                break;
            case WorktreeStart.Ready ready when node.Blueprint.Work is WorkSpec.Merge:
                Settle(id, basis, new NodeEntry.Completed(new NodeResult(NewId(), Inputs(id), ready.Commit.Hex, basis)));
                Route(id);
                break;
            default:
                Start(id, basis, Prompt(id, null), session: null);
                break;
        }
    }

    /// <summary>Logs a fresh turn that ends as soon as it starts, with <paramref name="end"/>.</summary>
    private void Settle(TaskId id, Basis basis, NodeEntry end)
    {
        StopDescendants(id);
        Log(id).Append(new NodeEntry.TurnStarted(basis, null));
        Log(id).Append(end);
    }

    /// <summary>
    /// How a turn that is done ends: with its result, which for an editing node holds its worktree's commit. When the commit
    /// still lacks some of the predecessors' commits and merging them conflicts, the node waits for the person.
    /// </summary>
    private NodeEntry Finish(TaskDefinition node, TaskId id, string report, Basis basis)
    {
        if (!Edits(node)) return new NodeEntry.Completed(new NodeResult(NewId(), report, null, basis));
        try
        {
            return _worktrees.Commit(id, node.Title.Length > 0 ? node.Title : node.Blueprint.Name, InputCommits(id)) switch
            {
                WorktreeStart.Ready ready => new NodeEntry.Completed(new NodeResult(NewId(), report, ready.Commit.Hex, basis)),
                WorktreeStart.Conflict conflict => new NodeEntry.Waiting(NodeState.Conflict, conflict.Description),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (GitFailure e)
        {
            return new NodeEntry.Failed(e.Message);
        }
    }

    private void Continue(TaskId id, string text)
    {
        var log = Log(id);
        if (_live.ContainsKey(id) || log.Record is not { Session: { } session, SessionBasis: { } basis })
        {
            throw new InvalidOperationException("The node has no session to continue.");
        }

        log.Append(new NodeEntry.PersonMessage(text));
        Start(id, basis, text, session);
    }

    private void Start(TaskId id, Basis basis, string prompt, string? session)
    {
        var node = _workflow.Tasks[id];
        var agent = AgentOf(node) ?? throw new InvalidOperationException("This node kind does not run yet.");
        StopDescendants(id);
        var log = Log(id);
        log.Append(new NodeEntry.TurnStarted(basis, session));
        var folder = Edits(node) ? _worktrees.Folder(id) : _project;
        var live = new Live();
        _live[id] = live;
        live.Done = Task.Run(() => Drive(id, node, agent, log, live, basis, folder, prompt, session));
    }

    private async Task Drive(TaskId id, TaskDefinition node, IAgent agent, NodeLog log, Live live, Basis basis, string folder, string prompt, string? session)
    {
        TurnOutcome outcome;
        try
        {
            outcome = await agent.Turn(node, folder, prompt, session, e => log.Append(new NodeEntry.Agent(e)), live.Stop.Token);
        }
        catch (Exception e)
        {
            outcome = new TurnOutcome.Failed(e.Message);
        }

        Change(() =>
        {
            _live.Remove(id);
            if (live.Halt == Halt.Closing) return;
            log.Append(live.Halt != Halt.None ? new NodeEntry.Stopped() : outcome switch
            {
                TurnOutcome.Done done => Finish(node, id, done.Report, basis),
                TurnOutcome.Waits waits => new NodeEntry.Waiting(waits.Why, waits.Text),
                TurnOutcome.Failed failed => new NodeEntry.Failed(failed.Reason),
                _ => new NodeEntry.Stopped(),
            });
            var continues = live.Halt is Halt.None or Halt.Message && log.Record.Session is not null && _workflow.Tasks.ContainsKey(id);
            if (live.Queued is { } message && continues)
            {
                Continue(id, message);
            }
            else if (live.Queued is { } dropped)
            {
                // A stop, an upstream change, or an edit ends the session's turn, so the message never reaches the agent.
                log.Append(new NodeEntry.MessageDropped(dropped));
            }
            else if (log.Record.End is NodeEntry.Completed && _workflow.Tasks.ContainsKey(id))
            {
                Route(id);
            }
            else if (live.Halt == Halt.Upstream && _workflow.Tasks.ContainsKey(id) && Refusal(id) is null)
            {
                // The upstream completed before this turn ended, so its routing passed this node by.
                StartFresh(id);
            }
        });
    }

    /// <summary>Hands a new result on: each successor that is ready and has no current result starts fresh.</summary>
    private void Route(TaskId id)
    {
        foreach (var next in Successors(id))
        {
            if (StateOf(next) is not (NodeState.Running or NodeState.Completed) && Refusal(next) is null)
            {
                StartFresh(next);
            }
        }
    }

    private void StopDescendants(TaskId id)
    {
        var queue = new Queue<TaskId>(Successors(id));
        var seen = new HashSet<TaskId>();
        while (queue.TryDequeue(out var next))
        {
            if (!seen.Add(next)) continue;
            if (_live.TryGetValue(next, out var live)) RequestStop(live, Halt.Upstream);
            foreach (var after in Successors(next)) queue.Enqueue(after);
        }
    }

    private static void RequestStop(Live live, Halt halt)
    {
        if (live.Halt == Halt.Closing) return;
        live.Halt = halt;
        live.Stop.Cancel();
    }

    private string? Refusal(TaskId id)
    {
        var node = _workflow.Tasks[id];
        if (_live.ContainsKey(id)) return "It is running.";
        foreach (var predecessor in Predecessors(_workflow, id))
        {
            if (StateOf(predecessor) is not NodeState.Completed) return $"It waits for {Title(predecessor)}.";
        }

        if (AgentOf(node) is not { } agent) return "This node kind does not run yet.";
        return agent.Problem(node) ?? (Edits(node) ? _worktrees.Problem : null);
    }

    private static bool Edits(TaskDefinition node) => node.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit } or WorkSpec.Merge;

    /// <summary>The distinct commits the node's predecessors hand on, in predecessor order.</summary>
    private ImmutableArray<CommitId> InputCommits(TaskId id) => [.. Predecessors(_workflow, id).SelectMany(Handed).Distinct()];

    /// <summary>The node's latest commit, or the commits it was given when its latest result has none, as a node that only reads.</summary>
    private IEnumerable<CommitId> Handed(TaskId id) => Log(id).Record.History.LastOrDefault() is { Commit: { } commit }
        ? [new CommitId(commit)]
        : Predecessors(_workflow, id).SelectMany(Handed);

    private NodeEntry.Waiting? Waiting(TaskId id) =>
        !_live.ContainsKey(id) && Log(id).Record is { Open: false, End: NodeEntry.Waiting waiting } ? waiting : null;

    private NodeState StateOf(TaskId id)
    {
        if (_live.ContainsKey(id)) return new NodeState.Running();
        return Log(id).Record switch
        {
            { Open: true } => new NodeState.InputRequired(NodeState.Interrupted),
            { End: NodeEntry.Waiting waiting } => new NodeState.InputRequired(waiting.Why),
            { End: NodeEntry.Failed failed } => new NodeState.Failed(failed.Reason),
            { End: NodeEntry.Stopped, TurnBasis: { } turn } when turn != BasisIn(_workflow, id) => new NodeState.Stale(),
            { History: [.., var latest] } => latest.Basis == BasisIn(_workflow, id) ? new NodeState.Completed() : new NodeState.Stale(),
            _ => new NodeState.Idle(),
        };
    }

    private Basis BasisIn(Workflow workflow, TaskId id)
    {
        var node = workflow.Tasks[id];
        var instruction = node.Blueprint.Work is WorkSpec.Agent
            ? AgentWork.Prompt(new NodeContext(node, ""))
            : string.Join("\n", node.Fields.Select(field => $"{field.Key}: {field.Value}"));
        return new Basis([.. Predecessors(workflow, id).Select(p => new BasisInput(p, Log(p).Record.History.LastOrDefault()?.Id))], instruction,
            node.Execution is { } settings ? new AgentModel(settings.Client, settings.Model, settings.Reasoning) : null);
    }

    /// <summary>
    /// A fresh turn's prompt: the node's rendered template with its predecessors' latest reports as its inputs, which follow
    /// the template under their own heading when it has no place for them. A Merge node's prompt names the
    /// <paramref name="conflict"/> it resolves. An Approval node's prompt is its inputs alone.
    /// </summary>
    private string Prompt(TaskId id, WorktreeStart.Conflict? conflict)
    {
        var node = _workflow.Tasks[id];
        var inputs = Inputs(id);
        if (node.Blueprint.Work is WorkSpec.Merge merge)
        {
            return merge.Template.Render(name => name switch
            {
                "title" => node.Title,
                "inputs" => inputs,
                "conflict" => conflict?.Description ?? "",
                _ => node.Field(name),
            });
        }

        if (node.Blueprint.Work is not WorkSpec.Agent { Template: var template })
        {
            return inputs;
        }

        var prompt = AgentWork.Prompt(new NodeContext(node, inputs));
        return template.Variables.Contains("inputs") || inputs.Length == 0 ? prompt : $"{prompt.TrimEnd()}\n\n## What earlier tasks handed on\n\n{inputs}\n";
    }

    /// <summary>The predecessors' latest reports, each under its node's title.</summary>
    private string Inputs(TaskId id) => string.Join("\n\n", Predecessors(_workflow, id)
        .Select(p => Log(p).Record.History.LastOrDefault() is { } result ? $"### {Title(p)}\n\n{result.Report.TrimEnd()}" : null)
        .OfType<string>());

    private IAgent? AgentOf(TaskDefinition node) => node.Blueprint.Work switch
    {
        WorkSpec.Agent or WorkSpec.Merge => _cli,
        WorkSpec.Person => PersonAgent.Instance,
        _ => null,
    };

    private string Title(TaskId id) => _workflow.Tasks[id] is { Title.Length: > 0 } node ? node.Title : _workflow.Tasks[id].Blueprint.Name;

    private static IEnumerable<TaskId> Predecessors(Workflow workflow, TaskId id) =>
        workflow.Connections.Where(c => c.Value == ConnectionKind.Dependency && c.Key.To == id).Select(c => c.Key.From);

    private IEnumerable<TaskId> Successors(TaskId id) =>
        _workflow.Connections.Where(c => c.Value == ConnectionKind.Dependency && c.Key.From == id).Select(c => c.Key.To);

    private NodeLog Log(TaskId id)
    {
        if (!_logs.TryGetValue(id, out var log))
        {
            _logs[id] = log = new NodeLog(_project, id);
        }

        return log;
    }

    private static string NewId() => Guid.CreateVersion7().ToString("D");

    private ImmutableDictionary<TaskId, NodeState> States() => _workflow.Tasks.Keys.ToImmutableDictionary(id => id, StateOf);

    /// <summary>
    /// Changes state under the lock and queues each node whose state changed in that order. Then one thread at a time
    /// raises what is queued, outside the state lock, so the events keep their order and a handler may call the engine.
    /// </summary>
    private void Change(Action action)
    {
        lock (_gate)
        {
            try
            {
                action();
            }
            finally
            {
                var now = States();
                foreach (var (node, state) in now.Where(pair => _published.GetValueOrDefault(pair.Key) != pair.Value))
                {
                    _events.Enqueue((node, state));
                }

                _published = now;
            }
        }

        lock (_dispatch)
        {
            while (true)
            {
                (TaskId Node, NodeState State) next;
                lock (_gate)
                {
                    if (!_events.TryDequeue(out next)) return;
                }

                NodeChanged?.Invoke(next.Node, next.State);
            }
        }
    }

    private sealed class Live
    {
        public CancellationTokenSource Stop { get; } = new();

        public Halt Halt { get; set; }

        public string? Queued { get; set; }

        public Task Done { get; set; } = Task.CompletedTask;
    }
}
