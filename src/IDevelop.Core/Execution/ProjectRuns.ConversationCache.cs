using System.Collections.Immutable;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    private sealed record CachedConversation(long Length, long Lines, AttemptHistory History);

    /// <summary>A run-owned attempt of a task: its run, the run's number among its workflow's runs, and why it started.</summary>
    private sealed record RunAttemptPlace(WorkflowId Workflow, RunId Run, int Number, AttemptCause Cause);

    private readonly Lock _historyGate = new();

    // Keyed by the attempt's folder, which names its owner: the project's standalone attempts or one run's.
    private readonly Dictionary<string, CachedConversation> _histories = [];
    private readonly Dictionary<AttemptId, RunAttemptPlace> _runAttempts = [];

    // Each run journal as last read, by its path and length. A journal only grows, so the same length is the same record.
    private readonly Dictionary<string, (long Length, RunRecord Record)> _journals = [];
    private sealed record PublishedAttempt(AttemptRecord Record, long LogRevision);

    private readonly Dictionary<TaskId, PublishedAttempt> _published = [];

    /// <summary>
    /// The history of one of the task's attempts, standalone or run-owned, with this window's live text while it runs it.
    /// A standalone attempt is found first; a run-owned one through the project's run journals.
    /// </summary>
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

        var standalone = AttemptLog.FolderOf(_attempts, task, attempt);
        if (Directory.Exists(standalone))
        {
            return ReadAttemptHistory(standalone, task, attempt)?.History;
        }

        return RunAttempt(task, attempt) is { } place ? ReadRunHistory(place.Workflow, place.Run, task, attempt)?.History : null;
    }

    /// <summary>The history of a run-owned attempt, with this window's live text and record while its turn runs here.</summary>
    private CachedConversation? ReadRunHistory(WorkflowId workflow, RunId run, TaskId task, AttemptId attempt)
    {
        if (LiveRun(workflow, run, attempt) is { } active && Read(active) is { } live)
        {
            return new CachedConversation(0, active.Live.LogRevision, live);
        }

        return ReadAttemptHistory(TurnStore.AttemptFolder(workflow, run, task, attempt), task, attempt);

        AttemptHistory? Read(ActiveRun active)
        {
            Probe?.Invoke("history.attempt.read");
            return active.ReadHistory(active.Current);
        }
    }

    /// <summary>
    /// A run-owned attempt's record and log revision, for a snapshot. While this window runs its turn they come from the turn
    /// in memory, so a snapshot never parses a log that grows as the turn streams.
    /// </summary>
    private (AttemptRecord? Record, long LogRevision) ReadRunRecord(WorkflowId workflow, RunId run, TaskId task, AttemptId attempt)
    {
        if (LiveRun(workflow, run, attempt) is { } active)
        {
            return (active.Current, active.Live.LogRevision);
        }

        return ReadAttemptHistory(TurnStore.AttemptFolder(workflow, run, task, attempt), task, attempt) is { } read
            ? (read.History.Record, read.Lines) : (null, 0);
    }

    /// <summary>The client run of a run-owned attempt's turn while it runs in this window.</summary>
    private ActiveRun? LiveRun(WorkflowId workflow, RunId run, AttemptId attempt)
    {
        TurnOwner[] owners;
        lock (_gate)
        {
            owners = [.. _owned.Values.Where(owner => owner.Address.Workflow == workflow && owner.Address.Run == run && owner.Address.Launch.Attempt == attempt)];
        }

        return owners.Select(owner => owner.Active).FirstOrDefault(active => active is not null);
    }

    private CachedConversation? ReadAttemptHistory(string folder, TaskId task, AttemptId attempt)
    {
        lock (_historyGate)
        {
            try
            {
                var length = new FileInfo(Path.Combine(folder, "events.jsonl")).Length;
                if (_histories.TryGetValue(folder, out var cached) && length <= cached.Length)
                {
                    return cached;
                }

                Probe?.Invoke("history.attempt.read");
                var events = AttemptLog.ReadPositioned(folder, out var lines, out var observedLength);
                var history = ConversationHistory.Project(events);
                if (history is null || history.Record.Task != task || history.Id != attempt)
                {
                    return null;
                }

                var read = new CachedConversation(observedLength, lines, history);
                if (history.Record.Status != AttemptStatus.Running)
                {
                    _histories[folder] = read;
                }

                return read;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The run that owns <paramref name="attempt"/>, read from the project's run journals when it is not known yet.</summary>
    private RunAttemptPlace? RunAttempt(TaskId task, AttemptId attempt)
    {
        lock (_historyGate)
        {
            if (_runAttempts.TryGetValue(attempt, out var known))
            {
                return known;
            }
        }

        return RunAttempts(task, CancellationToken.None).FirstOrDefault(found => found.Attempt == attempt).Place;
    }

    /// <summary>
    /// The task's attempts in every readable run journal of the project, with each run numbered among its workflow's runs
    /// in the order they were approved.
    /// </summary>
    private ImmutableArray<(AttemptId Attempt, RunAttemptPlace Place)> RunAttempts(TaskId task, CancellationToken ct)
    {
        var found = ImmutableArray.CreateBuilder<(AttemptId, RunAttemptPlace)>();
        var store = TurnStore;
        try
        {
            var root = DataFolder.Runs(_projectFolder);
            if (!Directory.Exists(root))
            {
                return [];
            }

            foreach (var workflowFolder in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParse(Path.GetFileName(workflowFolder), out var workflowId))
                {
                    continue;
                }

                var workflow = new WorkflowId(workflowId);
                List<RunRecord> runs = [];
                foreach (var runFolder in Directory.EnumerateDirectories(workflowFolder))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Guid.TryParse(Path.GetFileName(runFolder), out var run) && Journal(store, workflow, new RunId(run), runFolder) is { } record)
                    {
                        runs.Add(record);
                    }
                }

                var number = 0;
                foreach (var record in runs.OrderBy(Approved).ThenBy(record => record.Id.Value.ToString("D"), StringComparer.Ordinal))
                {
                    number++;
                    foreach (var attempt in record.Attempts.Values.Where(attempt => attempt.Task == task))
                    {
                        found.Add((attempt.Id, new RunAttemptPlace(workflow, record.Id, number, attempt.Cause)));
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        lock (_historyGate)
        {
            foreach (var (attempt, place) in found)
            {
                _runAttempts[attempt] = place;
            }
        }

        return found.ToImmutable();

        static DateTimeOffset Approved(RunRecord record) => record.Receipts.Values.MinBy(entry => entry.Sequence)?.At ?? DateTimeOffset.MaxValue;
    }

    /// <summary>The run's journal, read again only when it grew since the last read.</summary>
    private RunRecord? Journal(RunStore store, WorkflowId workflow, RunId run, string runFolder)
    {
        var path = Path.Combine(runFolder, "events.jsonl");
        if (!File.Exists(path))
        {
            return null;
        }

        var length = new FileInfo(path).Length;
        lock (_historyGate)
        {
            if (_journals.TryGetValue(path, out var cached) && cached.Length == length)
            {
                return cached.Record;
            }
        }

        Probe?.Invoke("history.journal.read");
        if (store.Read(workflow, run) is not RunRead.Loaded loaded)
        {
            return null;
        }

        lock (_historyGate)
        {
            _journals[path] = (length, loaded.Record);
        }

        return loaded.Record;
    }

    private static string Label(RunAttemptPlace place) => place.Cause switch
    {
        AttemptCause.Retry => $"Run {place.Number} retry",
        AttemptCause.Continue => $"Run {place.Number} continued",
        AttemptCause.ReviewFix => $"Run {place.Number} fix",
        _ => $"Run {place.Number}",
    };

    /// <summary>
    /// The task's standalone attempts in folder order, with its run-owned attempts placed among them by start time. Each one
    /// is labelled with its owner. An attempt whose log cannot be read is left out.
    /// </summary>
    private ImmutableArray<AttemptSummary> ListConversationAttempts(TaskId task, CancellationToken ct)
    {
        var standalone = ImmutableArray.CreateBuilder<AttemptSummary>();
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
                        standalone.Add(Summary(record));
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        var owned = new List<AttemptSummary>();
        foreach (var (attempt, place) in RunAttempts(task, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (ReadRunHistory(place.Workflow, place.Run, task, attempt)?.History.Record is { } record)
            {
                owned.Add(Summary(record) with { Label = Label(place) });
            }
        }

        var attempts = ImmutableArray.CreateBuilder<AttemptSummary>();
        var later = new Queue<AttemptSummary>(owned.OrderBy(summary => summary.Started).ThenBy(summary => summary.Id.Value.ToString("D"), StringComparer.Ordinal));
        foreach (var summary in standalone)
        {
            while (later.TryPeek(out var next) && next.Started < summary.Started)
            {
                attempts.Add(later.Dequeue());
            }

            attempts.Add(summary);
        }

        attempts.AddRange(later);
        return attempts.ToImmutable();

        static AttemptSummary Summary(AttemptRecord record) => new(record.Id, record.Continues, record.RequestedAt, record.Status, record.Requested);
    }
}
