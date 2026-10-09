using System.Windows.Input;
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
/// <param name="agent">For a new task that takes an agent, the agent it takes once accepted, else null.</param>
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

    /// <summary>The agent a new task takes once accepted, or null for a fill and for a task that takes no agent.</summary>
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
/// A planner's latest proposal in its inspector: what it adds, fills, and connects, why accepting it would fail, and
/// Accept and Dismiss. Its chosen tasks show as ghost cards until it is accepted or dismissed.
/// </summary>
public sealed class ProposalViewModel : ObservableObject
{
    private readonly WorkflowCanvasViewModel _canvas;
    private readonly string? _readProblem;
    private readonly RelayCommand _accept;
    private string? _problem;
    private IReadOnlyList<string> _connections = [];

    /// <param name="attempt">The attempt the proposal was read from.</param>
    internal ProposalViewModel(WorkflowCanvasViewModel canvas, ProposalRead read, AttemptId attempt)
    {
        _canvas = canvas;
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
                            agent: node.Blueprint.Work is WorkSpec.Person ? null : new ProposalAgentViewModel(this, node)))];
                break;
            case ProposalRead.Problem problem:
                _readProblem = problem.Text;
                Items = [];
                break;
            default:
                throw new ArgumentException("Only a proposal or its problem has a view.", nameof(read));
        }

        _accept = new RelayCommand(Accept, () => Proposal is not null && _problem is null && Items.Any(item => item.IsChosen));
        DismissCommand = new RelayCommand(() => _canvas.CloseProposal(this));
        Refresh();
    }

    public IReadOnlyList<ProposalItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// A new task has no agent of its own, neither the person's nor a planner's choice that this machine runs, and its type
    /// has none, so <see cref="UsePlannerAgent"/> decides its agent. A fill keeps its task's agent.
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

    /// <summary>Hides the proposal until the planner proposes again.</summary>
    public ICommand DismissCommand { get; }

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
        workflow.Apply(proposal.Accept(workflow, proposal.Items.ToHashSet(), _canvas.HasStarted)) is EditResult.Applied applied && ReferenceEquals(applied.Workflow, workflow);

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
        var cards = new List<Ghost>();
        foreach (var fill in proposal.Fills.Where(fill => chosen.Contains(fill.Slot) && workflow.Positions.ContainsKey(fill.Slot)))
        {
            var slot = workflow.Tasks[fill.Slot];
            cards.Add(new GhostCardViewModel(
                WorkflowCanvasViewModel.ToPoint(workflow.Positions[fill.Slot]), "Fills", fill.Title ?? slot.Title,
                Preview(slot.Blueprint, fill.Fields), _canvas.KindOf(slot.Blueprint))
            {
                // A fill keeps its task's agent, whose model the card's second line shows, as the task's own card does.
                Detail = RunText.AgentLines(slot.Execution, slot.Execution is { } execution ? Status(execution.Client) : new ClientStatus.Checking()).Model,
            });
        }

        var added = proposal.Nodes.Where(node => chosen.Contains(node.Id)).ToDictionary(node => node.Id);
        var agents = Agents().ToDictionary(agent => agent.Id);
        foreach (var node in added.Values)
        {
            // A new task shows the agent it takes on the lines its card will, or its type while it has none.
            var agent = agents.GetValueOrDefault(node.Id);
            var hasAgent = agent?.Settings is not null;
            cards.Add(new GhostCardViewModel(
                WorkflowCanvasViewModel.ToPoint(layout[node.Id]), hasAgent ? agent!.Client : $"New {node.Blueprint.Name}", node.Title,
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
                WorkflowCanvasViewModel.ToPoint(At(connect.Key.From)!.Value) + WorkflowCanvasViewModel.OutputPortCenter,
                WorkflowCanvasViewModel.ToPoint(At(connect.Key.To)!.Value) + WorkflowCanvasViewModel.InputPortCenter,
                _canvas.KindOf(BlueprintOf(connect.Key.From)!),
                connect.Kind));
        return [.. wires, .. cards];
    }

    /// <summary>Checks the chosen tasks against the workflow and the runs as they are now.</summary>
    internal void Refresh()
    {
        foreach (var fill in Items.Take(Proposal?.Fills.Length ?? 0))
        {
            fill.ShowStarted(_canvas.HasStarted(fill.Id));
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

    private void Accept()
    {
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
    /// The one edit that accepts the chosen tasks, each new task with the person's choice, else its type's own agent,
    /// else the planner's usable choice, else the planner's agent while the box is ticked, as each row shows.
    /// </summary>
    private WorkflowEdit.Batch AcceptEdit(Workflow workflow, IReadOnlySet<TaskId> chosen) => Proposal!.Accept(
        workflow, chosen, _canvas.HasStarted, UsePlannerAgent ? workflow.Tasks.GetValueOrDefault(Proposal.Planner)?.Execution : null,
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
        return new ProposalItemViewModel(
            this, fill.Slot, fill.Title ?? slot ?? "Untitled task", hasText ? $"{label}. It has text of yours now" : label,
            Preview(task?.Blueprint, fill.Fields), empty, _canvas.KindOf(task?.Blueprint ?? BuiltInBlueprints.Implement), slot ?? "an untitled task",
            hasText);
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
