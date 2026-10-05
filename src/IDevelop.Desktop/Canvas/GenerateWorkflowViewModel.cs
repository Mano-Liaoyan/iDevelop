using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>An entry in the sheet's client picker, which shows its label.</summary>
public sealed record PlannerClientChoice(ClientId Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An entry in the sheet's model or reasoning picker: the client's own id and what the picker shows.</summary>
public sealed record PlannerChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Generate Workflow sheet: the person's description and the planner's agent. Submitting places a Plan node in Chat
/// mode and runs it, and its proposal lands as any planner's does.
/// </summary>
public sealed class GenerateWorkflowViewModel : ObservableObject
{
    // A new list clears its picker's selection, so each list is raised before its selection.
    private static readonly string[] PickerProperties =
    [
        nameof(ClientChoices), nameof(SelectedClient), nameof(ModelChoices), nameof(SelectedModel),
        nameof(ReasoningChoices), nameof(SelectedReasoning), nameof(HasReasoning), nameof(Problem),
    ];

    // Planning a whole workflow is the deepest task an agent does here, so the planner starts at its model's high level.
    private const string PlanningLevel = "high";

    private readonly WorkflowCanvasViewModel _canvas;
    private readonly RelayCommand _submit;
    private string _prompt = "";
    private ExecutionSettings? _planner;
    private bool _clientChosen;

    internal GenerateWorkflowViewModel(WorkflowCanvasViewModel canvas)
    {
        _canvas = canvas;
        _planner = DefaultPlanner();
        _submit = new RelayCommand(Submit, () => CanSubmit);
        CancelCommand = new RelayCommand(canvas.CloseSheet);
        canvas.Clients.Changed += OnClientsChanged;
    }

    /// <summary>What the person wants built. Its first line titles the planner, and all of it is the planner's goal.</summary>
    public string Prompt
    {
        get => _prompt;
        set
        {
            if (SetProperty(ref _prompt, value))
            {
                _submit.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The clients that have a read-only mode, because a planner changes no file.</summary>
    public IReadOnlyList<PlannerClientChoice> ClientChoices =>
        [.. PlanningClients.Select(id => new PlannerClientChoice(id, RunText.ClientChoice(id, Status(id))))];

    public PlannerClientChoice? SelectedClient => ClientChoices.FirstOrDefault(choice => choice.Id == _planner?.Client);

    public IReadOnlyList<PlannerChoice> ModelChoices => _planner is { } planner
        ? [.. ExecutionChoices.Models(planner, Status(planner.Client)).Select(choice => new PlannerChoice(choice.Model.Id, RunText.ModelChoice(choice.Model, choice.Offered, Status(planner.Client))))]
        : [];

    public PlannerChoice? SelectedModel => ModelChoices.FirstOrDefault(choice => choice.Id == _planner?.Model);

    public IReadOnlyList<PlannerChoice> ReasoningChoices => _planner is { } planner
        ? [.. ExecutionChoices.Reasoning(planner, Status(planner.Client)).Select(choice => new PlannerChoice(choice.Level, RunText.ReasoningChoice(choice.Level, choice.Offered)))]
        : [];

    public PlannerChoice? SelectedReasoning => ReasoningChoices.FirstOrDefault(choice => choice.Id == _planner?.Reasoning);

    /// <summary>False when the model takes no reasoning level.</summary>
    public bool HasReasoning => ReasoningChoices.Count > 0;

    /// <summary>Why the planner cannot run on the chosen agent, which keeps Generate off, or null.</summary>
    public string? Problem => _planner is not { } planner
        ? $"None of {Names(PlanningClients)} is ready to plan. Agents in the sidebar says why."
        : Status(planner.Client) switch
        {
            ClientStatus.Ready ready => (planner.Model, ExecutionChoices.OfferedModel(ready, planner.Model)) switch
            {
                (null, _) => RunText.Describe(new StartProblem.NoModel(planner.Client)),
                ({ } id, null) => RunText.Describe(new StartProblem.ModelNotOffered(planner.Client, id)),
                ({ } id, { Problem: { } problem }) => RunText.Describe(new StartProblem.ModelUnready(planner.Client, id, problem)),
                _ => null,
            },
            ClientStatus.Missing missing => RunText.Describe(new StartProblem.ClientMissing(planner.Client, missing.Reason)),
            ClientStatus.Unready unready => RunText.Describe(new StartProblem.ClientUnready(planner.Client, unready.Reason)),
            _ => RunText.Describe(new StartProblem.ClientChecking(planner.Client)),
        };

    /// <summary>Places the planner and runs it. Off while the description is blank or the agent cannot plan.</summary>
    public ICommand SubmitCommand => _submit;

    public ICommand CancelCommand { get; }

    private bool CanSubmit => !string.IsNullOrWhiteSpace(_prompt) && Problem is null;

    private static IEnumerable<ClientId> PlanningClients => Clients.All.Where(Clients.HasReadOnlyMode);

    internal void ChooseClient(PlannerClientChoice choice)
    {
        if (choice.Id != _planner?.Client)
        {
            _clientChosen = true;
            Choose(ForPlanning(choice.Id));
        }
    }

    internal void ChooseModel(PlannerChoice choice)
    {
        if (_planner is { } planner && choice.Id != planner.Model && ExecutionChoices.OfferedModel(Status(planner.Client), choice.Id) is { } model)
        {
            Choose(ExecutionChoices.ForModel(planner, model));
        }
    }

    internal void ChooseReasoning(PlannerChoice choice)
    {
        if (_planner is { } planner && choice.Id != planner.Reasoning
            && ExecutionChoices.OfferedModel(Status(planner.Client), planner.Model) is { } model && model.ReasoningLevels.Contains(choice.Id))
        {
            Choose(planner with { Reasoning = choice.Id });
        }
    }

    /// <summary>Stops following the clients, once the sheet closes.</summary>
    internal void Detach() => _canvas.Clients.Changed -= OnClientsChanged;

    /// <summary>The first ready client that can plan, in the clients' order, with its first usable model.</summary>
    private ExecutionSettings? DefaultPlanner() =>
        PlanningClients.Where(id => Status(id) is ClientStatus.Ready).Select(ForPlanning).FirstOrDefault();

    /// <summary>The client's first usable model at the planning level, or at the model's own default when it lacks that level.</summary>
    private ExecutionSettings ForPlanning(ClientId client)
    {
        var settings = ExecutionChoices.ForClient(client, Status(client));
        return ExecutionChoices.OfferedModel(Status(client), settings.Model) is { } model && model.ReasoningLevels.Contains(PlanningLevel)
            ? settings with { Reasoning = PlanningLevel }
            : settings;
    }

    private ClientStatus Status(ClientId client) => _canvas.Clients.Current[client];

    private void Choose(ExecutionSettings planner)
    {
        _planner = planner;
        ShowPlanner();
    }

    private void ShowPlanner()
    {
        foreach (var property in PickerProperties)
        {
            OnPropertyChanged(property);
        }

        _submit.NotifyCanExecuteChanged();
    }

    // Until the person picks a client, the sheet follows the first one that becomes ready. A picked client keeps its
    // model and level, and takes its first model once it is ready.
    private void OnClientsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _planner = !_clientChosen ? DefaultPlanner()
            : _planner is { Model: null } planner ? ForPlanning(planner.Client)
            : _planner;
        ShowPlanner();
    });

    private void Submit()
    {
        if (CanSubmit && _planner is { } planner)
        {
            _canvas.Generate(_prompt, planner);
        }
    }

    private static string Names(IEnumerable<ClientId> clients)
    {
        var names = clients.Select(Clients.Name).ToArray();
        return names.Length < 3 ? string.Join(" or ", names) : $"{string.Join(", ", names[..^1])}, or {names[^1]}";
    }
}
