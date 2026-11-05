using System.Windows.Input;
using Avalonia.Threading;
using IDevelop.Clients;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The Generate Workflow sheet: the person's description and the planner's agent. Submitting places a Plan node in Chat
/// mode and runs it, and its proposal lands as any planner's does.
/// </summary>
public sealed class GenerateWorkflowViewModel : ObservableObject, IAgentPickerHost
{
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
        Picker = new AgentPicker(null, [.. PlanningClients], () => _planner, Status, Choose, ForPlanning);
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

    /// <summary>The planner's pickers. They offer the clients that have a read-only mode, because a planner changes no file.</summary>
    public AgentPicker Picker { get; }

    /// <summary>Why the planner cannot run on the chosen agent, which keeps Generate off, or null.</summary>
    public string? Problem => _planner is not { } planner
        ? $"None of {Names(PlanningClients)} is ready to plan. Agents in the sidebar says why."
        : Status(planner.Client) switch
        {
            ClientStatus.Ready ready => (planner.Model, ExecutionChoices.OfferedModel(ready, planner.Model)) switch
            {
                (null, _) => $"Choose a {ClientCatalog.Name(planner.Client)} model first.",
                ({ } id, null) => $"{ClientCatalog.Name(planner.Client)} does not offer {id} on this machine. Choose another model.",
                ({ } id, { Problem: { } problem }) => $"{id} cannot run now. {problem}",
                _ => null,
            },
            ClientStatus.Missing missing => $"{ClientCatalog.Name(planner.Client)} is not installed. {missing.Reason}",
            ClientStatus.Unready unready => $"{ClientCatalog.Name(planner.Client)} is not ready. {unready.Reason}",
            _ => $"iDevelop is still checking {ClientCatalog.Name(planner.Client)}.",
        };

    /// <summary>Places the planner and runs it. Off while the description is blank or the agent cannot plan.</summary>
    public ICommand SubmitCommand => _submit;

    public ICommand CancelCommand { get; }

    private bool CanSubmit => !string.IsNullOrWhiteSpace(_prompt) && Problem is null;

    private static IEnumerable<ClientId> PlanningClients => ClientCatalog.All.Where(ClientCatalog.HasReadOnlyMode);

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

    private void Choose(ExecutionSettings? planner)
    {
        _clientChosen |= planner?.Client != _planner?.Client;
        _planner = planner;
        ShowPlanner();
    }

    private void ShowPlanner()
    {
        Picker.Show();
        OnPropertyChanged(nameof(Problem));
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
        var names = clients.Select(ClientCatalog.Name).ToArray();
        return names.Length < 3 ? string.Join(" or ", names) : $"{string.Join(", ", names[..^1])}, or {names[^1]}";
    }
}
