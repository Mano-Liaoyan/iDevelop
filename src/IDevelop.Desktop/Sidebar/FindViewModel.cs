using System.Collections.ObjectModel;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Sidebar;

/// <summary>A node or a workflow that Find lists.</summary>
/// <param name="Canvas">The workflow, or the workflow that holds the node.</param>
/// <param name="Node">The node, or null for a workflow.</param>
/// <param name="Query">The words the name matched.</param>
public sealed record FindResult(WorkflowCanvasViewModel Canvas, TaskNodeViewModel? Node, string Query = "")
{
    public string Name => Node?.Title ?? Canvas.Name;

    private int MatchAt => Query.Length == 0 ? -1 : Name.IndexOf(Query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The name before the match, or the whole name when the match is in the project's name.</summary>
    public string BeforeMatch => MatchAt < 0 ? Name : Name[..MatchAt];

    public string Match => MatchAt < 0 ? "" : Name.Substring(MatchAt, Query.Length);

    public string AfterMatch => MatchAt < 0 ? "" : Name[(MatchAt + Query.Length)..];

    public bool IsNode => Node is not null;

    public bool IsWorkflow => Node is null;

    /// <summary>"Project › Workflow" for a node, and the project for a workflow.</summary>
    public string Place => Node is null ? Canvas.Project.Name : $"{Canvas.Project.Name} › {Canvas.Name}";

    public NodeKind Kind => Node?.Kind ?? default;
}

/// <summary>The left area's Find box: a query over the node titles, workflow names, and project names of every open project.</summary>
public sealed class FindViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<ProjectViewModel>> _projects;
    private readonly Action<FindResult> _choose;

    /// <param name="projects">The open projects, in their order.</param>
    /// <param name="choose">Shows the chosen workflow or node.</param>
    public FindViewModel(Func<IReadOnlyList<ProjectViewModel>> projects, Action<FindResult> choose)
    {
        _projects = projects;
        _choose = choose;
    }

    public string Query
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                Search();
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(HasNoMatch));
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ResultsLabel));
            }
        }
    } = "";

    /// <summary>A query replaces the Activity list and the project tree with the results.</summary>
    public bool IsActive => Query.Trim().Length > 0;

    public bool HasNoMatch => IsActive && Results.Count == 0;

    public bool HasResults => Results.Count > 0;

    /// <summary>"1 workflow · 3 nodes": how many of each the query found.</summary>
    public string ResultsLabel
    {
        get
        {
            var (flows, nodes) = (Results.Count(result => result.IsWorkflow), Results.Count(result => result.IsNode));
            return string.Join(" · ", new[]
            {
                flows switch { 0 => null, 1 => "1 workflow", _ => $"{flows} workflows" },
                nodes switch { 0 => null, 1 => "1 node", _ => $"{nodes} nodes" },
            }.OfType<string>());
        }
    }

    public ObservableCollection<FindResult> Results { get; } = [];

    /// <summary>The result Enter chooses. The first result takes it after each change of the query.</summary>
    public int Index
    {
        get;
        set => SetProperty(ref field, value);
    } = -1;

    /// <summary>The key hint in the box, such as Ctrl+K.</summary>
    public static string Hint => CanvasKeys.Hint(CanvasKeys.Find);

    internal void Move(int step)
    {
        if (Results.Count > 0)
        {
            Index = Math.Clamp(Index + step, 0, Results.Count - 1);
        }
    }

    /// <summary>Shows the result under <see cref="Index"/> and clears the query. It returns the result, or null when there was none.</summary>
    internal FindResult? Choose()
    {
        if (Index < 0 || Index >= Results.Count)
        {
            return null;
        }

        var result = Results[Index];
        Query = "";
        _choose(result);
        return result;
    }

    internal void Clear() => Query = "";

    private void Search()
    {
        var query = Query.Trim();
        Results.Clear();
        if (query.Length > 0)
        {
            bool Matches(string text) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            var canvases = _projects().SelectMany(project => project.Workflows).ToList();
            foreach (var canvas in canvases.Where(canvas => Matches(canvas.Name) || Matches(canvas.Project.Name)))
            {
                Results.Add(new FindResult(canvas, null, query));
            }

            foreach (var canvas in canvases)
            {
                foreach (var node in canvas.Outline.Where(node => Matches(node.Title)))
                {
                    Results.Add(new FindResult(canvas, node, query));
                }
            }
        }

        Index = Results.Count > 0 ? 0 : -1;
    }
}
