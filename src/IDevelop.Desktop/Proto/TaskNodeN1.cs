using System.Windows.Input;
using Avalonia;
using IDevelop.Desktop.Execution;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Canvas;

// THROWAWAY N1 PROTOTYPE. Never merge. What an expanded card shows, computed only from what the app records today.

/// <summary>What the expanded card's main section is about, which picks its template.</summary>
public enum DetailKind { Running, Question, Result, Failed, Waits, NotRun, Other }

/// <summary>A predecessor a pending task waits for, with its own status.</summary>
public sealed record WaitItem(string Title, string Status, NodeState State, NodeKind Kind);

/// <summary>A link from a finished task to what it handed on.</summary>
public sealed record ResultLink(string Label, string Help, ICommand Command);

public sealed partial class TaskNodeViewModel
{
    private static readonly string[] DetailProperties =
    [
        nameof(Detail), nameof(NowHeading), nameof(NowText), nameof(NowMeta), nameof(WaitsFor), nameof(ResultLinks), nameof(HasResultLinks),
        nameof(IsRunningDetail), nameof(IsQuestionDetail), nameof(IsResultDetail), nameof(IsFailedDetail), nameof(IsWaitsDetail),
        nameof(IsNotRunDetail), nameof(ShowsPlaceholders), nameof(ShowsDetail), nameof(PrimaryAction), nameof(HasPrimaryAction),
        nameof(ShortAgent),
    ];

    private bool _isExpanded;
    private bool _isPeeking;
    private RelayCommand? _toggleExpand;
    private RelayCommand? _cardSend;
    private bool _detailHooked;

    /// <summary>The card shows its details. A view preference only: it never changes the workflow.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            HookDetail();
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ShowsDetail));
                OnExpandedChanged();
                _canvas.OnExpansionChanged();
            }
        }
    }

    /// <summary>The pointer rests on the collapsed card, so a read-only peek shows what it would expand to.</summary>
    public bool IsPeeking
    {
        get => _isPeeking;
        set
        {
            HookDetail();
            if (SetProperty(ref _isPeeking, value))
            {
                OnPropertyChanged(nameof(ShowsDetail));
            }
        }
    }

    public bool ShowsDetail => IsExpanded || IsPeeking;

    public ICommand ToggleExpandCommand => _toggleExpand ??= new RelayCommand(() => IsExpanded = !IsExpanded);

    public string ExpandName => IsExpanded ? $"Collapse \"{Title}\"" : $"Expand \"{Title}\"";

    public bool ShowsPlaceholders => Proto.N1View.Placeholders;

    public DetailKind Detail => IsWaiting && DetailAttempt is { Pending: Pending.Question } ? DetailKind.Question
        : State is NodeState.Running or NodeState.RunningElsewhere or NodeState.Stopping ? DetailKind.Running
        : State == NodeState.Succeeded ? DetailKind.Result
        : State is NodeState.Failed or NodeState.Interrupted ? DetailKind.Failed
        : RunTask is { State: TaskState.Pending } ? DetailKind.Waits
        : DetailAttempt is null || RunTask is null && Problem is StartProblem.Waiting ? DetailKind.NotRun
        : DetailKind.Other;

    public bool IsRunningDetail => Detail == DetailKind.Running;

    public bool IsQuestionDetail => Detail == DetailKind.Question;

    public bool IsResultDetail => Detail == DetailKind.Result;

    public bool IsFailedDetail => Detail == DetailKind.Failed;

    public bool IsWaitsDetail => Detail == DetailKind.Waits;

    public bool IsNotRunDetail => Detail is DetailKind.NotRun or DetailKind.Other;

    public string NowHeading => Detail switch
    {
        DetailKind.Running => "Latest message",
        DetailKind.Question => "The agent asks",
        DetailKind.Result => "Result",
        DetailKind.Failed => "Why it stopped",
        DetailKind.Waits => "Starts after",
        _ => TaskFieldLabel,
    };

    /// <summary>The section's text, whole: the agent's latest message, its question, its result's first paragraph, or why it failed.</summary>
    public string? NowText => Detail switch
    {
        DetailKind.Running => DetailAttempt?.Activity.LastOrDefault(line => !line.IsTool)?.Text ?? "The agent has not written yet.",
        DetailKind.Question => DetailAttempt?.Pending is Pending.Question question ? question.Text : null,
        DetailKind.Result => FirstParagraph(ResultBlock.Prose(DetailAttempt?.Result)),
        DetailKind.Failed => RunDetail ?? DetailAttempt?.Detail ?? "The client stopped without a reason.",
        DetailKind.Waits => null,
        _ => FirstParagraph(Fields.Count > 0 ? Fields[0].Text : null) ?? "No instructions yet.",
    };

    /// <summary>A quiet line under the text: what the agent did and for how long.</summary>
    public string? NowMeta => Detail switch
    {
        DetailKind.Running when DetailAttempt is { } attempt => Join(Tools(attempt), $"for {RunText.Elapsed(DateTimeOffset.Now - attempt.RequestedAt)}"),
        DetailKind.Result when DetailAttempt is { EndedAt: { } ended } attempt => Join(Tools(attempt), $"took {RunText.Elapsed(ended - attempt.RequestedAt)}"),
        DetailKind.Failed when DetailAttempt is { EndedAt: { } ended } attempt => $"after {RunText.Elapsed(ended - attempt.RequestedAt)}",
        _ => null,
    };

    public IReadOnlyList<WaitItem> WaitsFor => RunTask is { State: TaskState.Pending } run
        ? [.. run.HeldBy.Select(id => _canvas.Nodes.FirstOrDefault(node => node.Id == id)).OfType<TaskNodeViewModel>()
            .Select(node => new WaitItem(node.Title, node.StatusLabel, node.State, node.Kind))]
        : [];

    public IReadOnlyList<ResultLink> ResultLinks => Detail == DetailKind.Result
        ? [new ResultLink("Report", "Opens the conversation at the result", OpenConversationCommand)]
        : [];

    public bool HasResultLinks => ResultLinks.Count > 0;

    /// <summary>"Codex · GPT-6.1-Sol · high", for a surface that shows the agent on one line.</summary>
    public string ShortAgent => AgentLabel;

    /// <summary>The one action the card offers beside Open conversation, or null.</summary>
    public string? PrimaryAction => IsRunOwned ? null : Detail switch
    {
        DetailKind.Running => "Stop",
        DetailKind.NotRun or DetailKind.Other or DetailKind.Failed or DetailKind.Result => "Run",
        _ => null,
    };

    public bool HasPrimaryAction => PrimaryAction is not null;

    public ICommand PrimaryCommand => Detail == DetailKind.Running ? CancelCommand : RunCommand;

    /// <summary>Sends the card's answer. A task a run owns answers through its conversation, which opens with the draft.</summary>
    public ICommand CardSendCommand => _cardSend ??= new RelayCommand(() =>
    {
        if (!IsRunOwned && SendCommand.CanExecute(null))
        {
            SendCommand.Execute(null);
        }
        else
        {
            OpenConversationCommand.Execute(null);
        }
    });

    partial void OnExpandedChanged();

    private string TaskFieldLabel => Fields.Count > 0 ? Fields[0].Spec.Label : "Task";

    private AttemptRecord? _detailAttempt;

    /// <summary>The attempt the card's details show: the run's attempt of the task while a run shows it, else its own latest.</summary>
    private AttemptRecord? DetailAttempt => _detailAttempt ?? _attempt;

    internal void RefreshDetail()
    {
        _detailAttempt = RunTask is { Attempt: { } attempt } && _canvas.Run is { } run ? _canvas.Runs.RunAttempt(run.Address, Id, attempt) : null;
        foreach (var property in DetailProperties)
        {
            OnPropertyChanged(property);
        }
    }

    internal bool IsDetailHooked => _detailHooked;

    private void HookDetail()
    {
        if (_detailHooked)
        {
            return;
        }

        _detailHooked = true;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(State) or nameof(StatusLabel) or nameof(LastAttempt) or nameof(RunTask) or nameof(IsWaiting)
                or nameof(Problem) or nameof(AgentLabel) or nameof(Preview))
            {
                RefreshDetail();
            }

            if (e.PropertyName == nameof(IsExpanded))
            {
                OnPropertyChanged(nameof(ExpandName));
                OnPropertyChanged(nameof(CardTipOrPeek));
            }
        };
        RefreshDetail();
    }

    private static string? Tools(AttemptRecord attempt) => attempt.Activity.Count(line => line.IsTool) switch
    {
        0 => null,
        1 => "1 tool call",
        var count => $"{count} tool calls",
    };

    private static string? Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } joined ? char.ToUpperInvariant(joined[0]) + joined[1..] : null;

    private static string? FirstParagraph(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => !part.StartsWith('#')) ?? text.Trim();
    }
}

public sealed partial class WorkflowCanvasViewModel
{
    private RelayCommand? _expandAll;

    /// <summary>Some card of the workflow is expanded, so the toolbar offers Collapse All.</summary>
    public bool AnyExpanded => Nodes.Any(node => node.IsExpanded);

    public string ExpandAllLabel => AnyExpanded ? "Collapse All" : "Expand All";

    private bool _isNarrow;

    public bool IsNarrow
    {
        get => _isNarrow;
        set => SetProperty(ref _isNarrow, value);
    }

    public ICommand ExpandAllCommand => _expandAll ??= new RelayCommand(() =>
    {
        var expand = !AnyExpanded;
        foreach (var node in Nodes)
        {
            node.IsExpanded = expand;
        }
    });

    private Avalonia.Threading.DispatcherTimer StartDetailTimer()
    {
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            foreach (var node in Nodes.Where(node => node.IsDetailHooked))
            {
                node.RefreshDetail();
            }
        };
        timer.Start();
        return timer;
    }

    /// <summary>Option B's margin notes, in the order the lane stacks them.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Proto.MarginNote> Notes { get; } = [];

    /// <summary>Raised after a card expands or collapses, so an option can lay the canvas out again.</summary>
    public event EventHandler? ExpansionChanged;

    private Avalonia.Threading.DispatcherTimer? _detailTimer;

    internal void OnExpansionChanged()
    {
        // The prototype reads an expanded card's attempt again each second, so a running task's latest message stays current.
        _detailTimer ??= StartDetailTimer();
        OnPropertyChanged(nameof(AnyExpanded));
        OnPropertyChanged(nameof(ExpandAllLabel));
        ExpansionChanged?.Invoke(this, EventArgs.Empty);
        Proto.N1Layout.Schedule(this);
    }
}

// THROWAWAY N1 PROTOTYPE: the option's view state on each node.
public sealed partial class TaskNodeViewModel
{
    private Vector _viewOffset;
    private Size _measuredSize = new(WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight);
    private string _tab = "now";
    private RelayCommand<string>? _selectTab;

    /// <summary>a: unfold in place; b: margin notes; c: uniform tabbed cards. IDEVELOP_N1_OPTION picks it.</summary>
    public static string Option { get; } = (Environment.GetEnvironmentVariable("IDEVELOP_N1_OPTION") ?? Proto.N1Defaults.Option).ToLowerInvariant();

    public static bool IsOptionA => Option == "a";

    public static bool IsOptionB => Option == "b";

    public static bool IsOptionC => Option == "c";

    /// <summary>The expanded container: the 320 px card and its two 10 px port gutters.</summary>
    public const double ExpandedContainerWidth = 340;

    public double CardContainerWidth => IsExpanded && !IsOptionB ? ExpandedContainerWidth : WorkflowCanvasViewModel.TaskCardWidth;

    public bool ShowsInlineDetail => IsExpanded && !IsOptionB;

    public bool ShowsTabs => IsExpanded && IsOptionC;

    public CornerRadius TintCorners => ShowsInlineDetail ? new CornerRadius(14, 14, 0, 0) : new CornerRadius(14);

    public string? ShownTab => IsOptionC ? _tab : null;

    public string Tab => _tab;

    public string TabGroup => "tabs-" + Id.Value.ToString("N");

    public bool IsNowTab => _tab == "now";

    public bool IsAgentTab => _tab == "agent";

    public bool IsLaterTab => _tab == "later";

    /// <summary>Option C's body is one height in every state, so every expanded card is the same size.</summary>
    public const double FixedBodyHeight = 226;

    public double DetailViewportHeight => IsOptionC ? FixedBodyHeight : double.NaN;

    public bool DetailShowsActions => !IsOptionC;

    public ICommand SelectTabCommand => _selectTab ??= new RelayCommand<string>(tab =>
    {
        _tab = tab ?? "now";
        OnPropertyChanged(nameof(ShownTab));
        OnPropertyChanged(nameof(Tab));
        OnPropertyChanged(nameof(IsNowTab));
        OnPropertyChanged(nameof(IsAgentTab));
        OnPropertyChanged(nameof(IsLaterTab));
    });

    /// <summary>How far the option's layout moves the card for the view. The workflow keeps its own position.</summary>
    public Vector ViewOffset
    {
        get => _viewOffset;
        set
        {
            if (SetProperty(ref _viewOffset, value))
            {
                OnPropertyChanged(nameof(DisplayLocation));
            }
        }
    }

    /// <summary>Where the card shows: its position plus the view's offset. A drag moves the position itself.</summary>
    public Point DisplayLocation
    {
        get => Location + _viewOffset;
        set => Location = value - _viewOffset;
    }

    public Size MeasuredSize
    {
        get => _measuredSize;
        set
        {
            if (SetProperty(ref _measuredSize, value))
            {
                _canvas.OnExpansionChanged();
            }
        }
    }

    internal WorkflowCanvasViewModel Canvas => _canvas;

    private Proto.CardPeek? _peek;

    /// <summary>Option B's card tooltip is the read-only peek; every other option keeps the plain tooltip.</summary>
    public object? CardTipOrPeek
    {
        get
        {
            if (!IsOptionB || IsRenaming || IsExpanded)
            {
                return CardTip;
            }

            HookDetail();
            return _peek ??= new Proto.CardPeek { DataContext = this };
        }
    }

    /// <summary>The card's container as it shows, ports included.</summary>
    public Rect DisplayRect => new(DisplayLocation, _measuredSize);

    partial void OnExpandedChanged()
    {
        OnPropertyChanged(nameof(CardContainerWidth));
        OnPropertyChanged(nameof(ShowsInlineDetail));
        OnPropertyChanged(nameof(ShowsTabs));
        OnPropertyChanged(nameof(TintCorners));
    }
}
