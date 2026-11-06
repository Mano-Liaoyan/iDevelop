using System.Collections.ObjectModel;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Mvvm;

namespace IDevelop.Desktop.Sidebar;

/// <summary>A node or a workflow that Find lists.</summary>
/// <param name="Canvas">The workflow, or the workflow that holds the node.</param>
/// <param name="Node">The node, or null for a workflow.</param>
public sealed record FindResult(WorkflowCanvasViewModel Canvas, TaskNodeViewModel? Node)
{
    public string Name => Node?.Title ?? Canvas.Name;

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
            }
        }
    } = "";

    /// <summary>A query replaces the Activity list and the project tree with the results.</summary>
    public bool IsActive => Query.Trim().Length > 0;

    public bool HasNoMatch => IsActive && Results.Count == 0;

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

    /// <summary>Shows the result under <see cref="Index"/> and clears the query. It returns whether there was one.</summary>
    internal bool Choose()
    {
        if (Index < 0 || Index >= Results.Count)
        {
            return false;
        }

        var result = Results[Index];
        Query = "";
        _choose(result);
        return true;
    }

    internal void Clear() => Query = "";

    private void Search()
    {
        var query = Query.Trim();
        Results.Clear();
        if (query.Length > 0)
        {
            bool Matches(string text) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            foreach (var project in _projects())
            {
                foreach (var canvas in project.Workflows)
                {
                    if (Matches(canvas.Name) || Matches(project.Name))
                    {
                        Results.Add(new FindResult(canvas, null));
                    }

                    foreach (var node in canvas.Outline.Where(node => Matches(node.Title)))
                    {
                        Results.Add(new FindResult(canvas, node));
                    }
                }
            }
        }

        Index = Results.Count > 0 ? 0 : -1;
    }
}
