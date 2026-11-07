using System.Collections.ObjectModel;
using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Sidebar;

/// <summary>
/// The left area's Activity list: every node in every open project that waits, failed, or runs, so a person sees who needs
/// them and what the agents do now without a click. It only reads the nodes, and it holds no state of its own but how
/// many rows it shows.
/// </summary>
public sealed class ActivityViewModel : ObservableObject
{
    /// <summary>The rows a list shows before Show all.</summary>
    public const int NormalLimit = 3;

    private readonly Func<IReadOnlyList<ProjectViewModel>> _projects;
    private readonly Action<TaskNodeViewModel> _open;
    private readonly Dictionary<TaskNodeViewModel, ActivityRow> _rows = [];
    private List<ActivityRow> _ordered = [];
    private bool _isOpenWhileCompact;

    /// <param name="projects">The open projects, in their order.</param>
    /// <param name="open">Shows a node and what it needs from the person.</param>
    public ActivityViewModel(Func<IReadOnlyList<ProjectViewModel>> projects, Action<TaskNodeViewModel> open)
    {
        _projects = projects;
        _open = open;
        ShowAllCommand = new RelayCommand(() => IsShowingAll = true);
    }

    /// <summary>The rows in view: waiting, then failed, then running, each in project order and then the node list's order.</summary>
    public ObservableCollection<ActivityRow> Rows { get; } = [];

    public bool HasRows => _ordered.Count > 0;

    /// <summary>"3 nodes", the whole list's size.</summary>
    public int Count => _ordered.Count;

    /// <summary>How many rows need the person's word, as <see cref="Theme.StateCounts"/> counts them, which the heading shows apart from what failed.</summary>
    public int WaitingCount => _ordered.Count(row => row.Node.State == NodeState.InputRequired && row.Node.WaitReason != WaitReason.Interrupted);

    /// <summary>How many rows iDevelop's closing stopped.</summary>
    public int InterruptedCount => _ordered.Count(row => row.Node.WaitReason == WaitReason.Interrupted);

    /// <summary>How many rows failed.</summary>
    public int FailedCount => _ordered.Count(row => row.Node.State == NodeState.Failed);

    /// <summary>How many rows run. The four counts add up to <see cref="Count"/>.</summary>
    public int RunningCount => _ordered.Count(row => row.Node.State == NodeState.Running);

    public bool HasWaiting => WaitingCount > 0;

    public bool HasInterrupted => InterruptedCount > 0;

    public bool HasFailed => FailedCount > 0;

    public bool HasRunning => RunningCount > 0;

    /// <summary>How many rows the list shows before Show all.</summary>
    internal int Limit
    {
        get;
        set
        {
            field = value;
            ShowRows();
        }
    } = NormalLimit;

    public bool IsShowingAll
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                ShowRows();
            }
        }
    }

    /// <summary>The person folded the list down to its heading. The window remembers it.</summary>
    public bool IsFolded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaiseFold();
            }
        }
    }

    /// <summary>
    /// The panel is short, so the list starts folded down to its heading, whatever the person chose for a taller panel. The
    /// heading's toggle still opens it, and that choice lasts until the panel grows.
    /// </summary>
    internal bool IsCompact
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                _isOpenWhileCompact = false;
                RaiseFold();
            }
        }
    }

    public bool IsOpen
    {
        get => IsCompact ? _isOpenWhileCompact : !IsFolded;
        set
        {
            if (IsCompact)
            {
                _isOpenWhileCompact = value;
                RaiseFold();
            }
            else
            {
                IsFolded = !value;
            }
        }
    }

    /// <summary>What the heading's toggle does now, for a screen reader and the tooltip.</summary>
    public string FoldLabel => IsOpen ? "Hide Activity" : "Show Activity";

    public bool HasMore => !IsShowingAll && _ordered.Count > Limit;

    public string ShowAllLabel => $"Show all {_ordered.Count}";

    /// <summary>Several projects are open, and the list covers them all.</summary>
    public bool CoversSeveralProjects
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public ICommand ShowAllCommand { get; }

    /// <summary>The nodes in <paramref name="state"/>, in list order.</summary>
    internal IEnumerable<TaskNodeViewModel> InState(NodeState state) => _ordered.Where(row => row.Node.State == state).Select(row => row.Node);

    /// <summary>Reads every node again.</summary>
    internal void Refresh()
    {
        var projects = _projects();
        var nodes = projects.SelectMany(project => project.Workflows).SelectMany(canvas => canvas.Outline)
            .Where(node => GroupOf(node) is not null)
            .OrderBy(node => GroupOf(node))
            .ToList();
        // The project's name says whose a row is while more than one project is open.
        var showsProject = projects.Count > 1;
        CoversSeveralProjects = showsProject;
        foreach (var gone in _rows.Keys.Except(nodes).ToList())
        {
            _rows.Remove(gone);
        }

        _ordered = [.. nodes.Select(node =>
        {
            if (!_rows.TryGetValue(node, out var row))
            {
                _rows.Add(node, row = new ActivityRow(node, _open));
            }

            row.Read(showsProject);
            return row;
        })];
        if (_ordered.Count <= Limit)
        {
            IsShowingAll = false;
        }

        ShowRows();
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(InterruptedCount));
        OnPropertyChanged(nameof(HasInterrupted));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowAllLabel));
    }

    private void RaiseFold()
    {
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(FoldLabel));
    }

    private static int? GroupOf(TaskNodeViewModel node) => node.State switch
    {
        NodeState.InputRequired => 0,
        NodeState.Failed => 1,
        NodeState.Running => 2,
        _ => null,
    };

    /// <summary>
    /// A short list of <paramref name="limit"/> rows, in list order: the first row of each need, so that no kind of need hides
    /// behind Show all, and then the rows that follow. The first failure always stays in view.
    /// </summary>
    internal static List<T> Shortened<T, TNeed>(IReadOnlyList<T> rows, int limit, Func<T, TNeed> need, Func<T, bool> failed)
    {
        var failure = rows.Index().Where(row => failed(row.Item)).Select(row => row.Index).Take(1);
        var firsts = rows.Index().DistinctBy(row => need(row.Item)).Select(row => row.Index);
        return [.. failure.Union(firsts).Union(Enumerable.Range(0, rows.Count)).Take(limit).Order().Select(index => rows[index])];
    }

    // The rows that stay keep their controls, so a keyboard focus on one survives a change to another.
    private void ShowRows()
    {
        var shown = IsShowingAll ? _ordered : Shortened(_ordered, Limit, row => (row.Node.State, row.Node.WaitReason), row => row.Node.State == NodeState.Failed);
        if (!Rows.SequenceEqual(shown))
        {
            foreach (var row in Rows.Except(shown).ToList())
            {
                Rows.Remove(row);
            }

            for (var index = 0; index < shown.Count; index++)
            {
                var at = Rows.IndexOf(shown[index]);
                if (at < 0)
                {
                    Rows.Insert(index, shown[index]);
                }
                else if (at != index)
                {
                    Rows.Move(at, index);
                }
            }
        }

        OnPropertyChanged(nameof(HasMore));
    }
}

/// <summary>One node in the Activity list: its title, and why it is listed.</summary>
public sealed class ActivityRow : ObservableObject
{
    private readonly Action<TaskNodeViewModel> _open;

    internal ActivityRow(TaskNodeViewModel node, Action<TaskNodeViewModel> open)
    {
        Node = node;
        _open = open;
        OpenCommand = new RelayCommand(() => _open(Node));
    }

    public TaskNodeViewModel Node { get; }

    public ICommand OpenCommand { get; }

    /// <summary>"Question · Payment webhook hardening · Claude Code", with the project's name after the workflow's while several projects are open.</summary>
    public string Detail => string.Join(" · ", new[] { Reason, Place, Node.AgentClientName }.OfType<string>());

    /// <summary>Why the node is listed, in the state's own word.</summary>
    public string Reason
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(Tip));
            }
        }
    } = "";

    /// <summary>The row's tooltip: the whole title, which the row may cut, then the detail.</summary>
    public string Tip => $"{Node.Title}\n{Detail}";

    /// <summary>The node's workflow, and its project's name after it while several projects are open, so a cut takes the project first.</summary>
    public string Place
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(Tip));
            }
        }
    } = "";

    internal void Read(bool showsProject)
    {
        Reason = ReasonOf(Node);
        Place = showsProject ? $"{Node.Canvas.Name} · {Node.Canvas.Project.Name}" : Node.Canvas.Name;
    }

    // "Input required · Question" names the reason after the state, which the glyph already shows.
    private static string ReasonOf(TaskNodeViewModel node) => node.State switch
    {
        NodeState.Running => OneLine(node.Latest) ?? node.StatusLabel,
        NodeState.InputRequired when node.StatusLabel.Split(" · ") is [_, var why] => why,
        _ => node.StatusLabel,
    };

    internal static string? OneLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
