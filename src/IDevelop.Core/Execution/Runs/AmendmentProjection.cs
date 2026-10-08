using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// How the workflow document follows a run's recorded amendments. The journal records an amendment first and stays the
/// authority: the document receives the amendments' edits only while it holds a revision of the run's amendment chain,
/// such as the one the latest amendment expected, and otherwise shows the difference. Layout is no part of a revision, so a
/// moved card never stands in the way, and undoing the projected edit in the document revokes nothing in the run.
/// </summary>
internal abstract record AmendmentProjection
{
    private AmendmentProjection() { }

    /// <summary>The document holds what the run executes, or the run has no amendment the document lacks.</summary>
    internal sealed record Current : AmendmentProjection;

    /// <summary>Applying <paramref name="Edit"/> moves the document from <paramref name="From"/> to the run's <paramref name="Target"/>.</summary>
    internal sealed record Apply(WorkflowEdit.Batch Edit, RevisionId From, RevisionId Target) : AmendmentProjection;

    /// <summary>
    /// The document no longer holds <paramref name="Expected"/>, the revision the run's latest amendment amended, or any
    /// earlier one of the run, so nothing is applied. <paramref name="Tasks"/> and <paramref name="Connections"/> differ
    /// from what the run executes now.
    /// </summary>
    internal sealed record Differs(RevisionId Expected, ImmutableArray<TaskId> Tasks, ImmutableArray<ConnectionKey> Connections) : AmendmentProjection
    {
        public bool Equals(Differs? other) => other is not null && Expected == other.Expected && Tasks.SequenceEqual(other.Tasks) &&
            Connections.SequenceEqual(other.Connections);

        public override int GetHashCode() => HashCode.Combine(Expected, Tasks.Length, Connections.Length);
    }

    public static AmendmentProjection Of(Workflow document, RunRecord record)
    {
        var amendments = record.Receipts.Values.Where(entry => entry.Event is RunEvent.Amended).OrderBy(entry => entry.Sequence)
            .Select(entry => (RunEvent.Amended)entry.Event).ToArray();
        var held = Revision.Capture(document).Id;
        if (amendments.Length == 0 || held == record.Revision.Id) return new Current();
        ApprovedRevision[] chain = [record.Revisions[amendments[0].Previous], .. amendments.Select(amended => record.Revisions[amended.Revision.Id])];
        var from = Array.FindLastIndex(chain, revision => revision.Id == held);
        // The edits extend the held revision to the latest, so they apply unless the document's copy of a blueprint differs.
        if (from >= 0 && Edits(chain[from].Snapshot, record.Revision.Snapshot) is { } edit && document.Apply(edit) is EditResult.Applied)
        {
            return new Apply(edit, held, record.Revision.Id);
        }

        var executed = record.Revision.Snapshot;
        ImmutableArray<TaskId> tasks = [.. document.Tasks.Keys.Union(executed.Tasks.Keys).Order().Where(task =>
            !document.Tasks.TryGetValue(task, out var mine) || !executed.Tasks.TryGetValue(task, out var theirs) ||
            Revision.CanonicalTask(mine) != Revision.CanonicalTask(theirs) || !mine.Blueprint.Equals(theirs.Blueprint))];
        ImmutableArray<ConnectionKey> connections = [.. document.Connections.Keys.Union(executed.Connections.Keys).Order().Where(key =>
            !document.Connections.TryGetValue(key, out var mine) || !executed.Connections.TryGetValue(key, out var theirs) || mine != theirs)];
        return new Differs(amendments[^1].Previous, tasks, connections);
    }

    /// <summary>
    /// The edit that turns <paramref name="before"/> into <paramref name="after"/>: a planner's amendment only adds tasks and
    /// connections and fills titles, fields, and settings. Null when a task changed its blueprint, which no edit expresses.
    /// </summary>
    private static WorkflowEdit.Batch? Edits(Workflow before, Workflow after)
    {
        var edits = ImmutableArray.CreateBuilder<WorkflowEdit>();
        ImmutableArray<TaskId> removedTasks = [.. before.Tasks.Keys.Where(task => !after.Tasks.ContainsKey(task))];
        ImmutableArray<ConnectionKey> removedConnections = [.. before.Connections.Keys.Where(key => !after.Connections.ContainsKey(key) &&
            !removedTasks.Contains(key.From) && !removedTasks.Contains(key.To))];
        if (!removedTasks.IsEmpty || !removedConnections.IsEmpty) edits.Add(new WorkflowEdit.Delete(removedTasks, removedConnections));
        foreach (var task in after.Tasks.Values.Where(task => !before.Tasks.ContainsKey(task.Id)))
        {
            edits.Add(new WorkflowEdit.PlaceNode(task.Id, task.Blueprint, after.Positions.GetValueOrDefault(task.Id))
            {
                Title = task.Title, Fields = task.Fields.ToImmutableDictionary(), Settings = new(task.Execution, task.Conversation),
            });
        }
        foreach (var (id, old) in before.Tasks.Where(pair => after.Tasks.ContainsKey(pair.Key)))
        {
            var task = after.Tasks[id];
            if (!task.Blueprint.Equals(old.Blueprint)) return null;
            if (task.Title != old.Title) edits.Add(new WorkflowEdit.EditTitle(id, task.Title));
            foreach (var field in task.Blueprint.Fields.Where(field => task.Field(field.Key) != old.Field(field.Key)))
                edits.Add(new WorkflowEdit.SetField(id, field.Key, task.Field(field.Key)));
            if (task.Execution != old.Execution) edits.Add(new WorkflowEdit.SetExecution(id, task.Execution));
            if (task.Conversation != old.Conversation) edits.Add(new WorkflowEdit.SetConversation(id, task.Conversation));
        }
        foreach (var (key, kind) in after.Connections)
        {
            if (!before.Connections.TryGetValue(key, out var held)) edits.Add(new WorkflowEdit.Connect(key, kind));
            else if (held != kind) edits.Add(new WorkflowEdit.SetConnectionKind(key, kind));
        }
        return new(edits.ToImmutable());
    }
}
