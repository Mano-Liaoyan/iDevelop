using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    private sealed record CachedConversation(long Length, AttemptHistory History);

    private readonly Lock _historyGate = new();
    private readonly Dictionary<(TaskId Task, AttemptId Attempt), CachedConversation> _histories = [];
    private sealed record PublishedAttempt(AttemptRecord Record, long LogRevision);

    private readonly Dictionary<TaskId, PublishedAttempt> _published = [];

    private AttemptHistory? ReadConversationHistory(TaskId task, AttemptId attempt)
    {
        (ActiveRun Run, AttemptRecord Published)? owned;
        lock (_gate)
        {
            owned = _active.GetValueOrDefault(task) is { } active && active.AttemptId == attempt ? (active, Latest[task]) : null;
        }

        if (owned is { } current)
        {
            return current.Run.ReadHistory(current.Published);
        }

        AttemptHistory? history;
        lock (_historyGate)
        {
            try
            {
                var folder = AttemptLog.FolderOf(_attempts, task, attempt);
                var key = (task, attempt);
                var length = new FileInfo(Path.Combine(folder, "events.jsonl")).Length;
                if (_histories.TryGetValue(key, out var cached) && length <= cached.Length)
                {
                    return cached.History;
                }

                var events = AttemptLog.ReadPositioned(folder, out _, out var observedLength);
                history = ConversationHistory.Project(events);
                if (history is null || history.Record.Task != task || history.Id != attempt)
                {
                    return null;
                }

                if (history.Record.Status != AttemptStatus.Running)
                {
                    _histories[key] = new CachedConversation(observedLength, history);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return history;
    }

    private ImmutableArray<AttemptSummary> ListConversationAttempts(TaskId task, CancellationToken ct)
    {
        var attempts = ImmutableArray.CreateBuilder<AttemptSummary>();
        var folder = AttemptLog.TaskFolder(_attempts, task);
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var id in Directory.EnumerateDirectories(folder)
                    .Select(path => Guid.TryParse(Path.GetFileName(path), out var id) ? (Guid?)id : null)
                    .OfType<Guid>().Order())
                {
                    ct.ThrowIfCancellationRequested();
                    if (ReadConversationHistory(task, new AttemptId(id))?.Record is { } record)
                    {
                        attempts.Add(new AttemptSummary(record.Id, record.Continues, record.RequestedAt, record.Status, record.Requested));
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return attempts.ToImmutable();
    }
}
