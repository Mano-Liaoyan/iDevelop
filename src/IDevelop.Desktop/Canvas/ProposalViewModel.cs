using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>A dashed card on the canvas for a task that a proposal adds, or fills in place of the empty card.</summary>
public sealed record GhostCardViewModel(Point Location, string Label, string Title, string Preview);

/// <summary>A task that a proposal adds or fills, which the person can untick before accepting.</summary>
public sealed class ProposalItemViewModel(ProposalViewModel proposal, TaskId id, string label, string preview, bool isChosen) : ObservableObject
{
    private bool _isChosen = isChosen;

    internal TaskId Id { get; } = id;

    public string Label { get; } = label;

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
                Items = [.. Proposal.Fills.Select(fill => Item(fill.Slot, FillLabel(fill), Preview(SlotBlueprint(fill.Slot), fill.Fields), IsEmptySlot(fill.Slot))),
                    .. Proposal.Nodes.Where(node => !workflow.Tasks.ContainsKey(node.Id))
                        .Select(node => Item(node.Id, $"Add {node.Blueprint.Name} \"{node.Title}\"", Preview(node.Blueprint, node.Fields), true))];
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

    /// <summary>Each connection that Accept adds now, as its two tasks' titles.</summary>
    public IReadOnlyList<string> Connections => Proposal is null ? [] :
        [.. Proposal.Accept(_canvas.Workflow, Chosen()).Edits.OfType<WorkflowEdit.Connect>().Select(connect =>
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
        workflow.Apply(proposal.Accept(workflow, proposal.Items.ToHashSet())) is EditResult.Applied applied && ReferenceEquals(applied.Workflow, workflow);

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
            yield return new GhostCardViewModel(
                WorkflowCanvasViewModel.ToPoint(workflow.Positions[fill.Slot]), "Fills", fill.Title ?? workflow.Tasks[fill.Slot].Title,
                Preview(workflow.Tasks[fill.Slot].Blueprint, fill.Fields));
        }

        var layout = proposal.Layout(workflow);
        foreach (var node in proposal.Nodes.Where(node => chosen.Contains(node.Id)))
        {
            yield return new GhostCardViewModel(WorkflowCanvasViewModel.ToPoint(layout[node.Id]), $"New {node.Blueprint.Name}", node.Title, Preview(node.Blueprint, node.Fields));
        }
    }

    /// <summary>Checks the chosen tasks against the workflow as it is now.</summary>
    internal void Refresh()
    {
        Problem = _readProblem ?? (Proposal is { } proposal && _canvas.Workflow.Apply(proposal.Accept(_canvas.Workflow, Chosen())) is EditResult.Rejected rejected
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
        if (_canvas.Edit(Proposal!.Accept(workflow, chosen), Title) is EditResult.Applied)
        {
            var added = Proposal.Nodes.Count(node => chosen.Contains(node.Id));
            var filled = Proposal.Fills.Count(fill => chosen.Contains(fill.Slot));
            _canvas.CloseProposal(this);
            _canvas.Notice($"Added {Count(added, "task")} and filled {Count(filled, "task")}.");
        }
    }

    private ProposalItemViewModel Item(TaskId id, string label, string preview, bool isChosen) => new(this, id, label, preview, isChosen);

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
