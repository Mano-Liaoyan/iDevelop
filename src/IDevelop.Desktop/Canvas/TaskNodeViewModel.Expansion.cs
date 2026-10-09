using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;

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
        nameof(StartProblem), nameof(LastAttempt),
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

    /// <summary>The tasks a run holds this one back for, with where each stands, while it does.</summary>
    public IReadOnlyList<CardWait> Waits => RunTask is { State: TaskState.Pending, HeldBy: var held } && _runActive
        ? [.. held.Select(id => _canvas.Nodes.FirstOrDefault(node => node.Id == id)).OfType<TaskNodeViewModel>()
            .Select(node => new CardWait(node.Title, node.StatusLabel, node.Kind))]
        : [];

    /// <summary>The expanded card offers Run: the task is the person's to start, and nothing of it runs or waits now.</summary>
    public bool ShowsCardRun => HasAgent && !IsRunOwned && State is not (NodeState.Running or NodeState.RunningElsewhere or NodeState.Stopping
        or NodeState.Waiting or NodeState.InReview);

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
            case nameof(ShowsRunState) or nameof(RunTask):
                OnPropertyChanged(nameof(Waits));
                FollowRoute();
                break;
        }

        if (e.PropertyName is nameof(StartProblem) or nameof(IsRunOwned))
        {
            OnPropertyChanged(nameof(PeekProblem));
        }

        if (e.PropertyName is nameof(State) or nameof(RunTask) or nameof(IsRunOwned))
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
    private object? Route => _canvas.Run is { } run && ShowsRunState ? run.Coordinator : null;

    private void OpenDetail()
    {
        var route = Route;
        var session = !HasAgent ? null : _canvas.Run is { } run && ShowsRunState
            ? _canvas.Runs.OpenConversation(run.Coordinator, Id)
            : _canvas.Runs.OpenConversation(Id);
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
