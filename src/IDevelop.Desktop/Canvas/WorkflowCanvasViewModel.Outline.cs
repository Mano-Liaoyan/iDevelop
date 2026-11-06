using System.Collections.ObjectModel;
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

    /// <summary>Each task's step: 1 with nothing before it, otherwise one after the last task it depends on. A cycle's tasks keep no step of their own.</summary>
    internal static IReadOnlyDictionary<TaskId, int> Steps(Workflow workflow)
    {
        var before = workflow.Connections.Where(connection => connection.Value.Blocks()).Select(connection => connection.Key).ToLookup(key => key.To, key => key.From);
        var steps = new Dictionary<TaskId, int>();
        foreach (var id in ReadingOrder(workflow))
        {
            steps[id] = before[id].Select(from => steps.GetValueOrDefault(from)).DefaultIfEmpty(0).Max() + 1;
        }

        return steps;
    }

    private void ArrangeOutline()
    {
        foreach (var (id, step) in Steps(Workflow))
        {
            _nodes[id].Step = step;
        }

        List<TaskNodeViewModel> order = [.. ReadingOrder(Workflow).Select(id => _nodes[id])];
        if (Outline.SequenceEqual(order))
        {
            return;
        }

        // A list forgets its selection when the selected row leaves it, even to come straight back, and tells the canvas.
        // So the selected row stays, and the other rows take their places around it.
        var stays = SelectedNode is { } selected && order.Contains(selected) ? selected : null;
        foreach (var row in Outline.Where(row => row != stays).ToList())
        {
            Outline.Remove(row);
        }

        for (var index = 0; index < order.Count; index++)
        {
            if (order[index] != stays)
            {
                Outline.Insert(index, order[index]);
            }
        }
    }
}
