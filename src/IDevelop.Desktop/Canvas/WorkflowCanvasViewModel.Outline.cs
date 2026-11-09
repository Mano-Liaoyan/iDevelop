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

    /// <summary>The workflow's depth stages, in order. The Index lists them under the workflow's row, and Flow lays them out in rows.</summary>
    public ObservableCollection<WorkflowStage> Stages { get; } = [];

    /// <summary>The Index lists the workflow's stages under its row. Opening a workflow expands it, and the window remembers it.</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ExpandLabel));
                OnPropertyChanged(nameof(ShowsStages));
            }
        }
    }

    /// <summary>What the row's chevron does now, for a screen reader.</summary>
    public string ExpandLabel => $"{(IsExpanded ? "Fold" : "Unfold")} {Name}";

    /// <summary>The Index shows stage rows under the workflow's row.</summary>
    public bool ShowsStages => IsExpanded && Stages.Count > 0;

    /// <summary>The node's row shows in the Index: its workflow expands and its stage opens.</summary>
    internal void RevealInIndex(TaskNodeViewModel node)
    {
        IsExpanded = true;
        Stages.FirstOrDefault(stage => stage.Holds(node))?.Open();
    }

    private void ArrangeStages()
    {
        var stages = FlowLayout.Stages(Workflow);
        while (Stages.Count > stages.Count)
        {
            Stages.RemoveAt(Stages.Count - 1);
        }

        while (Stages.Count < stages.Count)
        {
            Stages.Add(new WorkflowStage(Stages.Count + 1));
        }

        for (var index = 0; index < stages.Count; index++)
        {
            Stages[index].Show([.. stages[index].Select(id => _nodes[id])]);
        }

        OnPropertyChanged(nameof(ShowsStages));
    }

    private void ArrangeOutline()
    {
        Arrange(Outline, [.. ReadingOrder(Workflow).Select(id => _nodes[id])], SelectedNode);
        ArrangeStages();
    }

    internal static void Arrange<T>(ObservableCollection<T> rows, IReadOnlyList<T> order, TaskNodeViewModel? selected)
        where T : class
    {
        if (rows.SequenceEqual(order))
        {
            return;
        }

        // A list forgets its selection when the selected row leaves it, even to come straight back, and tells the canvas.
        // So the selected row stays, and the other rows take their places around it.
        var stays = selected is T row && rows.Contains(row) && order.Contains(row) ? row : null;
        foreach (var other in rows.Where(other => other != stays).ToList())
        {
            rows.Remove(other);
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
