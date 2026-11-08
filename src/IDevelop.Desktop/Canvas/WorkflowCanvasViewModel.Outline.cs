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

    /// <summary>The group headers of the Nodes section, in its order (ADR 0072).</summary>
    public IReadOnlyList<NodeGroup> Groups => field ??= [.. Enum.GetValues<NodeGroupKind>().Select(kind => new NodeGroup(kind, OnGroupFolded))];

    /// <summary>
    /// The rows of the left tree under an expanded workflow: for each group that holds a node ("Needs you", "Running",
    /// "Stale", "Not started", "Done"), its header and, unless it is folded, its nodes in reading order. The tree shows the selected
    /// or open node's row: its group opens and lists all its rows, whatever its size (ADR 0070).
    /// </summary>
    public ObservableCollection<object> TreeRows { get; } = [];

    /// <summary>
    /// The tree's rows in their order. The group of <paramref name="reveal"/> opens. A folded group lists no rows.
    /// </summary>
    internal static IReadOnlyList<object> TreeOrder(IReadOnlyList<TaskNodeViewModel> outline, IReadOnlyList<NodeGroup> groups, TaskNodeViewModel? reveal = null)
    {
        var members = outline.ToLookup(NodeGroup.Of);
        foreach (var group in groups)
        {
            group.Count = members[group.Kind].Count();
        }

        if (reveal is not null)
        {
            groups.First(group => group.Kind == NodeGroup.Of(reveal)).Open();
        }

        var rows = new List<object>(outline.Count + groups.Count);
        foreach (var group in groups.Where(group => group.Count > 0))
        {
            rows.Add(group);
            if (!group.IsFolded)
            {
                rows.AddRange(members[group.Kind]);
            }

            group.ListsRows = !group.IsFolded;
        }

        return rows;
    }

    /// <summary>The node whose group the tree last opened, and that group, so a group that the person folds stays folded.</summary>
    private (TaskNodeViewModel Node, NodeGroupKind Group)? _revealed;

    /// <summary>
    /// Lists the tree's rows again, with a row for <paramref name="keep"/>, which is about to be selected, or else for the selected
    /// node. The tree opens a node's group once for each selection, and again when the node moves to another group.
    /// </summary>
    private void ArrangeTreeRows(TaskNodeViewModel? keep = null)
    {
        var shown = keep ?? SelectedNode;
        var reveal = shown;
        if (reveal is not null && _revealed == (reveal, NodeGroup.Of(reveal)))
        {
            reveal = null;
        }
        else if (reveal is not null)
        {
            _revealed = (reveal, NodeGroup.Of(reveal));
        }

        _arrangingTree = true;
        try
        {
            Arrange(TreeRows, TreeOrder(Outline, Groups, reveal));
        }
        finally
        {
            _arrangingTree = false;
        }
    }

    private bool _arrangingTree;

    // A group that opens while the tree arranges its rows needs no second pass.
    private void OnGroupFolded()
    {
        if (!_arrangingTree)
        {
            ArrangeTreeRows();
        }
    }

    private void ArrangeOutline()
    {
        Arrange(Outline, [.. ReadingOrder(Workflow).Select(id => _nodes[id])]);
        ArrangeTreeRows();
    }

    private void Arrange<T>(ObservableCollection<T> rows, IReadOnlyList<T> order)
        where T : class
    {
        if (rows.SequenceEqual(order))
        {
            return;
        }

        // A list forgets its selection when the selected row leaves it, even to come straight back, and tells the canvas.
        // So the selected row stays, and the other rows take their places around it.
        var stays = SelectedNode is T selected && rows.Contains(selected) && order.Contains(selected) ? selected : null;
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
