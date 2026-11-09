using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// How a workflow's nodes read by depth: the Index's stages now, and Flow's rows with their widths later. A pure function, so saved
/// positions never change.
/// </summary>
public static class FlowLayout
{
    /// <summary>
    /// The stages in depth order. A node's depth is the longest path from a node with no predecessor, along the dependency connections,
    /// with a back edge of a depth-first walk ignored. Inside a stage the nodes stand by saved Y, then saved X.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TaskId>> Stages(Workflow workflow)
    {
        var dependencies = workflow.Connections.Where(connection => connection.Value.Blocks()).Select(connection => connection.Key).ToList();
        var next = dependencies.ToLookup(key => key.From, key => key.To);
        var hasPredecessor = dependencies.Select(key => key.To).ToHashSet();
        Dictionary<TaskId, bool> walked = [];
        HashSet<(TaskId From, TaskId To)> kept = [];

        void Walk(TaskId id)
        {
            walked[id] = false;
            foreach (var to in next[id].Where(to => !walked.TryGetValue(to, out var done) || done))
            {
                kept.Add((id, to));
                if (!walked.ContainsKey(to))
                {
                    Walk(to);
                }
            }

            walked[id] = true;
        }

        var ordered = workflow.Tasks.Keys.OrderBy(id => workflow.Positions[id].Y).ThenBy(id => workflow.Positions[id].X).ThenBy(id => id).ToList();
        foreach (var id in ordered.Where(id => !hasPredecessor.Contains(id)).Concat(ordered).Where(id => !walked.ContainsKey(id)))
        {
            if (!walked.ContainsKey(id))
            {
                Walk(id);
            }
        }

        var before = kept.ToLookup(edge => edge.To, edge => edge.From);
        Dictionary<TaskId, int> depths = [];
        int Depth(TaskId id)
        {
            if (!depths.TryGetValue(id, out var depth))
            {
                depths[id] = depth = before[id].Select(Depth).DefaultIfEmpty(-1).Max() + 1;
            }

            return depth;
        }

        return [.. ordered.GroupBy(Depth).OrderBy(stage => stage.Key).Select(stage => (IReadOnlyList<TaskId>)[.. stage])];
    }
}
