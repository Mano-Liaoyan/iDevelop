using System.Collections.ObjectModel;
using System.Windows.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Sidebar;

/// <summary>
/// The Index's Inbox: every node in every open project that waits, failed, or runs, in three groups, so a person sees who needs them
/// and what the agents do now without a click. It only reads the nodes, and it holds no state of its own.
/// </summary>
public sealed class ActivityViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<ProjectViewModel>> _projects;
    private readonly Action<TaskNodeViewModel> _open;
    private readonly Dictionary<TaskNodeViewModel, ActivityRow> _rows = [];

    /// <param name="projects">The open projects, in their order.</param>
    /// <param name="open">Shows a node and what it needs from the person.</param>
    public ActivityViewModel(Func<IReadOnlyList<ProjectViewModel>> projects, Action<TaskNodeViewModel> open)
    {
        _projects = projects;
        _open = open;
    }

    /// <summary>Every row: waiting, then failed, then running, each in project order and then the node list's order.</summary>
    public ObservableCollection<ActivityRow> Rows { get; } = [];

    /// <summary>The Inbox's three groups, in their order. An empty group shows nothing.</summary>
    public IReadOnlyList<InboxGroup> Groups { get; } = [new("Needs you"), new("Failed"), new("Running")];

    public bool HasRows => Rows.Count > 0;

    /// <summary>How many rows need the person, as <see cref="Theme.StateCounts"/> counts them.</summary>
    public int WaitingCount => Rows.Count(row => row.Node.State == NodeState.InputRequired);

    /// <summary>How many rows failed.</summary>
    public int FailedCount => Rows.Count(row => row.Node.State == NodeState.Failed);

    /// <summary>How many rows run.</summary>
    public int RunningCount => Rows.Count(row => row.Node.State == NodeState.Running);

    /// <summary>What the Inbox counts: the rows that need the person and the rows that failed.</summary>
    public int InboxCount => WaitingCount + FailedCount;

    public bool HasInbox => InboxCount > 0;

    /// <summary>The nodes in <paramref name="state"/>, in list order.</summary>
    internal IEnumerable<TaskNodeViewModel> InState(NodeState state) => Rows.Where(row => row.Node.State == state).Select(row => row.Node);

    /// <summary>Reads every node again. The rows that stay keep their controls, so a keyboard focus on one survives a change to another.</summary>
    internal void Refresh()
    {
        var nodes = _projects().SelectMany(project => project.Workflows).SelectMany(canvas => canvas.Outline)
            .Where(node => GroupOf(node) is not null)
            .OrderBy(node => GroupOf(node))
            .ToList();
        foreach (var gone in _rows.Keys.Except(nodes).ToList())
        {
            _rows.Remove(gone);
        }

        var ordered = nodes.Select(node =>
        {
            if (!_rows.TryGetValue(node, out var row))
            {
                _rows.Add(node, row = new ActivityRow(node, _open));
            }

            row.Read();
            return row;
        }).ToList();
        Sync(Rows, ordered);
        for (var index = 0; index < Groups.Count; index++)
        {
            Groups[index].Show([.. ordered.Where(row => GroupOf(row.Node) == index)]);
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(InboxCount));
        OnPropertyChanged(nameof(HasInbox));
    }

    private static int? GroupOf(TaskNodeViewModel node) => node.State switch
    {
        NodeState.InputRequired => 0,
        NodeState.Failed => 1,
        NodeState.Running => 2,
        _ => null,
    };

    internal static void Sync<T>(ObservableCollection<T> shown, IReadOnlyList<T> rows)
    {
        if (shown.SequenceEqual(rows))
        {
            return;
        }

        foreach (var row in shown.Except(rows).ToList())
        {
            shown.Remove(row);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var at = shown.IndexOf(rows[index]);
            if (at < 0)
            {
                shown.Insert(index, rows[index]);
            }
            else if (at != index)
            {
                shown.Move(at, index);
            }
        }
    }
}

/// <summary>One group of the Inbox: its heading and its rows.</summary>
public sealed class InboxGroup(string title) : ObservableObject
{
    public string Title { get; } = title;

    public ObservableCollection<ActivityRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public int Count => Rows.Count;

    internal void Show(IReadOnlyList<ActivityRow> rows)
    {
        ActivityViewModel.Sync(Rows, rows);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Count));
    }
}

/// <summary>One node in the Inbox: its title, and why it is listed.</summary>
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

    /// <summary>"Question · harbor-api › Payment webhook hardening · Claude Code": why the node is listed, where it is, and its agent.</summary>
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

    /// <summary>The row's tooltip: the whole title, then the detail.</summary>
    public string Tip => $"{Node.Title}\n{Detail}";

    /// <summary>"harbor-api › Payment webhook hardening": where the node is.</summary>
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

    /// <summary>"Question ·" before the row's place while the node needs the person, or null.</summary>
    public string? ReasonLead
    {
        get;
        private set => SetProperty(ref field, value);
    }

    internal void Read()
    {
        Reason = ReasonOf(Node);
        Place = $"{Node.Canvas.Project.Name} › {Node.Canvas.Name}";
        ReasonLead = Node.State == NodeState.InputRequired ? $"{Reason} ·" : null;
    }

    // "Needs you · Question" names the reason after the state, which the glyph already shows.
    private static string ReasonOf(TaskNodeViewModel node) => node.State switch
    {
        NodeState.Running => OneLine(node.Latest) ?? node.StatusLabel,
        NodeState.InputRequired when node.StatusLabel.Split(" · ") is [_, var why] => why,
        _ => node.StatusLabel,
    };

    internal static string? OneLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
