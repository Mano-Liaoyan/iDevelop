using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>Where the newest planner that Generate placed in this window stands, which the Generate button shows.</summary>
public enum GenerateProgress
{
    Idle,
    Planning,
    Reviewing,
}

/// <summary>
/// Generate Workflow: the sheet that takes a description, and the Plan node in Chat mode that it places and runs. Its
/// proposal lands as ghost cards that the person accepts, so nothing is added without Accept.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private const int TitleLength = 60;

    private readonly HashSet<TaskId> _generated = [];
    private readonly Dictionary<TaskId, bool> _usesPlannerAgent = [];
    private TaskId? _latestGenerated;
    private RelayCommand? _openGenerate;
    private GenerateWorkflowViewModel? _sheet;
    private GenerateProgress _generateProgress;

    /// <summary>The open Generate Workflow sheet, or null.</summary>
    public GenerateWorkflowViewModel? Sheet
    {
        get => _sheet;
        private set => SetProperty(ref _sheet, value);
    }

    public ICommand OpenGenerateCommand => _openGenerate ??= new RelayCommand(OpenSheet);

    /// <summary>The workflow has no task, so the canvas offers to start from a description.</summary>
    public bool IsEmpty => Nodes.Count == 0;

    public GenerateProgress GenerateProgress
    {
        get => _generateProgress;
        private set
        {
            if (SetProperty(ref _generateProgress, value))
            {
                OnPropertyChanged(nameof(GenerateLabel));
                OnPropertyChanged(nameof(IsGenerateIdle));
                OnPropertyChanged(nameof(IsPlanning));
                OnPropertyChanged(nameof(IsReviewing));
            }
        }
    }

    public string GenerateLabel => GenerateProgress switch
    {
        GenerateProgress.Idle => "Generate",
        GenerateProgress.Planning => "Planning…",
        GenerateProgress.Reviewing => "Review Proposal",
    };

    public bool IsGenerateIdle => GenerateProgress == GenerateProgress.Idle;

    public bool IsPlanning => GenerateProgress == GenerateProgress.Planning;

    public bool IsReviewing => GenerateProgress == GenerateProgress.Reviewing;

    /// <summary>The view's size in canvas units, which the view keeps current. Generate centers its planner in it.</summary>
    internal Size ViewportSize { get; set; }

    /// <summary>Whether Generate placed the planner in this window.</summary>
    internal bool Generated(TaskId planner) => _generated.Contains(planner);

    /// <summary>
    /// Whether the planner's new tasks take its agent when their type has none. The person's choice holds for every
    /// proposal of the planner. Until they choose, it is on only for a planner that Generate placed.
    /// </summary>
    internal bool UsesPlannerAgent(TaskId planner) => _usesPlannerAgent.TryGetValue(planner, out var uses) ? uses : Generated(planner);

    internal void ChoosePlannerAgent(TaskId planner, bool uses) => _usesPlannerAgent[planner] = uses;

    /// <summary>
    /// What the Generate button does: it opens the sheet, or, while the newest generated planner plans or its proposal
    /// waits, selects that planner and returns it.
    /// </summary>
    internal TaskNodeViewModel? ShowGenerate()
    {
        if (GenerateProgress != GenerateProgress.Idle && _latestGenerated is { } id && _nodes.TryGetValue(id, out var planner))
        {
            Select(planner);
            return planner;
        }

        OpenSheet();
        return null;
    }

    internal void CloseSheet()
    {
        Sheet?.Detach();
        Sheet = null;
        _runWorkflow?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Closes the sheet and places a Plan node in Chat mode with the description as its goal, left in the view and at
    /// its middle height, then selects and runs it. A refused run leaves the node, and the notice says why.
    /// </summary>
    internal void Generate(string prompt, ExecutionSettings planner)
    {
        CloseSheet();
        var start = new CanvasPoint(ViewportLocation.X + 60, ViewportLocation.Y + ViewportSize.Height / 2 - TaskCardHeight / 2);
        var edit = new WorkflowEdit.PlaceNode(TaskId.New(), BuiltInBlueprints.Plan, FreeSpot(start))
        {
            Title = PlannerTitle(prompt),
            Fields = ImmutableDictionary<string, string>.Empty.Add("goal", prompt.Trim()),
            Settings = new NodeSettings(planner, ConversationMode.Chat),
        };
        if (Place(edit) is not { } node)
        {
            return;
        }

        _generated.Add(node.Id);
        _latestGenerated = node.Id;
        node.PropertyChanged += OnGeneratedChanged;
        node.RunCommand.Execute(null);
        ShowGenerateProgress();
    }

    /// <summary>The description's first line that has text, cut to 60 characters, or "Plan".</summary>
    internal static string PlannerTitle(string prompt) =>
        prompt.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) is not { } line ? "Plan"
        : line.Length <= TitleLength ? line
        : $"{line[..(TitleLength - 1)].TrimEnd()}…";

    partial void InitializeGenerate()
    {
        Nodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            ShowGenerateProgress();
        };
    }

    private void OpenSheet()
    {
        Sheet ??= new GenerateWorkflowViewModel(this);
        _runWorkflow?.NotifyCanExecuteChanged();
    }

    private void OnGeneratedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskNodeViewModel.State) or nameof(TaskNodeViewModel.Role))
        {
            ShowGenerateProgress();
        }
    }

    private void ShowGenerateProgress() =>
        GenerateProgress = _latestGenerated is { } id && _nodes.TryGetValue(id, out var planner)
            ? planner switch
            {
                { Role: NodeRole.Proposing } => GenerateProgress.Reviewing,
                { State: NodeState.Running or NodeState.RunningElsewhere or NodeState.Stopping } => GenerateProgress.Planning,
                _ => GenerateProgress.Idle,
            }
            : GenerateProgress.Idle;
}
