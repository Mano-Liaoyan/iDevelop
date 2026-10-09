using System.Windows.Input;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The agent that a task a proposal adds takes once accepted, which the review shows and the person may change: their
/// own choice, else the planner's when this machine can run it, else its type's, else the planner's agent while the
/// proposal's box is ticked. A planner's choice this machine cannot run says why, on the task's row and its ghost card.
/// </summary>
public sealed class ProposalAgentViewModel : ObservableObject
{
    private static readonly string[] Summary = [nameof(Label), nameof(Parts), nameof(Reason), nameof(Note)];

    // A new list clears its picker's selection, so each list is raised before its selection.
    private static readonly string[] Pickers =
    [
        nameof(ClientChoices), nameof(SelectedClient), nameof(ModelChoices), nameof(SelectedModel), nameof(ReasoningChoices),
        nameof(SelectedReasoning), nameof(HasReasoning),
    ];

    // About what a card's 11 px line holds beside its tile.
    private const int GhostLine = 32;

    private readonly ProposalViewModel _proposal;
    private readonly ProposedNode _node;
    private AgentCheck _check;
    private ExecutionSettings? _changed;
    private bool _isEditing;

    internal ProposalAgentViewModel(ProposalViewModel proposal, ProposedNode node)
    {
        _proposal = proposal;
        _node = node;
        _check = Check();
        EditCommand = new RelayCommand(() => _proposal.Edit(this));
    }

    /// <summary>"Codex · GPT-5.5 · high", or "No agent".</summary>
    public string Label => RunText.AgentLabel(Settings, Settings is { } settings ? _proposal.Status(settings.Client) : new ClientStatus.Checking());

    /// <summary>
    /// The client, then the model with its level, as two chips, which wrap whole in a narrow inspector and never leave a
    /// level on a line of its own.
    /// </summary>
    public IReadOnlyList<string> Parts => Settings is { } settings
        ? [Clients.Name(settings.Client), .. ModelAndLevel(settings) is { } rest ? [rest] : Array.Empty<string>()]
        : ["No agent"];

    /// <summary>The planner's reason for a choice the task takes, or what the person's own choice replaced.</summary>
    public string? Reason => _changed is not null
        ? _check switch
        {
            AgentCheck.Usable usable => $"Your choice. The planner chose {RunText.AgentLabel(usable.Settings, _proposal.Status(usable.Settings.Client))}.",
            AgentCheck.Unusable unusable => $"Your choice. {unusable.Problem}",
            _ => "Your choice.",
        }
        : _check is AgentCheck.Usable ? _node.Agent?.Reason : null;

    /// <summary>Why the planner's choice falls back, and to what, or null.</summary>
    public string? Note => Unusable is { } unusable ? $"{unusable.Problem} {Fallback switch
    {
        FallbackTo.Type => "The task keeps its type's agent instead.",
        FallbackTo.Planner => "The task takes the planner's agent instead.",
        FallbackTo.None => "Choose the task's agent before it runs.",
    }}" : null;

    /// <summary>
    /// The agent on the task's ghost card: the whole label on one line when a card's line holds it, else the client over
    /// the model and level, so the card never cuts the level off. Null while the task has no agent.
    /// </summary>
    internal (string Line, string? Second)? GhostAgent => Settings is not { } settings ? null
        : Label.Length <= GhostLine ? (Label, null)
        : (Clients.Name(settings.Client), ModelAndLevel(settings));

    /// <summary>The note under the task's ghost card, such as "Pi isn't ready · planner's agent", or null.</summary>
    public string? GhostNote => Unusable is { } unusable ? $"{unusable.Brief} · {Fallback switch
    {
        FallbackTo.Type => "type's agent",
        FallbackTo.Planner => "planner's agent",
        FallbackTo.None => "no agent",
    }}" : null;

    /// <summary>Whether the pickers show under the task's row. One task's at a time.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        internal set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(Editor));
                OnPropertyChanged(nameof(EditName));
            }
        }
    }

    /// <summary>This agent while its pickers show, else null, so only the open task's pickers are in the window.</summary>
    public ProposalAgentViewModel? Editor => _isEditing ? this : null;

    public ICommand EditCommand { get; }

    public string EditName => _isEditing ? $"Done changing the agent of \"{_node.Title}\"" : $"Change the agent of \"{_node.Title}\"";

    /// <summary>Every client, or only those with a read-only mode for a task that only reads.</summary>
    public IReadOnlyList<ClientChoice> ClientChoices =>
        [.. Clients.All.Where(id => !ReadOnly || Clients.HasReadOnlyMode(id)).Select(id => new ClientChoice(_node.Id, id, RunText.ClientChoice(id, _proposal.Status(id))))];

    public ClientChoice? SelectedClient => ClientChoices.FirstOrDefault(choice => choice.Id == Settings?.Client);

    public IReadOnlyList<Choice> ModelChoices => Settings is { } settings
        ? [.. ExecutionChoices.Models(settings, _proposal.Status(settings.Client))
            .Select(choice => new Choice(_node.Id, choice.Model.Id, RunText.ModelChoice(choice.Model, choice.Offered, _proposal.Status(settings.Client))))]
        : [];

    public Choice? SelectedModel => ModelChoices.FirstOrDefault(choice => choice.Id == Settings?.Model);

    public IReadOnlyList<Choice> ReasoningChoices => Settings is { } settings
        ? [.. ExecutionChoices.Reasoning(settings, _proposal.Status(settings.Client)).Select(choice => new Choice(_node.Id, choice.Level, RunText.ReasoningChoice(choice.Level, choice.Offered)))]
        : [];

    public Choice? SelectedReasoning => ReasoningChoices.FirstOrDefault(choice => choice.Id == Settings?.Reasoning);

    /// <summary>False when the model takes no reasoning level.</summary>
    public bool HasReasoning => ReasoningChoices.Count > 0;

    internal TaskId Id => _node.Id;

    /// <summary>The agent the task takes in place of its type's and the planner's: the person's, else the planner's that this machine runs.</summary>
    internal ExecutionSettings? Chosen => _changed ?? (_check as AgentCheck.Usable)?.Settings;

    /// <summary>What accepting gives the task, as <see cref="Proposal.Accept"/> decides it.</summary>
    internal ExecutionSettings? Settings => Chosen ?? _node.Blueprint.Defaults.Execution ?? _proposal.Fallback;

    /// <summary>The task has no agent of its own and its type has none, so the proposal's box decides its agent.</summary>
    internal bool NeedsFallback => Chosen is null && _node.Blueprint.Defaults.Execution is null;

    private bool ReadOnly => _node.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review;

    private AgentCheck.Unusable? Unusable => _changed is null ? _check as AgentCheck.Unusable : null;

    private FallbackTo Fallback => _node.Blueprint.Defaults.Execution is not null ? FallbackTo.Type
        : _proposal.Fallback is not null ? FallbackTo.Planner
        : FallbackTo.None;

    internal void ChooseClient(ClientChoice choice)
    {
        if (choice.Id is { } id && id != Settings?.Client)
        {
            Change(ExecutionChoices.ForClient(id, _proposal.Status(id)));
        }
    }

    internal void ChooseModel(Choice choice)
    {
        if (Settings is { } settings && choice.Id != settings.Model && ExecutionChoices.OfferedModel(_proposal.Status(settings.Client), choice.Id) is { } model)
        {
            Change(ExecutionChoices.ForModel(settings, model));
        }
    }

    internal void ChooseReasoning(Choice choice)
    {
        if (Settings is { } settings && choice.Id != settings.Reasoning
            && ExecutionChoices.OfferedModel(_proposal.Status(settings.Client), settings.Model) is { } model && model.ReasoningLevels.Contains(choice.Id))
        {
            Change(settings with { Reasoning = choice.Id });
        }
    }

    /// <summary>Checks the planner's choice again, after what the clients offer changed.</summary>
    internal void Recheck() => _check = Check();

    /// <summary>Shows the agent and its words again, as after an edit of the workflow that may change the planner's agent.</summary>
    internal void ShowSummary()
    {
        foreach (var property in Summary)
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>Shows what changed, the pickers too: the person's choice, the box, or the clients.</summary>
    internal void Show()
    {
        ShowSummary();
        foreach (var property in Pickers)
        {
            OnPropertyChanged(property);
        }
    }

    private AgentCheck Check() => AgentCheck.Of(_node.Agent, _node.Blueprint, _proposal.ClientStatuses);

    private string? ModelName(ExecutionSettings settings) =>
        settings.Model is { } id ? ExecutionChoices.OfferedModel(_proposal.Status(settings.Client), id)?.Name ?? id : null;

    /// <summary>"Claude Haiku 4.5 · low", the agent after its client, or null when it names neither.</summary>
    private string? ModelAndLevel(ExecutionSettings settings) =>
        string.Join(" · ", new[] { ModelName(settings), settings.Reasoning }.OfType<string>()) is { Length: > 0 } rest ? rest : null;

    private void Change(ExecutionSettings settings)
    {
        _changed = settings;
        _proposal.OnAgentChanged();
    }

    private enum FallbackTo { Type, Planner, None }
}
