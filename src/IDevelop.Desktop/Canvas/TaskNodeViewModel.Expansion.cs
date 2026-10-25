using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Mvvm;

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
        nameof(State), nameof(EngineState), nameof(Role), nameof(StatusLabel), nameof(Proposal), nameof(Preview), nameof(ReviewSummary),
        nameof(RunRefusal), nameof(LastReply), nameof(Results), nameof(AgentLabel),
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

    /// <summary>Why the node cannot run, for the peek.</summary>
    public string? PeekProblem => RunRefusal;

    /// <summary>A collapsed card's tooltip is its peek. An expanded card shows everything already, and a renamed one shows none.</summary>
    public bool ShowsPeek => !IsExpanded && !IsRenaming;

    /// <summary>What the expanded card reads of its node: its engine state and status, its latest reply and result, and its first field.</summary>
    internal CardFacts CardFacts() => new(_engineState, StatusLabel)
    {
        Role = Role,
        HasAgent = HasAgent,
        LastReply = LastReply,
        Report = _history.Count > 0 ? _history[^1].Report : null,
        Detail = StateDetail,
        Proposal = Proposal is { HasItems: true } ? Subtitle : null,
        Review = ReviewSummary,
        FieldLabel = Fields.Count > 0 ? Fields[0].Spec.Label : "Task",
        FieldText = Fields.Count > 0 ? Fields[0].Text : null,
    };

    /// <summary>The agent pickers describe the next turn while one runs or waits, which the card says under them.</summary>
    public bool ShowsNextRunNote => HasAgent && State is NodeState.Running;

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
            case nameof(RunRefusal):
                OnPropertyChanged(nameof(PeekProblem));
                break;
            case nameof(State):
                OnPropertyChanged(nameof(ShowsNextRunNote));
                break;
        }

        if (DetailInputs.Contains(e.PropertyName))
        {
            _detail?.Refresh();
        }
    }

    private void OpenDetail() => Detail = new CardDetailViewModel(this);

    private void CloseDetail()
    {
        var detail = Detail;
        Detail = null;
        detail?.Dispose();
    }
}
