using System.Collections.ObjectModel;
using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>The sidebar's list of tasks, in the order the workflow reads rather than the order its file holds them.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>
    /// Every task in reading order: each after the tasks it depends on, and of the tasks free to come next, the highest
    /// on the canvas, then the leftmost. A context connection orders nothing, because context connections may form cycles.
    /// </summary>
    public ObservableCollection<TaskNodeViewModel> Outline { get; } = [];

    private static IReadOnlyList<TaskId> ReadingOrder(Workflow workflow)
    {
        var dependencies = workflow.Connections.Where(connection => connection.Value.Blocks()).Select(connection => connection.Key).ToList();
        var before = dependencies.ToLookup(key => key.To);
        var waitsFor = workflow.Tasks.Keys.ToDictionary(id => id, id => before[id].Count());
        var next = dependencies.ToLookup(key => key.From, key => key.To);
        var ready = new PriorityQueue<TaskId, (double Y, double X, TaskId Id)>();
        void Free(TaskId id) => ready.Enqueue(id, (workflow.Positions[id].Y, workflow.Positions[id].X, id));

        foreach (var id in waitsFor.Where(entry => entry.Value == 0).Select(entry => entry.Key))
        {
            Free(id);
        }

        var order = new List<TaskId>(workflow.Tasks.Count);
        while (ready.TryDequeue(out var id, out _))
        {
            order.Add(id);
            foreach (var after in next[id])
            {
                waitsFor[after]--;
                if (waitsFor[after] == 0)
                {
                    Free(after);
                }
            }
        }

        return order;
    }

    /// <summary>A workflow with more nodes than this lists the ones that need the person first and folds the rest after this many rows.</summary>
    public const int TreeRowLimit = 12;

    /// <summary>
    /// The node rows of the left tree. A workflow of up to <see cref="TreeRowLimit"/> nodes lists all of them in reading order.
    /// A larger one lists first the nodes that need the person or run (waiting, failed, interrupted, running), then the rest up
    /// to <see cref="TreeRowLimit"/> rows, each part in reading order, until the person asks for every row. The selected node
    /// always has its row.
    /// </summary>
    public ObservableCollection<TaskNodeViewModel> TreeRows { get; } = [];

    /// <summary>The left tree lists every node of a large workflow.</summary>
    public bool ShowsAllRows
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowAllLabel));
                ArrangeTreeRows();
            }
        }
    }

    /// <summary>The workflow has more nodes than the tree lists at first, so the tree offers a row that lists them all.</summary>
    public bool HasFoldedRows => Outline.Count > TreeRowLimit;

    /// <summary>"Show all 50 nodes", or "Show fewer nodes" while the tree lists them all.</summary>
    public string ShowAllLabel => ShowsAllRows ? "Show fewer nodes" : $"Show all {Outline.Count} nodes";

    public ICommand ToggleAllRowsCommand => field ??= new RelayCommand(() => ShowsAllRows = !ShowsAllRows);

    /// <summary>The node needs the person or runs, so a large workflow's tree lists it first.</summary>
    private static bool LeadsTree(TaskNodeViewModel node) => node.IsWaiting || node.State is NodeState.Failed or NodeState.Running;

    /// <summary>The tree's rows in their order, with a row for each node in <paramref name="keep"/>.</summary>
    internal static IReadOnlyList<TaskNodeViewModel> TreeOrder(IReadOnlyList<TaskNodeViewModel> outline, bool all, params TaskNodeViewModel?[] keep)
    {
        if (all || outline.Count <= TreeRowLimit)
        {
            return outline;
        }

        var first = outline.Where(LeadsTree).ToList();
        var rest = outline.Where(node => !LeadsTree(node)).ToList();
        var shown = rest.Take(Math.Max(0, TreeRowLimit - first.Count)).ToHashSet();
        return [.. first, .. rest.Where(node => shown.Contains(node) || keep.Contains(node))];
    }

    /// <summary>Lists the tree's rows again, with a row for <paramref name="keep"/> too, which is about to be selected.</summary>
    private void ArrangeTreeRows(TaskNodeViewModel? keep = null)
    {
        Arrange(TreeRows, TreeOrder(Outline, ShowsAllRows, SelectedNode, keep));
        OnPropertyChanged(nameof(HasFoldedRows));
        OnPropertyChanged(nameof(ShowAllLabel));
    }

    private void ArrangeOutline()
    {
        Arrange(Outline, [.. ReadingOrder(Workflow).Select(id => _nodes[id])]);
        ArrangeTreeRows();
    }

    private void Arrange(ObservableCollection<TaskNodeViewModel> rows, IReadOnlyList<TaskNodeViewModel> order)
    {
        if (rows.SequenceEqual(order))
        {
            return;
        }

        // A list forgets its selection when the selected row leaves it, even to come straight back, and tells the canvas.
        // So the selected row stays, and the other rows take their places around it.
        var stays = SelectedNode is { } selected && order.Contains(selected) ? selected : null;
        foreach (var row in rows.Where(row => row != stays).ToList())
        {
            rows.Remove(row);
        }

        for (var index = 0; index < order.Count; index++)
        {
            if (order[index] != stays)
            {
                rows.Insert(index, order[index]);
            }
        }
    }
}
