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
    /// <summary>The rows a list shows before Show all, and at the smallest windows.</summary>
    public const int NormalLimit = 5;

    public const int SmallLimit = 3;

    private readonly Func<IReadOnlyList<ProjectViewModel>> _projects;
    private readonly Action<TaskNodeViewModel> _open;
    private readonly Dictionary<TaskNodeViewModel, ActivityRow> _rows = [];
    private List<ActivityRow> _ordered = [];

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

    public int Limit
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ShowRows();
            }
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

    public bool HasMore => !IsShowingAll && _ordered.Count > Limit;

    public string ShowAllLabel => $"Show all {_ordered.Count}";

    public ICommand ShowAllCommand { get; }

    /// <summary>The nodes that wait for the person or failed, in list order.</summary>
    internal IEnumerable<TaskNodeViewModel> Attention => _ordered.Where(row => row.Node.State is NodeState.InputRequired or NodeState.Failed).Select(row => row.Node);

    /// <summary>Reads every node again.</summary>
    internal void Refresh()
    {
        var projects = _projects();
        var showsProject = projects.Count > 1;
        var nodes = projects.SelectMany(project => project.Workflows).SelectMany(canvas => canvas.Outline)
            .Where(node => GroupOf(node) is not null)
            .OrderBy(node => GroupOf(node))
            .ToList();
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
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowAllLabel));
    }

    private static int? GroupOf(TaskNodeViewModel node) => node.State switch
    {
        NodeState.InputRequired => 0,
        NodeState.Failed => 1,
        NodeState.Running => 2,
        _ => null,
    };

    // The rows that stay keep their controls, so a keyboard focus on one survives a change to another.
    private void ShowRows()
    {
        var shown = IsShowingAll ? _ordered : _ordered.Take(Limit).ToList();
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

    /// <summary>"Question · Payment webhook hardening", with the project's name first while several projects are open.</summary>
    public string Detail
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    internal void Read(bool showsProject) => Detail = Describe(Node, showsProject);

    internal static string Describe(TaskNodeViewModel node, bool showsProject)
    {
        var parts = new[] { showsProject ? node.Canvas.Project.Name : null, Reason(node), node.Canvas.Name };
        return string.Join(" · ", parts.OfType<string>());
    }

    // "Input required · question" names the reason after the state, which the glyph already shows.
    private static string Reason(TaskNodeViewModel node) => node.State switch
    {
        NodeState.Running => OneLine(node.Latest) ?? node.StatusLabel,
        NodeState.InputRequired when node.StatusLabel.Split(" · ") is [_, var why] => char.ToUpperInvariant(why[0]) + why[1..],
        _ => node.StatusLabel,
    };

    private static string? OneLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
