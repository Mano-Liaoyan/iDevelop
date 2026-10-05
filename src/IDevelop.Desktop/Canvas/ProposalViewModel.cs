using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>A dashed card on the canvas for a task that a proposal adds, or fills in place of the empty card.</summary>
public sealed record GhostCardViewModel(Point Location, string Label, string Title, string Preview, NodeKind Kind);

/// <summary>A task that a proposal adds or fills, which the person can untick before accepting.</summary>
public sealed class ProposalItemViewModel(ProposalViewModel proposal, TaskId id, string label, string preview, bool isChosen, NodeKind kind) : ObservableObject
{
    private bool _isChosen = isChosen;
    private bool _hasStarted;

    internal TaskId Id { get; } = id;

    public NodeKind Kind { get; } = kind;

    /// <summary>False for a fill of a task that has started, which no proposal changes.</summary>
    public bool CanChoose => !_hasStarted;

    public string Label => _hasStarted ? $"{label}. It has started, so it stays as it is" : label;

    public string Preview { get; } = preview;

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
                Items = [.. Proposal.Fills.Select(fill => Item(fill.Slot, FillLabel(fill), Preview(SlotBlueprint(fill.Slot), fill.Fields), IsEmptySlot(fill.Slot), SlotBlueprint(fill.Slot))),
                    .. Proposal.Nodes.Where(node => !workflow.Tasks.ContainsKey(node.Id))
                        .Select(node => Item(node.Id, $"Add {node.Blueprint.Name} \"{node.Title}\"", Preview(node.Blueprint, node.Fields), true, node.Blueprint))];
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

    /// <summary>The proposal adds tasks, whose agent <see cref="UsePlannerAgent"/> decides. A fill keeps its task's agent.</summary>
    public bool AddsTasks => Items.Count > (Proposal?.Fills.Length ?? 0);

    /// <summary>
    /// Whether each new task whose type has no agent takes the planner's agent. On for a planner that Generate placed
    /// in this window, and off for any other, so a new task takes its type's default agent unless the person opts in.
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
            }
        }
    }

    /// <summary>Each connection that Accept adds now, as its two tasks' titles.</summary>
    public IReadOnlyList<string> Connections => Proposal is null ? [] :
        [.. AcceptEdit(_canvas.Workflow, Chosen()).Edits.OfType<WorkflowEdit.Connect>().Select(connect =>
            $"{Title(connect.Key.From)} → {Title(connect.Key.To)}{(connect.Kind == ConnectionKind.Context ? " (context)" : "")}")];

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

    /// <summary>The chosen tasks as ghost cards: a new task where accepting places it, and a fill over its empty card.</summary>
    internal IEnumerable<GhostCardViewModel> Ghosts(Workflow workflow)
    {
        if (Proposal is not { } proposal)
        {
            yield break;
        }

        var chosen = Chosen();
        foreach (var fill in proposal.Fills.Where(fill => chosen.Contains(fill.Slot) && workflow.Positions.ContainsKey(fill.Slot)))
        {
            var slot = workflow.Tasks[fill.Slot];
            yield return new GhostCardViewModel(
                WorkflowCanvasViewModel.ToPoint(workflow.Positions[fill.Slot]), "Fills", fill.Title ?? slot.Title,
                Preview(slot.Blueprint, fill.Fields), _canvas.KindOf(slot.Blueprint));
        }

        var layout = proposal.Layout(workflow);
        foreach (var node in proposal.Nodes.Where(node => chosen.Contains(node.Id)))
        {
            yield return new GhostCardViewModel(
                WorkflowCanvasViewModel.ToPoint(layout[node.Id]), $"New {node.Blueprint.Name}", node.Title, Preview(node.Blueprint, node.Fields), _canvas.KindOf(node.Blueprint));
        }
    }

    /// <summary>Checks the chosen tasks against the workflow and the runs as they are now.</summary>
    internal void Refresh()
    {
        foreach (var fill in Items.Take(Proposal?.Fills.Length ?? 0))
        {
            fill.ShowStarted(_canvas.HasStarted(fill.Id));
        }

        Problem = _readProblem ?? (Proposal is not null && _canvas.Workflow.Apply(AcceptEdit(_canvas.Workflow, Chosen())) is EditResult.Rejected rejected
            ? $"Accept would be refused. {RejectionText.Describe(rejected.Reason, Title)}"
            : null);
        OnPropertyChanged(nameof(Connections));
        _accept.NotifyCanExecuteChanged();
    }

    internal void OnChoiceChanged()
    {
        Refresh();
        _canvas.ShowGhosts();
    }

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
            _canvas.Notice($"Added {Count(added, "task")} and filled {Count(filled, "task")}.");
        }
    }

    /// <summary>The one edit that accepts the chosen tasks, with the planner's agent as the fallback while the box is ticked.</summary>
    private WorkflowEdit.Batch AcceptEdit(Workflow workflow, IReadOnlySet<TaskId> chosen) => Proposal!.Accept(
        workflow, chosen, _canvas.HasStarted, UsePlannerAgent ? workflow.Tasks.GetValueOrDefault(Proposal.Planner)?.Execution : null);

    /// <param name="blueprint">The blueprint of the task, or null for a fill whose task is gone, which reads as Implement.</param>
    private ProposalItemViewModel Item(TaskId id, string label, string preview, bool isChosen, Blueprint? blueprint) =>
        new(this, id, label, preview, isChosen, _canvas.KindOf(blueprint ?? BuiltInBlueprints.Implement));

    private bool IsEmptySlot(TaskId slot) => _canvas.Workflow.Tasks.TryGetValue(slot, out var task) && PlanningContext.IsEmpty(task);

    private Blueprint? SlotBlueprint(TaskId slot) => _canvas.Workflow.Tasks.GetValueOrDefault(slot)?.Blueprint;

    private string FillLabel(ProposedFill fill)
    {
        var slot = _canvas.Workflow.Tasks.TryGetValue(fill.Slot, out var task) && !string.IsNullOrWhiteSpace(task.Title) ? $"\"{task.Title.Trim()}\"" : "an untitled task";
        var label = fill.Title is { } title && title != task?.Title.Trim() ? $"Fill {slot} as \"{title}\"" : $"Fill {slot}";
        return task is null || PlanningContext.IsEmpty(task) ? label : $"{label}. It has text of yours now";
    }

    /// <summary>A title the proposal gives, else the workflow's, for a message about a task.</summary>
    private string Title(TaskId id) =>
        Proposal?.TitleOf(id) ?? (_canvas.Workflow.Tasks.TryGetValue(id, out var task) ? task.Title : "a removed task");

    /// <summary>The first value the proposal gives, in the blueprint's field order, as a card previews its first field.</summary>
    private static string Preview(Blueprint? blueprint, IReadOnlyDictionary<string, string> fields) =>
        (blueprint?.Fields.Select(field => field.Key) ?? fields.Keys)
            .Select(key => fields.GetValueOrDefault(key))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
