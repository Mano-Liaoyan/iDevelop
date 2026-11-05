using Avalonia.Controls;
using IDevelop.Clients;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;
using JetBrains.Annotations;

namespace IDevelop.Desktop.Canvas;

// A picker's UI Automation value is its chosen entry's text, so each entry's text is its label. The inspector reuses
// its pickers for every task, and a picker keeps its selected entry when the new list holds an equal one, so an entry
// names its task. The Generate sheet has no task yet, so its entries name none.

/// <summary>An entry in a picker. Its type says what the entry sets.</summary>
public abstract record Choice([UsedImplicitly] TaskId? Task, string Label)
{
    public sealed override string ToString() => Label;
}

/// <summary>An entry in a client picker. A null client is "None".</summary>
public sealed record ClientChoice(TaskId? Task, ClientId? Id, string Label) : Choice(Task, Label);

/// <summary>An entry in a model picker: the client's own id and what the picker shows.</summary>
public sealed record ModelChoice(TaskId? Task, string Id, string Label) : Choice(Task, Label);

/// <summary>An entry in a reasoning picker: the client's own level and what the picker shows.</summary>
public sealed record ReasoningChoice(TaskId? Task, string Id, string Label) : Choice(Task, Label);

/// <summary>An entry in the inspector's conversation picker.</summary>
public sealed record ConversationChoice(TaskId? Task, ConversationMode Mode, string Label) : Choice(Task, Label);

/// <summary>A view model whose view shows an <see cref="AgentPicker"/>.</summary>
internal interface IAgentPickerHost
{
    AgentPicker Picker { get; }
}

/// <summary>
/// The client, model, and reasoning pickers of one agent. The host owns the agent's settings: the pickers read them,
/// and the host decides what a choice does with them.
/// </summary>
public sealed class AgentPicker : ObservableObject
{
    // A new list clears its picker's selection, so each list is raised before its selection.
    private static readonly string[] Properties =
    [
        nameof(ClientChoices), nameof(SelectedClient), nameof(ModelChoices), nameof(SelectedModel),
        nameof(ReasoningChoices), nameof(SelectedReasoning), nameof(HasReasoning),
    ];

    private readonly TaskId? _task;
    private readonly IReadOnlyList<ClientId?> _clients;
    private readonly Func<ExecutionSettings?> _settings;
    private readonly Func<ClientId, ClientStatus> _status;
    private readonly Action<ExecutionSettings?> _apply;
    private readonly Func<ClientId, ExecutionSettings> _forClient;

    /// <param name="task">The task the entries name, or null when no task exists yet.</param>
    /// <param name="clients">The client picker's entries in order. A null entry is "None".</param>
    /// <param name="settings">The agent's settings now.</param>
    /// <param name="status">What a client offers now.</param>
    /// <param name="apply">Takes the settings that a person's choice makes.</param>
    /// <param name="forClient">The settings that a newly chosen client starts with.</param>
    internal AgentPicker(TaskId? task, IReadOnlyList<ClientId?> clients, Func<ExecutionSettings?> settings, Func<ClientId, ClientStatus> status,
        Action<ExecutionSettings?> apply, Func<ClientId, ExecutionSettings> forClient)
    {
        _task = task;
        _clients = clients;
        _settings = settings;
        _status = status;
        _apply = apply;
        _forClient = forClient;
    }

    public IReadOnlyList<ClientChoice> ClientChoices
    {
        get => Kept(ref field,
            [.. _clients.Select(id => new ClientChoice(_task, id, id is { } client ? RunText.ClientChoice(client, _status(client)) : "None"))]);
    } = [];

    public ClientChoice? SelectedClient => ClientChoices.FirstOrDefault(choice => choice.Id == _settings()?.Client);

    /// <summary>The models this machine offers, then the agent's model when it is not one of them, marked.</summary>
    public IReadOnlyList<ModelChoice> ModelChoices
    {
        get => Kept(ref field, _settings() is { } settings
            ? [.. ExecutionChoices.Models(settings, _status(settings.Client))
                .Select(choice => new ModelChoice(_task, choice.Model.Id, RunText.ModelChoice(choice.Model, choice.Offered, _status(settings.Client))))]
            : []);
    } = [];

    public ModelChoice? SelectedModel => ModelChoices.FirstOrDefault(choice => choice.Id == _settings()?.Model);

    /// <summary>The levels the agent's model offers, then the agent's level when the model does not offer it, marked.</summary>
    public IReadOnlyList<ReasoningChoice> ReasoningChoices
    {
        get => Kept(ref field, _settings() is { } settings
            ? [.. ExecutionChoices.Reasoning(settings, _status(settings.Client))
                .Select(choice => new ReasoningChoice(_task, choice.Level, RunText.ReasoningChoice(choice.Level, choice.Offered)))]
            : []);
    } = [];

    public ReasoningChoice? SelectedReasoning => ReasoningChoices.FirstOrDefault(choice => choice.Id == _settings()?.Reasoning);

    /// <summary>False when the model takes no reasoning level, such as an Antigravity model without a level suffix.</summary>
    public bool HasReasoning => ReasoningChoices.Count > 0;

    // A click, a key, and UI Automation each choose in a picker their own way, and a picker's binding only shows the
    // agent, so a change of selection is the choice. A picker whose list is replaced keeps an equal entry from its old
    // list selected, such as the old level when the new client's model also offers it, so only a change in the open
    // list or by a key on the focused picker is a choice.
    internal static bool IsChoice(ComboBox picker) => picker.IsDropDownOpen || picker.IsKeyboardFocusWithin;

    /// <summary>The selection handler of every agent picker in every view.</summary>
    internal static void OnChosen(object? sender)
    {
        if (sender is ComboBox { DataContext: IAgentPickerHost host } picker && IsChoice(picker))
        {
            host.Picker.Choose(picker.SelectedItem);
        }
    }

    /// <summary>Shows the settings and what the clients offer again.</summary>
    internal void Show()
    {
        foreach (var property in Properties)
        {
            OnPropertyChanged(property);
        }
    }

    // A picker whose list is replaced rebuilds its entries, which can swallow a click or key the person gives it at that
    // moment, so a list that came out the same, as after a skill listing lands, stays the very list the picker shows.
    private static IReadOnlyList<T> Kept<T>(ref IReadOnlyList<T> shown, IReadOnlyList<T> now) => now.SequenceEqual(shown) ? shown : shown = now;

    private void Choose(object? entry)
    {
        var settings = _settings();
        switch (entry)
        {
            case ClientChoice client when client.Id != settings?.Client:
                _apply(client.Id is { } id ? _forClient(id) : null);
                break;
            case ModelChoice choice when settings is not null && choice.Id != settings.Model
                && ExecutionChoices.OfferedModel(_status(settings.Client), choice.Id) is { } model:
                _apply(ExecutionChoices.ForModel(settings, model));
                break;
            case ReasoningChoice choice when settings is not null && choice.Id != settings.Reasoning
                && ExecutionChoices.OfferedModel(_status(settings.Client), settings.Model) is { } model && model.ReasoningLevels.Contains(choice.Id):
                _apply(settings with { Reasoning = choice.Id });
                break;
        }
    }
}
