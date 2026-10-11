using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The card's expansion (#94, ADR 0024): whether it shows its details, where the canvas's layout shows it, and the details
/// it shows, which an expanded card and a collapsed card's peek share. Expansion is the person's view of this machine: it
/// never changes the workflow.
/// </summary>
public sealed partial class TaskNodeViewModel
{
    private static readonly string[] DetailInputs =
    [
        nameof(State), nameof(Role), nameof(StatusLabel), nameof(RunDetail), nameof(Proposal), nameof(Preview), nameof(ReviewSummary),
        nameof(StartProblem), nameof(RunRefusal), nameof(RunOwner), nameof(LastAttempt), nameof(ShowsCardRun),
    ];

    private bool _isExpanded;
    private Vector _viewOffset;
    private Size _cardSize = new(WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight);
    private CardDetailViewModel? _detail;
    private int _detailHolds;
    private RelayCommand? _toggleExpand;

    /// <summary>The card shows its details. A new card starts collapsed.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
            {
                return;
            }

            if (value)
            {
                HoldDetail();
            }
            else
            {
                ReleaseDetail();
            }

            OnPropertyChanged(nameof(ExpandName));
            OnPropertyChanged(nameof(ShowsPeek));
            _canvas.OnCardExpansionChanged();
        }
    }

    public ICommand ToggleExpandCommand => _toggleExpand ??= new RelayCommand(() => IsExpanded = !IsExpanded);

    /// <summary>The disclosure's name and tooltip, such as Expand "Design".</summary>
    public string ExpandName => $"{(IsExpanded ? "Collapse" : "Expand")} \"{Title}\"";

    /// <summary>How far the canvas's layout moves the card on screen to make room for expanded cards.</summary>
    public Vector ViewOffset
    {
        get => _viewOffset;
        internal set
        {
            if (SetProperty(ref _viewOffset, value))
            {
                OnPropertyChanged(nameof(DisplayLocation));
            }
        }
    }

    /// <summary>
    /// Where the card shows: its saved position moved by the layout. A drag moves the saved position, by as much as it
    /// moves the card.
    /// </summary>
    public Point DisplayLocation
    {
        get => Location + _viewOffset;
        set => Location = value - _viewOffset;
    }

    /// <summary>The size the card's container shows, its port gutters included, which the layout and the wires follow.</summary>
    public Size CardSize
    {
        get => _cardSize;
        internal set
        {
            if (SetProperty(ref _cardSize, value))
            {
                _canvas.ScheduleCardLayout();
            }
        }
    }

    /// <summary>The card's container where it shows.</summary>
    public Rect DisplayRect => new(DisplayLocation, _cardSize);

    /// <summary>The details while the card is expanded or its peek shows, else null.</summary>
    public CardDetailViewModel? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>The peek's line under the title, such as "Implement · Codex · GPT-5.5 · high".</summary>
    public string PeekAgent => HasAgent ? $"{TypeName} · {AgentLabel}" : TypeName;

    /// <summary>Why the task cannot start, for the peek, except while a run owns it, which its status already says.</summary>
    public string? PeekProblem => IsRunOwned ? null : StartProblem;

    /// <summary>A collapsed card's tooltip is its peek. An expanded card shows everything already, and a renamed one shows none.</summary>
    public bool ShowsPeek => !IsExpanded && !IsRenaming;

    /// <summary>
    /// The tasks this one waits for, with where each stands: those the active run holds it back for, or, between runs, those
    /// without a result that its newest run could not start it without (#90). A listed task that waits for one task counts
    /// it, as its card does when the name does not fit, so the row keeps its room for the listed title.
    /// </summary>
    public IReadOnlyList<CardWait> Waits => WaitsFor is { } holders
        ? [.. holders.Select(id => _canvas.Nodes.FirstOrDefault(node => node.Id == id)).OfType<TaskNodeViewModel>()
            .OrderBy(node => node.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(node => new CardWait(node.Title, node.SubtitleChoices is [_, var counted] ? counted : node.StatusLabel, node.Kind))]
        : [];

    private IReadOnlyCollection<TaskId>? WaitsFor => Standing is TaskStanding.Waits waits ? waits.Holders
        : Standing is null && _runActive && RunTask is { State: TaskState.Pending, HeldBy: { Count: > 0 } held } ? held
        : null;

    /// <summary>
    /// What the expanded card reads of its task: its state and status, the attempt that state describes, and what the run
    /// and the runs before it say (#94, #90).
    /// </summary>
    internal CardFacts CardFacts(AttemptRecord? latest) => new(State, StatusLabel)
    {
        Role = Role,
        HasAgent = HasAgent,
        // The conversation reads the task's own attempts unless it is the run's, so an attempt older than an earlier
        // run's word on the task says nothing about it.
        Latest = Standing is null && (RunTask is null || ShowsRunConversation) ? latest : null,
        Detail = RunDetail,
        FromEarlierRun = Standing is TaskStanding.Succeeded || RunTask is { Carried: true },
        EarlierReport = (Standing as TaskStanding.Succeeded)?.Report,
        Refusal = RunRefusal,
        Owner = Standing is not null ? RunOwner : null,
        Waits = WaitsFor is not null,
        WaitsNote = Standing is null && WaitsFor is { } holders ? WorkflowRunText.Unbroken(WorkflowRunText.Unrun(holders, TitleOf, _canvas.Run?.View.Tasks)) : null,
        Proposal = Proposal is { HasItems: true } ? Subtitle : null,
        Review = ReviewSummary,
        FieldLabel = Fields.Count > 0 ? Fields[0].Spec.Label : "Task",
        FieldText = Fields.Count > 0 ? Fields[0].Text : null,
    };

    /// <summary>
    /// The expanded card offers Run where the inspector does, while nothing of the task runs or waits, and its tooltip says
    /// why Run would start nothing.
    /// </summary>
    public bool ShowsCardRun => HasAgent && ShowsRunControls && State is not (NodeState.Running or NodeState.RunningElsewhere
        or NodeState.Stopping or NodeState.Waiting or NodeState.InReview);

    /// <summary>The expanded card offers Stop while this window runs the task on its own.</summary>
    public bool ShowsCardStop => !IsRunOwned && State == NodeState.Running && RunsHere;

    /// <summary>The agent pickers describe the next attempt while one runs, which the card says under them.</summary>
    public bool ShowsNextRunNote => HasAgent && State is NodeState.Running or NodeState.RunningElsewhere or NodeState.Stopping or NodeState.Waiting
        or NodeState.InReview;

    /// <summary>The run needs the person's choice in the inspector, as the card's attention glyph also shows.</summary>
    internal bool AnswersInInspector => NeedsRunPanel;

    /// <summary>One more surface shows the details: the expanded card, or the open peek.</summary>
    internal void HoldDetail()
    {
        if (_detailHolds++ == 0)
        {
            OpenDetail();
        }
    }

    internal void ReleaseDetail()
    {
        if (_detailHolds > 0 && --_detailHolds == 0)
        {
            CloseDetail();
        }
    }

    /// <summary>The node leaves the canvas, so its details stop following its conversation.</summary>
    internal void DetachDetail()
    {
        _detailHolds = 0;
        CloseDetail();
    }

    partial void InitializeExpansion()
    {
        PropertyChanged += OnExpansionInputChanged;
        _canvas.RunRouteChanged += (_, _) => FollowRoute();
    }

    private void OnExpansionInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Location):
                OnPropertyChanged(nameof(DisplayLocation));
                break;
            case nameof(IsRenaming):
                OnPropertyChanged(nameof(ShowsPeek));
                break;
            case nameof(Title):
                OnPropertyChanged(nameof(ExpandName));
                break;
            case nameof(AgentLabel):
                OnPropertyChanged(nameof(PeekAgent));
                break;
            case nameof(Draft):
                _detail?.OnDraftChanged();
                break;
            case nameof(ShowsRunState) or nameof(RunTask) or nameof(ShowsRunConversation) or nameof(StatusLabel):
                OnPropertyChanged(nameof(Waits));
                FollowRoute();
                break;
        }

        if (e.PropertyName is nameof(StartProblem) or nameof(IsRunOwned))
        {
            OnPropertyChanged(nameof(PeekProblem));
        }

        if (e.PropertyName is nameof(State) or nameof(RunTask) or nameof(IsRunOwned) or nameof(JoinsRun) or nameof(Role))
        {
            OnPropertyChanged(nameof(ShowsCardRun));
            OnPropertyChanged(nameof(ShowsCardStop));
            OnPropertyChanged(nameof(ShowsNextRunNote));
        }

        if (DetailInputs.Contains(e.PropertyName))
        {
            _detail?.Refresh();
        }
    }

    /// <summary>The run's coordinator the task's conversation goes through, as the conversation view chooses it, or null.</summary>
    private WorkflowRunCoordinator? Route => _canvas.Run is { } run && ShowsRunConversation ? run.Coordinator : null;

    private void OpenDetail()
    {
        var route = Route;
        var session = !HasAgent ? null : route is not null ? _canvas.Runs.OpenConversation(route, Id) : _canvas.Runs.OpenConversation(Id);
        Detail = new CardDetailViewModel(this, session) { Route = route };
    }

    private void CloseDetail()
    {
        var detail = Detail;
        Detail = null;
        detail?.Dispose();
    }

    /// <summary>A run that starts, ends, or changes hands moves the task's conversation, so the details follow it.</summary>
    private void FollowRoute()
    {
        if (_detail is { } detail && !Equals(detail.Route, Route))
        {
            CloseDetail();
            OpenDetail();
        }
    }
}
