using System.Collections.Immutable;
using System.Threading.Channels;
using IDevelop.Clients;
using IDevelop.Git;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>
/// Runs one workflow's nodes and routes their results. Every ready node starts at once, where ready means every dependency
/// predecessor is completed and current. A node's completion starts each successor that is ready and has no current
/// result, in a fresh session given the predecessors' latest results. A person's message continues the node's own session.
/// A review's reject and an approval's send back go along the feedback edge: they continue each predecessor's session, and
/// the reviewer or approver runs again once the new results complete. A reviewer that sent its round limit of rejects
/// waits for the person instead of reviewing again. When a node starts a turn, every running node after it stops and goes
/// stale, and it runs again once its inputs are complete. When the person edits a running node's basis, the node stops,
/// goes stale, and waits for their Run. Each node's state is its log replayed, whether a turn runs, and its current basis, so nothing else is stored.
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

    /// <summary>
    /// What a planner may fill and place, which its prompt names when a fresh turn starts, or null for none. The engine
    /// calls it under its lock, so it must not wait for another thread.
    /// </summary>
    public Func<TaskId, PlanningContext?>? Planning { get; set; }

    /// <summary>The handles a planner's session read when it started, which its proposals name, or null before it started.</summary>
    public PlanningHandles? Planned(TaskId node)
    {
        lock (_gate) return Log(node).Record.Planning;
    }

    /// <summary>The node's conversation, which follows its log until it is disposed.</summary>
    public IConversation Conversation(TaskId node)
    {
        lock (_gate) return new NodeConversation(node, Log(node), () => TerminalCommand(node));
    }

    /// <summary>
    /// The folder the node's agent works in: its worktree for a node that edits the project, else the project folder. Null
    /// when the worktree cannot be found, such as outside a Git work tree.
    /// </summary>
    public string? Folder(TaskId node)
    {
        lock (_gate)
        {
            if (_workflow.Tasks.GetValueOrDefault(node) is not { } task || !Edits(task)) return _project;
            try
            {
                return _worktrees.Folder(node);
            }
            catch (GitFailure)
            {
                return null;
            }
        }
    }

    private string? TerminalCommand(TaskId id)
    {
        lock (_gate)
        {
            if (_live.ContainsKey(id) || Log(id).Record is not { Session: { } session, SessionBasis.Model.Client: var client } || Folder(id) is not { } folder
                || Moved(id) is not null)
            {
                return null;
            }

            return Clients.TerminalCommand.For(folder, ClientCatalog.Get(client).Terminal(session), Clients.TerminalCommand.Current);
        }
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

            if (Moved(node) is { } moved) throw new InvalidOperationException(moved);
            Continue(node, text);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Why the node's session cannot go on, or null: a client that finds a session only in the folder where it ran, Pi, and a
    /// session that ran in another folder, because the project folder moved since (ADR 0026).
    /// </summary>
    private string? Moved(TaskId id) =>
        Log(id).Record is { SessionFolder: { } ran, SessionBasis.Model.Client: var client } && ClientCatalog.Get(client).SessionsPerFolder
        && Folder(id) is { } now && !string.Equals(Path.GetFullPath(ran), Path.GetFullPath(now),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? $"{ClientCatalog.Name(client)} can't continue this conversation after the project folder moved, so it needs a new conversation."
            : null;

    /// <summary>
    /// Accepts what a waiting node offers as its result: an Approval node's inputs, a reviewer's inputs at its round limit,
    /// or a Chat node's last reply.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node waits for nothing the person can approve.</exception>
    public Task Approve(TaskId node)
    {
        Change(() =>
        {
            var log = Log(node);
            if (Waiting(node) is not { Why: NodeState.Approval or NodeState.Reply or NodeState.RoundLimit } waiting || log.Record.SessionBasis is not { } basis)
            {
                throw new InvalidOperationException("The node waits for no approval.");
            }

            log.Append(Finish(_workflow.Tasks[node], node, ResultBlock.Prose(waiting.Text), basis, out _));
            if (log.Record.End is NodeEntry.Completed) Route(node);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Commits the person's resolution of the conflict the node waits for, on the conflict's parents, and starts the node's
    /// fresh turn from there.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The node waits for no conflict, cannot run now, rests on a basis that changed since the conflict, or a conflicting
    /// file still holds markers.
    /// </exception>
    public Task MarkResolved(TaskId node)
    {
        Change(() =>
        {
            if (Waiting(node) is not { Why: NodeState.Conflict } || Log(node).Record.TurnBasis is not { } basis)
            {
                throw new InvalidOperationException("The node waits for no conflict.");
            }

            if (Refusal(node) is { } reason) throw new InvalidOperationException(reason);
            if (basis != BasisIn(_workflow, node))
            {
                throw new InvalidOperationException("What the node starts from changed since the conflict. Run it again.");
            }

            if (_worktrees.MarkersRemain(node) is { IsEmpty: false } remaining)
            {
                throw new InvalidOperationException($"Conflict markers remain in {string.Join(", ", remaining)}.");
            }

            Begin(node, basis, () => _worktrees.Commit(node, "Resolve the conflicting merge", InputCommits(node), resolving: true));
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether Merge into my branch is offered: every node is completed and nothing is stale, and an end node's commit holds
    /// work that the checked-out branch does not have yet.
    /// </summary>
    public bool CanMergeIntoBranch
    {
        get
        {
            lock (_gate)
            {
                if (_workflow.Tasks.Count == 0 || !_workflow.Tasks.Keys.All(id => StateOf(id) is NodeState.Completed)) return false;
                try
                {
                    return _worktrees.HasWorkBeyondHead(EndCommits());
                }
                catch (GitFailure)
                {
                    return false;
                }
            }
        }
    }

    /// <summary>The commits of the end nodes, the nodes with no successors.</summary>
    private ImmutableArray<CommitId> EndCommits() => [.. _workflow.Tasks.Keys.Where(id => !Successors(id).Any()).SelectMany(Handed).Distinct()];

    /// <summary>Merges every end node's commit, the commits of the nodes with no successors, into the checked-out branch.</summary>
    /// <exception cref="InvalidOperationException">
    /// A node is not completed or is stale, the branch already holds the end nodes' work, the end nodes conflict with each
    /// other, or the branch moved and the merge conflicts.
    /// </exception>
    public Task MergeIntoBranch()
    {
        lock (_gate)
        {
            if (!CanMergeIntoBranch) throw new InvalidOperationException("Every node must be completed and current, with work the branch does not have yet.");
            _worktrees.MergeIntoBranch(EndCommits());
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends the inputs of an Approval node, or of a reviewer at its round limit, back with the person's note along the
    /// feedback edge. The node runs again once its predecessors complete.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node waits for no approval, or a predecessor runs.</exception>
    public Task SendBack(TaskId node, string note)
    {
        Change(() =>
        {
            if (Waiting(node) is not { } waiting || _workflow.Tasks[node].Blueprint.Work is not WorkSpec.Person && waiting.Why != NodeState.RoundLimit)
            {
                throw new InvalidOperationException("The node waits for no approval.");
            }

            foreach (var predecessor in Predecessors(_workflow, node))
            {
                if (_live.ContainsKey(predecessor)) throw new InvalidOperationException($"{Title(predecessor)} is running.");
            }

            // A send back is never lost: a predecessor without a session must be able to start one with the note.
            foreach (var predecessor in Targets(node).Where(p => Log(p).Record.Session is null))
            {
                if (Refusal(predecessor) is { } reason) throw new InvalidOperationException($"{Title(predecessor)} cannot run. {reason}");
            }

            Log(node).Append(new NodeEntry.PersonMessage(note));
            Feedback(node, note);
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

    /// <summary>
    /// Starts a fresh session of the node, with <paramref name="feedback"/> after its prompt when a feedback edge found no
    /// session to continue. <paramref name="round"/> says that routing starts it for the next round of its feedback loop.
    /// </summary>
    private void StartFresh(TaskId id, string? feedback = null, bool round = false)
    {
        if (Refusal(id) is { } reason) throw new InvalidOperationException(reason);
        var basis = BasisIn(_workflow, id);
        if (Edits(_workflow.Tasks[id]))
        {
            Begin(id, basis, () => _worktrees.Reset(id, InputCommits(id)), feedback, round);
        }
        else
        {
            StartPrompted(id, basis, feedback, round);
        }
    }

    /// <summary>
    /// Starts a fresh turn on the node's prompt. A planner's prompt lists what it may fill and place, and its log records
    /// those handles, which its proposals name.
    /// </summary>
    private void StartPrompted(TaskId id, Basis basis, string? feedback, bool round)
    {
        var planning = _workflow.Tasks[id].Blueprint.Work is WorkSpec.Agent { Proposes: true } ? Planning?.Invoke(id) ?? PlanningContext.None : null;
        Start(id, basis, WithFeedback(Prompt(id, null, planning), feedback), session: null, round: round, planning: planning?.Handles(id.Value));
    }

    private static string WithFeedback(string prompt, string? feedback) =>
        feedback is null ? prompt : $"{prompt.TrimEnd()}\n\n## Feedback on your earlier result\n\n{feedback}\n";

    /// <summary>
    /// Starts an editing node's fresh turn once <paramref name="prepare"/> readied its worktree. A conflict goes to a Merge
    /// node's agent, or waits for the person. A failure fails only this node, so routing goes on to the others.
    /// </summary>
    private void Begin(TaskId id, Basis basis, Func<WorktreeStart> prepare, string? feedback = null, bool round = false)
    {
        var node = _workflow.Tasks[id];
        WorktreeStart start;
        try
        {
            start = prepare();
        }
        catch (Exception e)
        {
            Settle(id, basis, new NodeEntry.Failed(e.Message));
            return;
        }

        switch (start)
        {
            case WorktreeStart.Conflict conflict when node.Blueprint.Work is WorkSpec.Merge:
                Start(id, basis, WithFeedback(Prompt(id, conflict), feedback), session: null, conflict, round);
                break;
            case WorktreeStart.Conflict conflict:
                Settle(id, basis, new NodeEntry.Conflicted(conflict.Description), new NodeEntry.Waiting(NodeState.Conflict, conflict.Description));
                break;
            default:
                StartPrompted(id, basis, feedback, round);
                break;
        }
    }

    /// <summary>Logs a fresh turn that ends as soon as it starts, with <paramref name="entries"/>.</summary>
    private void Settle(TaskId id, Basis basis, params NodeEntry[] entries)
    {
        StopDescendants(id);
        Log(id).Append(new NodeEntry.TurnStarted(basis, null));
        foreach (var entry in entries) Log(id).Append(entry);
    }

    /// <summary>
    /// How a turn that is done ends: with its result, which for an editing node holds its worktree's commit. A Merge node
    /// whose files still hold conflict markers waits for the person. When the commit still lacks some of the predecessors'
    /// commits and merging them conflicts, a Merge node's session goes on with <paramref name="again"/>, the new conflict,
    /// and any other node waits for the person.
    /// </summary>
    private NodeEntry Finish(TaskDefinition node, TaskId id, string report, Basis basis, out WorktreeStart.Conflict? again)
    {
        again = null;
        if (!Edits(node)) return new NodeEntry.Completed(new NodeResult(NewId(), report, null, basis));
        var merge = node.Blueprint.Work is WorkSpec.Merge;
        var resolving = Log(id).Record.Resolving;
        try
        {
            if (merge && resolving && _worktrees.MarkersRemain(id) is { IsEmpty: false } remaining)
            {
                return new NodeEntry.Waiting(NodeState.Conflict, $"Conflict markers remain in {string.Join(", ", remaining)}.");
            }

            switch (_worktrees.Commit(id, node.Title.Length > 0 ? node.Title : node.Blueprint.Name, InputCommits(id), resolving))
            {
                case WorktreeStart.Conflict conflict when merge:
                    again = conflict;
                    return new NodeEntry.Waiting(NodeState.Conflict, conflict.Description);
                case WorktreeStart.Conflict conflict:
                    Log(id).Append(new NodeEntry.Conflicted(conflict.Description));
                    return new NodeEntry.Waiting(NodeState.Conflict, conflict.Description);
                case WorktreeStart.Ready ready:
                    return new NodeEntry.Completed(new NodeResult(NewId(), report, ready.Commit.Hex, basis));
                default:
                    throw new InvalidOperationException();
            }
        }
        catch (Exception e) when (e is GitFailure or IOException or UnauthorizedAccessException)
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

    private void Start(TaskId id, Basis basis, string prompt, string? session, WorktreeStart.Conflict? conflict = null, bool round = false,
        PlanningHandles? planning = null)
    {
        var node = _workflow.Tasks[id];
        var agent = AgentOf(node) ?? throw new InvalidOperationException("This node kind does not run yet.");
        StopDescendants(id);
        var log = Log(id);
        var folder = Edits(node) ? _worktrees.Folder(id) : _project;
        log.Append(new NodeEntry.TurnStarted(basis, session) { Round = round, Planning = planning, Folder = session is null ? folder : null });
        if (conflict is not null) log.Append(new NodeEntry.Conflicted(conflict.Description));
        var live = new Live();
        _live[id] = live;
        live.Done = Task.Run(() => Drive(id, node, agent, log, live, basis, folder, prompt, session));
    }

    private async Task Drive(TaskId id, TaskDefinition node, IAgent agent, NodeLog log, Live live, Basis basis, string folder, string prompt, string? session)
    {
        TurnOutcome outcome;
        try
        {
            outcome = await agent.Turn(node, folder, prompt, session, e => Report(log, live, e), live.Replies.Reader, live.Stop.Token);
        }
        catch (Exception e)
        {
            outcome = new TurnOutcome.Failed(e.Message);
        }

        Change(() =>
        {
            _live.Remove(id);
            if (live.Halt == Halt.Closing) return;
            WorktreeStart.Conflict? again = null;
            log.Append(live.Halt != Halt.None ? new NodeEntry.Stopped() : outcome switch
            {
                TurnOutcome.Done done when node.Blueprint.Work is WorkSpec.Review review => Judge(id, review, done.Report, basis),
                TurnOutcome.Done done => Finish(node, id, done.Report, basis, out again),
                TurnOutcome.Waits waits => new NodeEntry.Waiting(waits.Why, waits.Text),
                TurnOutcome.Failed failed => new NodeEntry.Failed(failed.Reason),
                _ => new NodeEntry.Stopped(),
            });
            var continues = live.Halt is Halt.None or Halt.Message && log.Record.Session is not null && _workflow.Tasks.ContainsKey(id);
            if (log.Record.End is NodeEntry.Feedback feedback && _workflow.Tasks.ContainsKey(id))
            {
                // The findings go back even when the person's message continues the reviewer's session.
                Return(id, feedback.Text);
            }

            if (again is not null && continues)
            {
                // A Merge node's next conflict among its inputs is another turn of its session.
                Start(id, basis, Prompt(id, again), log.Record.Session, again);
                _live[id].Queued = live.Queued;
            }
            else if (live.Queued is { } message && continues)
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

    /// <summary>Logs what the agent said. A question waits for the person, under a key of the engine's own, until it is answered.</summary>
    private void Report(NodeLog log, Live live, AgentEvent e)
    {
        if (e is not AgentEvent.QuestionAsked asked)
        {
            log.Append(new NodeEntry.Agent(e));
            return;
        }

        Change(() =>
        {
            var question = new AgentQuestion(NewId(), asked.Questions);
            live.Asked.Add((question, asked.RequestId));
            log.Append(new NodeEntry.Asked(question));
        });
    }

    /// <summary>
    /// Sends the person's answer to the question of <paramref name="key"/> into the node's running turn. It answers each
    /// question asked once, with its choices, one unless the question takes several, and any words of the person's own.
    /// </summary>
    /// <exception cref="InvalidOperationException">The turn waits for no such question, or the answer does not fit it.</exception>
    public Task Answer(TaskId node, string key, QuestionsReply reply)
    {
        Change(() =>
        {
            var live = _live.GetValueOrDefault(node);
            if (live?.Asked.FindIndex(pending => pending.Question.Key == key) is not (>= 0 and var index))
            {
                throw new InvalidOperationException("The agent no longer waits for this answer.");
            }

            var (question, request) = live.Asked[index];
            string? Words(AskedQuestion item) => reply.Answers.Where(answer => answer.QuestionId == item.Id).ToList() is [var answer]
                && (item.MultiSelect || answer.OptionIds.Length <= 1) && answer.OptionIds.All(id => item.Options.Any(option => option.Id == id))
                && string.Join(", ", item.Options.Where(option => answer.OptionIds.Contains(option.Id)).Select(option => option.Label)
                    .Append(answer.Text?.Trim() ?? "").Where(word => word.Length > 0)) is { Length: > 0 } words ? words : null;
            var words = question.Questions.Select(Words).ToList();
            if (reply.Answers.Length != words.Count || words.Contains(null)) throw new InvalidOperationException("The answer does not fit the question.");
            live.Asked.RemoveAt(index);
            Log(node).Append(new NodeEntry.Answered(key, reply, string.Join("\n", words)));
            live.Replies.Writer.TryWrite(new QuestionReply(request, reply));
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hands a new result on: each successor that is ready and has no current result starts fresh. A successor whose
    /// feedback this result answers in its session runs its next round, or waits for the person at its round limit.
    /// </summary>
    private void Route(TaskId id)
    {
        foreach (var next in Successors(id))
        {
            if (StateOf(next) is NodeState.Running or NodeState.Completed || Refusal(next) is not null) continue;
            var node = _workflow.Tasks[next];
            var round = Log(next).Record.End is NodeEntry.Feedback && Log(id).Record.Continued;
            if (round && Log(next).Record.Rounds >= node.RoundLimit && node.Blueprint.Work is not WorkSpec.Person)
            {
                Settle(next, BasisIn(_workflow, next), new NodeEntry.Waiting(NodeState.RoundLimit, Inputs(next)));
            }
            else
            {
                StartFresh(next, round: round);
            }
        }
    }

    /// <summary>Ends the node's turn by sending <paramref name="text"/> back along the feedback edge.</summary>
    private void Feedback(TaskId id, string text)
    {
        Log(id).Append(new NodeEntry.Feedback(text));
        Return(id, text);
    }

    /// <summary>
    /// Sends <paramref name="text"/> to each of <see cref="Targets"/>: it continues the predecessor's session, or starts a
    /// fresh one when it has none. A predecessor edited since its session started stays stale for the person's Run. Their
    /// new results rerun the node by routing.
    /// </summary>
    private void Return(TaskId id, string text)
    {
        foreach (var predecessor in Targets(id).Where(p => !_live.ContainsKey(p)))
        {
            var record = Log(predecessor).Record;
            if (record.Session is null || Moved(predecessor) is not null)
            {
                if (Refusal(predecessor) is null) StartFresh(predecessor, feedback: text);
            }
            else if (record.SessionBasis == BasisIn(_workflow, predecessor))
            {
                Continue(predecessor, text);
            }
        }
    }

    /// <summary>The predecessors feedback goes to: each one that is not downstream of another, whose new result reruns it.</summary>
    private List<TaskId> Targets(TaskId id)
    {
        var predecessors = Predecessors(_workflow, id).ToList();
        return [.. predecessors.Where(p => !predecessors.Any(other => other != p && Upstream(p).Contains(other)))];
    }

    private HashSet<TaskId> Upstream(TaskId id)
    {
        var seen = new HashSet<TaskId>();
        var queue = new Queue<TaskId>(Predecessors(_workflow, id));
        while (queue.TryDequeue(out var next))
        {
            if (seen.Add(next)) foreach (var before in Predecessors(_workflow, next)) queue.Enqueue(before);
        }

        return seen;
    }

    /// <summary>
    /// How a reviewer's turn ends: an approval completes it with its report, a reject sends its findings back in its fix
    /// template, and a verdict that cannot be read waits for the person.
    /// </summary>
    private NodeEntry Judge(TaskId id, WorkSpec.Review review, string text, Basis basis) => Verdict.Read(text) switch
    {
        ({ Approves: true }, _) => new NodeEntry.Completed(new NodeResult(NewId(), text, null, basis)),
        ({ } verdict, _) => new NodeEntry.Feedback(Render(id, review.Fix, Findings(verdict))),
        (_, var problem) => new NodeEntry.Waiting($"iDevelop could not read the verdict. {problem}", text),
    };

    /// <summary>Each finding under a heading with its id, severity, and location, then its text and the change that would settle it.</summary>
    private static string Findings(Verdict verdict) => string.Join("\n\n", verdict.Findings.Select(finding =>
    {
        var detail = string.Join(", ", new[] { finding.Severity, finding.Location }.OfType<string>());
        return $"### Finding {finding.Id}{(detail.Length > 0 ? $" ({detail})" : "")}\n\n{finding.Text}\n\nThe change that would settle it: {finding.Change}";
    }));

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
        if (node.Blueprint.Work is WorkSpec.Review && node.Conversation == ConversationMode.Chat)
        {
            return "A review ends each turn with its verdict, so it cannot chat. Choose Autonomous or May ask.";
        }

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
        if (_live.TryGetValue(id, out var live))
        {
            return live.Asked is [var (question, _), ..] ? new NodeState.InputRequired(question.Text) { Question = question } : new NodeState.Running();
        }

        return Log(id).Record switch
        {
            { Open: true } => new NodeState.InputRequired(NodeState.Interrupted),
            { End: NodeEntry.Waiting waiting } => new NodeState.InputRequired(waiting.Why),
            { End: NodeEntry.Failed failed } => new NodeState.Failed(failed.Reason),
            { End: NodeEntry.Feedback } => new NodeState.Idle(),
            { End: NodeEntry.Stopped, TurnBasis: { } turn } when turn != BasisIn(_workflow, id) => new NodeState.Stale(),
            { History: [.., var latest] } => latest.Basis == BasisIn(_workflow, id) ? new NodeState.Completed() : new NodeState.Stale(),
            _ => new NodeState.Idle(),
        };
    }

    private Basis BasisIn(Workflow workflow, TaskId id)
    {
        var node = workflow.Tasks[id];
        var instruction = node.Blueprint.Work is WorkSpec.Agent
            ? AgentPrompt.For(node, "")
            : string.Join("\n", node.Fields.Select(field => $"{field.Key}: {field.Value}"));
        return new Basis([.. Predecessors(workflow, id).Select(p => new BasisInput(p, Log(p).Record.History.LastOrDefault()?.Id))], instruction,
            node.Execution is { } settings ? new AgentModel(settings.Client, settings.Model, settings.Reasoning) : null);
    }

    /// <summary>
    /// A fresh turn's prompt: the node's rendered template with its predecessors' latest reports as its inputs, which follow
    /// the template under their own heading when it has no place for them. A Merge node's prompt names the
    /// <paramref name="conflict"/> it resolves. An Approval node's prompt is its inputs alone.
    /// </summary>
    private string Prompt(TaskId id, WorktreeStart.Conflict? conflict, PlanningContext? planning = null)
    {
        var node = _workflow.Tasks[id];
        var inputs = Inputs(id);
        if (node.Blueprint.Work is WorkSpec.Merge merge)
        {
            return merge.Template.Render(name => name switch
            {
                "title" => node.Title,
                "inputs" => inputs,
                "conflict" => conflict?.Description ?? "The work handed on merged without a conflict. Check that the merge keeps what each side meant.\n",
                _ => node.Field(name),
            });
        }

        if (node.Blueprint.Work is WorkSpec.Review review)
        {
            return $"{Render(id, review.Reviewer, "").TrimEnd()}\n\n{BuiltInBlueprints.VerdictFormat}\n";
        }

        if (node.Blueprint.Work is not WorkSpec.Agent { Template: var template })
        {
            return inputs;
        }

        var prompt = AgentPrompt.For(node, inputs, planning);
        return template.Variables.Contains("inputs") || inputs.Length == 0 ? prompt : $"{prompt.TrimEnd()}\n\n## What earlier tasks handed on\n\n{inputs}\n";
    }

    /// <summary>The predecessors' latest reports, each under its node's title.</summary>
    private string Inputs(TaskId id) => string.Join("\n\n", Predecessors(_workflow, id)
        .Select(p => Log(p).Record.History.LastOrDefault() is { } result ? $"### {Title(p)}\n\n{result.Report.TrimEnd()}" : null)
        .OfType<string>());

    /// <summary>
    /// A review template with its subject's ticket, latest report, and change, each predecessor's in turn, and
    /// <paramref name="findings"/>.
    /// </summary>
    private string Render(TaskId id, PromptTemplate template, string findings)
    {
        var node = _workflow.Tasks[id];
        string Each(Func<TaskId, string> part) => string.Join("\n\n", Predecessors(_workflow, id).Select(part));
        return template.Render(name => name switch
        {
            "title" => node.Title,
            "inputs" => Inputs(id),
            "ticket" => Each(subject => BasisIn(_workflow, subject).Instruction.Trim()),
            "report" => Each(subject => Log(subject).Record.History.LastOrDefault()?.Report.Trim() ?? ""),
            "change" => Each(Diff),
            "findings" => findings,
            _ => node.Field(name),
        });
    }

    /// <summary>The change of the node's latest commit, from the merge of the commits it started from.</summary>
    private string Diff(TaskId id)
    {
        if (Log(id).Record.History.LastOrDefault() is not { Commit: { } commit }) return "It changed no file.";
        try
        {
            return $"```diff\n{_worktrees.Change(InputCommits(id), new CommitId(commit))}```";
        }
        catch (GitFailure e)
        {
            return $"iDevelop could not read the change. {e.Message}";
        }
    }

    private IAgent? AgentOf(TaskDefinition node) => node.Blueprint.Work switch
    {
        WorkSpec.Agent or WorkSpec.Review or WorkSpec.Merge => _cli,
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

        /// <summary>The questions the agent waits on, oldest first, each with the client's own request id.</summary>
        public List<(AgentQuestion Question, string RequestId)> Asked { get; } = [];

        public Channel<QuestionReply> Replies { get; } = Channel.CreateUnbounded<QuestionReply>();

        public Task Done { get; set; } = Task.CompletedTask;
    }
}
