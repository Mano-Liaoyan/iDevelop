using System.Collections.Immutable;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>A task that a proposal adds or fills, which the person can untick before accepting.</summary>
/// <param name="title">The task's title once accepted.</param>
/// <param name="label">What accepting does, such as Add Implement "Wire export", which names the row and leads its tooltip.</param>
/// <param name="slot">For a fill, the title of the task it fills, else null.</param>
/// <param name="hasText">A fill of a task the person has written in, which starts unticked.</param>
/// <param name="agent">For a task that takes an agent, the agent it takes once accepted, else null.</param>
public sealed class ProposalItemViewModel(
    ProposalViewModel proposal, TaskId id, string title, string label, string preview, bool isChosen, NodeKind kind, string? slot = null, bool hasText = false,
    ProposalAgentViewModel? agent = null) : ObservableObject
{
    private bool _isChosen = isChosen;
    private bool _hasStarted;

    internal TaskId Id { get; } = id;

    public NodeKind Kind { get; } = kind;

    public string Title { get; } = title;

    /// <summary>False for a fill of a task that has started, which no proposal changes.</summary>
    public bool CanChoose => !_hasStarted;

    public string Label => _hasStarted ? $"{label}. It has started, so it stays as it is" : label;

    /// <summary>A fill's line under its title, such as "Fills Backend · Has your text". Null for a task the proposal adds.</summary>
    public string? Note => slot is null ? null
        : _hasStarted ? $"Fills {slot} · Started"
        : hasText ? $"Fills {slot} · Has your text"
        : $"Fills {slot}";

    public string Preview { get; } = preview;

    /// <summary>The agent the task takes once accepted, or null for a task that takes no agent.</summary>
    public ProposalAgentViewModel? Agent { get; } = agent;

    public bool HasAgent => Agent is not null;

    /// <summary>The label, then the first value the proposal gives the task.</summary>
    public string Tip => string.IsNullOrEmpty(Preview) ? Label : $"{Label}\n{Preview}";

    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (SetProperty(ref _isChosen, value))
            {
                proposal.OnChoiceChanged();
            }
        }
    }

    /// <summary>Unticks the fill and turns its box off once its task has started.</summary>
    internal void ShowStarted(bool hasStarted)
    {
        if (!SetProperty(ref _hasStarted, hasStarted, nameof(CanChoose)))
        {
            return;
        }

        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(Tip));
        if (hasStarted && _isChosen)
        {
            _isChosen = false;
            OnPropertyChanged(nameof(IsChosen));
        }
    }
}

/// <summary>
/// Where a proposal of a run's planner came from (#84): the run, the revision the person sees it against, and the planner's
/// turn that waits on it.
/// </summary>
/// <param name="Revision">The run's revision when the proposal was read, which an amendment names as the one the person saw.</param>
/// <param name="Turn">The planner's latest turn, which Mark done names.</param>
/// <param name="Waits">The planner waits on this proposal, so accepting or dismissing it lets the tasks after it go on.</param>
internal sealed record RunSource(RunAddress Address, RevisionId Revision, TurnKey Turn, bool Waits);

/// <summary>
/// A planner's latest proposal in its inspector: what it adds, fills, and connects, why accepting it would fail, and
/// Accept and Dismiss. Its chosen tasks show as ghost cards until it is accepted or dismissed. A run's planner's proposal
/// amends the run while the run goes on in this window, and otherwise edits only the workflow for the next run (#84).
/// </summary>
public sealed class ProposalViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private readonly string? _readProblem;
    private readonly RelayCommand _accept;
    private readonly RelayCommand _dismiss;
    private RunSource? _run;
    private RevisionId? _latest;
    // The run refused an acceptance as stale before a reread brought the newer revision, so the next one takes it.
    private bool _stale;
    private bool _amending;
    private string? _problem;
    private IReadOnlyList<string> _connections = [];

    /// <param name="attempt">The attempt the proposal was read from.</param>
    /// <param name="run">For a run's planner, the run the proposal came from.</param>
    internal ProposalViewModel(WorkflowCanvasViewModel canvas, ProposalRead read, AttemptId attempt, RunSource? run = null)
    {
        _canvas = canvas;
        _run = run;
        Identity = IdentityOf(read, attempt);
        var workflow = canvas.Workflow;
        switch (read)
        {
            case ProposalRead.Ready ready:
                Proposal = ready.Proposal;
                // A slot the person has written in since starts unticked, so Accept never overwrites it unasked. A node an
                // earlier Accept placed is not offered again.
                Items = [.. Proposal.Fills.Select(FillItem),
                    .. Proposal.Nodes.Where(node => !workflow.Tasks.ContainsKey(node.Id))
                        .Select(node => new ProposalItemViewModel(
                            this, node.Id, node.Title, $"Add {node.Blueprint.Name} \"{node.Title}\"", Preview(node.Blueprint, node.Fields), true, _canvas.KindOf(node.Blueprint),
                            agent: node.Blueprint.Work is WorkSpec.Person ? null : ProposalAgentViewModel.ForNode(this, node)))];
                break;
            case ProposalRead.Problem problem:
                _readProblem = problem.Text;
                Items = [];
                break;
            default:
                throw new ArgumentException("Only a proposal or its problem has a view.", nameof(read));
        }

        _accept = new RelayCommand(Accept, () => !_amending && Proposal is not null && _problem is null && Items.Any(item => item.IsChosen));
        _dismiss = new RelayCommand(Dismiss, () => !_amending);
        Refresh();
    }

    /// <summary>
    /// Accept amends the run: the proposal is a run's planner's, and this window controls the run, which is neither stopping
    /// nor ended.
    /// </summary>
    public bool IntoRun => _run is { } run && _canvas.Run is { IsControlled: true, View.Phase: RunPhase.Approved } shown && shown.Address == run.Address;

    /// <summary>"Accept", or "Accept for Next Run" for a run's planner whose run Accept no longer amends.</summary>
    public string AcceptLabel => _run is not null && !IntoRun ? "Accept for Next Run" : "Accept";

    public string AcceptTip => IntoRun ? "Adds and fills the chosen tasks in the run, and the workflow follows"
        : _run is not null ? "Adds and fills the chosen tasks in the workflow only, for its next run"
        : "Adds and fills the chosen tasks in one change";

    public string DismissTip => IntoRun && _run!.Waits ? "Hides the proposal and lets the tasks after the planner go on"
        : "Hides the proposal until the planner proposes again";

    /// <summary>Why Accept changes only the workflow for the next run, or null while it amends the run or the proposal is no run's.</summary>
    public string? RunNote => _run is null || IntoRun ? null
        : _canvas.Run is { } shown && shown.Address == _run.Address && !shown.IsControlled
            ? "Another window controls this run, so Accept changes only the workflow for its next run."
        : _canvas.Run is { View.Phase: RunPhase.StopRequested } or { View.Status: RunStatus.Stopped } ? "The run has stopped, so Accept changes only the workflow for its next run."
        : "The run has ended, so Accept changes only the workflow for its next run.";

    /// <summary>The proposal came from a run's planner that waits on it.</summary>
    internal bool PlannerWaits => _run is { Waits: true };

    /// <summary>The proposal came from a run's planner.</summary>
    internal bool FromRun => _run is not null;

    /// <summary>
    /// The proposal reread from the run, or the run's phase changed. Accept keeps naming the revision the person first saw
    /// it against, so a run that another amendment moved refuses it, and the view then takes the newer one.
    /// </summary>
    internal void ShowRun(RunSource? run)
    {
        if (run is not null)
        {
            _latest = run.Revision;
            _run = _run is { } shown && !_stale ? run with { Revision = shown.Revision } : run;
            _stale = false;
        }

        OnPropertyChanged(nameof(IntoRun));
        OnPropertyChanged(nameof(AcceptLabel));
        OnPropertyChanged(nameof(AcceptTip));
        OnPropertyChanged(nameof(DismissTip));
        OnPropertyChanged(nameof(RunNote));
        Refresh();
        _dismiss.NotifyCanExecuteChanged();
    }

    public IReadOnlyList<ProposalItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// A task has no agent of its own, neither the person's nor a planner's choice that this machine runs, nor its type's or,
    /// for a fill, its task's, so <see cref="UsePlannerAgent"/> decides its agent.
    /// </summary>
    public bool UsesFallback => Items.Any(item => item.Agent is { NeedsFallback: true });

    /// <summary>
    /// Whether each new task without an agent of its own, whose type has none, takes the planner's agent. On for a planner
    /// that Generate placed in this window, and off for any other, so such a task takes its type's default agent unless
    /// the person opts in.
    /// </summary>
    public bool UsePlannerAgent
    {
        get => Proposal is { } proposal && _canvas.UsesPlannerAgent(proposal.Planner);
        set
        {
            if (Proposal is { } proposal && value != UsePlannerAgent)
            {
                _canvas.ChoosePlannerAgent(proposal.Planner, value);
                OnPropertyChanged();
                OnAgentChanged();
            }
        }
    }

    /// <summary>
    /// Which clients this window was still checking when it started the planner, so the planner did not consider them,
    /// such as "Pi was still being checked when the planner started, so the planner didn't consider it." Null for none.
    /// </summary>
    public string? CheckingNote => Proposal is { } proposal && _canvas.CheckingAtStart(proposal.Planner) is { IsEmpty: false } checking
        ? checking.Length == 1
            ? $"{IDevelop.Execution.Clients.Name(checking[0])} was still being checked when the planner started, so the planner didn't consider it."
            : $"{Names(checking)} were still being checked when the planner started, so the planner didn't consider them."
        : null;

    /// <summary>Each connection that Accept adds now, as its two tasks' titles, as of the last <see cref="Refresh"/>.</summary>
    public IReadOnlyList<string> Connections => _connections;

    /// <summary>"1 connection" or "3 connections", which the inspector shows in place of <see cref="Connections"/> until asked, or null for none.</summary>
    public string? ConnectionCount => _connections.Count switch
    {
        0 => null,
        1 => "1 connection",
        var count => $"{count} connections",
    };

    /// <summary>Why the proposal cannot be read, or why accepting the chosen tasks would be rejected.</summary>
    public string? Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    public ICommand AcceptCommand => _accept;

    /// <summary>Hides the proposal until the planner proposes again, and lets a run's planner that waits on it go on.</summary>
    public ICommand DismissCommand => _dismiss;

    internal Proposal? Proposal { get; }

    /// <summary>The planner's agent while the box is ticked, which a new task without an agent of its own takes, else null.</summary>
    internal ExecutionSettings? Fallback => UsePlannerAgent ? _canvas.Workflow.Tasks.GetValueOrDefault(Proposal!.Planner)?.Execution : null;

    /// <summary>What each client offers on this machine now.</summary>
    internal IReadOnlyDictionary<ClientId, ClientStatus> ClientStatuses => _canvas.Clients.Current;

    internal ClientStatus Status(ClientId client) => _canvas.Clients.Current[client];

    /// <summary>The proposal's attempt and turn, or the problem and its attempt, which a new turn replaces.</summary>
    internal object Identity { get; }

    internal static object IdentityOf(ProposalRead read, AttemptId attempt) => read switch
    {
        ProposalRead.Ready ready => (ready.Proposal.Attempt, ready.Proposal.Turn),
        ProposalRead.Problem problem => (attempt, problem.Text),
        _ => throw new ArgumentException("Only a proposal or its problem has an identity.", nameof(read)),
    };

    /// <summary>Accepting all of it would change nothing, as after an accept: its nodes exist and its fills are in place.</summary>
    internal bool IsSettled(Workflow workflow) => Proposal is { } proposal &&
        workflow.Apply(proposal.Accept(workflow, proposal.Items.ToHashSet(), HasStarted)) is EditResult.Applied applied && ReferenceEquals(applied.Workflow, workflow);

    /// <summary>
    /// The chosen tasks as ghost cards, a new task where accepting places it and a fill over its empty card, after the
    /// connections accepting adds as ghost wires, so the cards draw over the wires.
    /// </summary>
    internal IEnumerable<Ghost> Ghosts(Workflow workflow)
    {
        if (Proposal is not { } proposal)
        {
            return [];
        }

        var chosen = Chosen();
        var layout = proposal.Layout(workflow);
        // A card the workflow holds shows where the canvas shows it, and a new one moves as the planner shows, wider while
        // it is expanded, so the proposal keeps its place beside the planner (ADR 0024).
        var shown = _canvas.Nodes.ToDictionary(node => node.Id);
        var shift = shown.GetValueOrDefault(proposal.Planner) is { } planner
            ? planner.ViewOffset + new Vector(planner.CardSize.Width - WorkflowCanvasViewModel.TaskCardWidth, 0)
            : default;
        Point Place(TaskId id, CanvasPoint saved) => shown.GetValueOrDefault(id) is { } node ? node.DisplayLocation : WorkflowCanvasViewModel.ToPoint(saved) + shift;
        Point Output(TaskId id, CanvasPoint saved) => shown.GetValueOrDefault(id) is { } node
            ? node.DisplayLocation + new Vector(node.CardSize.Width - WorkflowCanvasViewModel.InputPortCenter.X, WorkflowCanvasViewModel.OutputPortCenter.Y)
            : Place(id, saved) + (Vector)WorkflowCanvasViewModel.OutputPortCenter;
        var cards = new List<Ghost>();
        var agents = Agents().ToDictionary(agent => agent.Id);
        foreach (var fill in proposal.Fills.Where(fill => chosen.Contains(fill.Slot) && workflow.Positions.ContainsKey(fill.Slot)))
        {
            var slot = workflow.Tasks[fill.Slot];
            // A fill shows the model of the agent it takes, its task's own or the planner's choice, on the card's second line.
            var agent = agents.GetValueOrDefault(fill.Slot);
            cards.Add(new GhostCardViewModel(
                Place(fill.Slot, workflow.Positions[fill.Slot]), "Fills", fill.Title ?? slot.Title,
                Preview(slot.Blueprint, fill.Fields), _canvas.KindOf(slot.Blueprint))
            {
                Detail = agent?.Model,
                Note = agent?.GhostNote,
            });
        }

        var added = proposal.Nodes.Where(node => chosen.Contains(node.Id)).ToDictionary(node => node.Id);
        foreach (var node in added.Values)
        {
            // A new task shows the agent it takes on the lines its card will, or its type while it has none.
            var agent = agents.GetValueOrDefault(node.Id);
            var hasAgent = agent?.Settings is not null;
            cards.Add(new GhostCardViewModel(
                Place(node.Id, layout[node.Id]), hasAgent ? agent!.Client : $"New {node.Blueprint.Name}", node.Title,
                Preview(node.Blueprint, node.Fields), _canvas.KindOf(node.Blueprint))
            {
                Detail = hasAgent ? agent!.Model : null,
                Note = agent?.GhostNote,
            });
        }

        CanvasPoint? At(TaskId id) => layout.TryGetValue(id, out var position) ? position : workflow.Positions.TryGetValue(id, out position) ? position : null;
        Blueprint? BlueprintOf(TaskId id) => added.TryGetValue(id, out var node) ? node.Blueprint : workflow.Tasks.GetValueOrDefault(id)?.Blueprint;
        var wires = AcceptEdit(workflow, chosen).Edits.OfType<WorkflowEdit.Connect>()
            .Where(connect => At(connect.Key.From) is not null && At(connect.Key.To) is not null && BlueprintOf(connect.Key.From) is not null)
            .Select(connect => GhostWireViewModel.Between(
                Output(connect.Key.From, At(connect.Key.From)!.Value),
                Place(connect.Key.To, At(connect.Key.To)!.Value) + (Vector)WorkflowCanvasViewModel.InputPortCenter,
                _canvas.KindOf(BlueprintOf(connect.Key.From)!),
                connect.Kind));
        return [.. wires, .. cards];
    }

    /// <summary>Checks the chosen tasks against the workflow and the runs as they are now.</summary>
    internal void Refresh()
    {
        foreach (var fill in Items.Take(Proposal?.Fills.Length ?? 0))
        {
            fill.ShowStarted(HasStarted(fill.Id));
        }

        var edit = Proposal is null ? null : AcceptEdit(_canvas.Workflow, Chosen());
        Problem = _readProblem ?? (edit is not null && _canvas.Workflow.Apply(edit) is EditResult.Rejected rejected
            ? $"Accept would be refused. {RejectionText.Describe(rejected.Reason, Title)}"
            : null);
        _connections = edit is null ? [] : [.. edit.Edits.OfType<WorkflowEdit.Connect>().Select(connect =>
            $"{Title(connect.Key.From)} → {Title(connect.Key.To)}{(connect.Kind == ConnectionKind.Context ? " (context)" : "")}")];
        OnPropertyChanged(nameof(Connections));
        OnPropertyChanged(nameof(ConnectionCount));
        // The planner's own agent, which a task without one takes, may have changed with the workflow.
        foreach (var agent in Agents())
        {
            agent.ShowSummary();
        }

        OnPropertyChanged(nameof(UsesFallback));
        _accept.NotifyCanExecuteChanged();
    }

    internal void OnChoiceChanged()
    {
        Refresh();
        _canvas.ShowGhosts();
    }

    /// <summary>Opens the pickers of one task's agent, closing any other's, or closes them when they are open.</summary>
    internal void Edit(ProposalAgentViewModel agent)
    {
        var open = !agent.IsEditing;
        foreach (var each in Agents())
        {
            each.IsEditing = open && ReferenceEquals(each, agent);
        }
    }

    /// <summary>A task's agent, the box, or what the clients offer changed, so each task's agent and its ghost show again.</summary>
    internal void OnAgentChanged()
    {
        foreach (var agent in Agents())
        {
            agent.Show();
        }

        Refresh();
        _canvas.ShowGhosts();
    }

    /// <summary>Checks each planner's choice against what the clients offer now.</summary>
    internal void OnClientsChanged()
    {
        foreach (var agent in Agents())
        {
            agent.Recheck();
        }

        OnAgentChanged();
    }

    private IEnumerable<ProposalAgentViewModel> Agents() => Items.Select(item => item.Agent).OfType<ProposalAgentViewModel>();

    private HashSet<TaskId> Chosen() => [.. Items.Where(item => item.IsChosen).Select(item => item.Id)];

    /// <summary>
    /// Whether a fill's task has started, so it stays as it is: in the run that Accept amends, once the run has started or
    /// readied it, and otherwise once it has an attempt of its own.
    /// </summary>
    private bool HasStarted(TaskId task) => IntoRun
        ? _canvas.Run!.View.Tasks.GetValueOrDefault(task) is { } view && (view.Attempt is not null || view.State is not (TaskState.Pending or TaskState.Unrequested))
        : _canvas.HasStarted(task);

    private void Accept()
    {
        if (IntoRun)
        {
            _ = AcceptIntoRunAsync(finish: false);
            return;
        }

        var workflow = _canvas.Workflow;
        var chosen = Chosen();
        if (_canvas.Edit(AcceptEdit(workflow, chosen), Title) is EditResult.Applied)
        {
            var added = Proposal!.Nodes.Count(node => chosen.Contains(node.Id));
            var filled = Proposal.Fills.Count(fill => chosen.Contains(fill.Slot));
            _canvas.CloseProposal(this);
            _canvas.Notice(AcceptedNotice(added, filled));
        }
    }

    /// <summary>
    /// Amends the run with the chosen tasks against the revision the person saw, and the document follows as one edit that
    /// Undo can reverse (#84). Then a planner that waits on its proposal, or one the person finishes, is marked done.
    /// </summary>
    internal async Task AcceptIntoRunAsync(bool finish)
    {
        if (_canvas.Run is not { } run || _run is not { } source || Proposal is not { } proposal)
        {
            return;
        }

        var chosen = Chosen();
        var agents = Agents().ToArray();
        var amendment = new RunAmendment(source.Revision, proposal, [.. proposal.Items.Where(chosen.Contains)], Fallback, new OperationId(Guid.NewGuid()))
        {
            Planned = agents.Where(agent => agent.Planned is not null).ToImmutableDictionary(agent => agent.Id, agent => agent.Planned!),
            Changed = agents.Where(agent => agent.Changed is not null).ToImmutableDictionary(agent => agent.Id, agent => agent.Changed!),
        };
        var (coordinator, address) = (run.Coordinator, run.Address);
        SetAmending(true);
        try
        {
            var outcome = await WorkflowRunViewModel.Retrying(() => coordinator.Amend(address, amendment),
                result => result is RunCommand.Refused refused && WorkflowRunText.Transient(refused.Reason));
            // The proposal's next reread of the run brings the newer revision, if it has not yet.
            if (outcome is RunCommand.Refused { Reason.Problem: RunProblem.RevisionConflict })
            {
                if (_latest is { } latest && latest != source.Revision)
                {
                    _run = source with { Revision = latest };
                    Refresh();
                }
                else
                {
                    _stale = true;
                }

                _canvas.Notice("The run changed since this proposal showed. Check it again, then accept it.");
                return;
            }

            if (outcome is not RunCommand.Accepted)
            {
                _canvas.Notice(outcome switch
                {
                    RunCommand.Unavailable unavailable => unavailable.Message,
                    RunCommand.Refused refused => $"The run did not take the proposal. {WorkflowRunText.Problem(refused.Reason)}",
                    _ => null,
                });
                return;
            }

            var followed = await _canvas.FollowRunAsync(coordinator);
            var notice = AcceptedNotice(proposal.Nodes.Count(node => chosen.Contains(node.Id)), proposal.Fills.Count(fill => chosen.Contains(fill.Slot)));
            if (!followed)
            {
                notice = $"The run took the proposal. {notice[..^1]} there, but the workflow has other changes, so it stays as it is.";
            }

            _canvas.CloseProposal(this);
            _canvas.Notice(finish || source.Waits ? await FinishAsync(coordinator, address, proposal.Planner, source.Turn) ?? notice : notice);
        }
        finally
        {
            SetAmending(false);
        }
    }

    /// <summary>Hides the proposal, and marks a run's planner done that waits on it, so the tasks after it go on.</summary>
    private async void Dismiss()
    {
        _canvas.CloseProposal(this);
        if (IntoRun && _run is { Waits: true } source && Proposal is { } proposal && _canvas.Run is { } run)
        {
            SetAmending(true);
            try
            {
                _canvas.Notice(await FinishAsync(run.Coordinator, run.Address, proposal.Planner, source.Turn));
            }
            finally
            {
                SetAmending(false);
            }
        }
    }

    /// <summary>Marks the run's planner done, and says why when the run refuses, else null.</summary>
    private static async Task<string?> FinishAsync(WorkflowRunCoordinator coordinator, RunAddress address, TaskId planner, TurnKey turn)
    {
        var done = await WorkflowRunViewModel.Retrying(() => coordinator.MarkDone(address, planner, turn),
            result => result.Outcome == CommandOutcome.Refused && result.Detail == WorkflowRunCoordinator.BusyMessage);
        return done.Outcome == CommandOutcome.Applied ? null : done.Detail;
    }

    private void SetAmending(bool amending)
    {
        _amending = amending;
        _accept.NotifyCanExecuteChanged();
        _dismiss.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The one edit that accepts the chosen tasks, each with the person's choice, else its own agent, else the planner's
    /// usable choice, else the planner's agent while the box is ticked, as each row shows.
    /// </summary>
    private WorkflowEdit.Batch AcceptEdit(Workflow workflow, IReadOnlySet<TaskId> chosen) => Proposal!.Accept(
        workflow, chosen, HasStarted, UsePlannerAgent ? workflow.Tasks.GetValueOrDefault(Proposal.Planner)?.Execution : null,
        Agents().Where(agent => agent.Planned is not null).ToDictionary(agent => agent.Id, agent => agent.Planned!),
        Agents().Where(agent => agent.Changed is not null).ToDictionary(agent => agent.Id, agent => agent.Changed!));

    /// <summary>A fill whose task is gone reads as Implement.</summary>
    private ProposalItemViewModel FillItem(ProposedFill fill)
    {
        var task = _canvas.Workflow.Tasks.GetValueOrDefault(fill.Slot);
        var slot = task is not null && !string.IsNullOrWhiteSpace(task.Title) ? task.Title.Trim() : null;
        var empty = task is not null && PlanningContext.IsEmpty(task);
        var hasText = task is not null && !empty;
        var named = slot is null ? "an untitled task" : $"\"{slot}\"";
        var label = fill.Title is { } title && title != task?.Title.Trim() ? $"Fill {named} as \"{title}\"" : $"Fill {named}";
        var shown = fill.Title ?? slot ?? "Untitled task";
        return new ProposalItemViewModel(
            this, fill.Slot, shown, hasText ? $"{label}. It has text of yours now" : label,
            Preview(task?.Blueprint, fill.Fields), empty, _canvas.KindOf(task?.Blueprint ?? BuiltInBlueprints.Implement), slot ?? "an untitled task",
            hasText,
            task is null || task.Blueprint.Work is WorkSpec.Person ? null
                : ProposalAgentViewModel.ForFill(this, fill, shown, task.Blueprint, () => _canvas.Workflow.Tasks.GetValueOrDefault(fill.Slot)?.Execution));
    }

    /// <summary>A title the proposal gives, else the workflow's, for a message about a task.</summary>
    private string Title(TaskId id) =>
        Proposal?.TitleOf(id) ?? (_canvas.Workflow.Tasks.TryGetValue(id, out var task) ? task.Title : "a removed task");

    /// <summary>The first value the proposal gives, in the blueprint's field order, as a card previews its first field.</summary>
    private static string Preview(Blueprint? blueprint, IReadOnlyDictionary<string, string> fields) =>
        (blueprint?.Fields.Select(field => field.Key) ?? fields.Keys)
            .Select(key => fields.GetValueOrDefault(key))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    /// <summary>"Added 3 tasks.", "Filled 1 task.", or both, leaving out a count of none.</summary>
    internal static string AcceptedNotice(int added, int filled) => (added, filled) switch
    {
        (> 0, > 0) => $"Added {Count(added, "task")} and filled {Count(filled, "task")}.",
        (> 0, _) => $"Added {Count(added, "task")}.",
        _ => $"Filled {Count(filled, "task")}.",
    };

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>"Pi and Antigravity CLI", or "Claude Code, Pi, and Antigravity CLI".</summary>
    private static string Names(IReadOnlyList<ClientId> clients)
    {
        var names = clients.Select(IDevelop.Execution.Clients.Name).ToArray();
        return names.Length < 3 ? string.Join(" and ", names) : $"{string.Join(", ", names[..^1])}, and {names[^1]}";
    }
}
