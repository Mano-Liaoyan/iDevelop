using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// The attempts of one open project. It lives as long as the project is open in a window.
/// Several tasks of a project folder can run at once, and each task runs at most once at a time, across every iDevelop
/// instance.
/// </summary>
public sealed partial class ProjectRuns : IAsyncDisposable
{
    private const string LeaveReason = "The project was closed while this task ran.";

    private readonly Lock _gate = new();
    private readonly string _projectFolder;
    private readonly ClientDirectory _clients;
    private readonly HostQuestions _questions;
    private long _revision;

    private event Action<TaskId?, long>? ConversationChanged;

    private readonly string _attempts;
    private readonly HashSet<AttemptId> _started = [];
    private readonly Dictionary<TaskId, ActiveRun> _active = [];
    private readonly Lock _advancing = new();
    private long _launches;
    private Task? _leaving;
    private ImmutableDictionary<WorkflowId, Workflow> _workflows = ImmutableDictionary<WorkflowId, Workflow>.Empty;

    /// <summary>Why a review's next step could not start, by review. Cleared once a step starts.</summary>
    private ImmutableDictionary<TaskId, StartProblem> _stalls = ImmutableDictionary<TaskId, StartProblem>.Empty;

    private ProjectRuns(string projectFolder, ClientDirectory clients, ImmutableDictionary<TaskId, AttemptRecord> latest, ImmutableArray<string> warnings,
        ImmutableDictionary<(TaskId Task, AttemptId Attempt), long> logRevisions, HostQuestions questions)
    {
        _projectFolder = projectFolder;
        _clients = clients;
        _questions = questions;
        clients.Changed += OnClientsChanged;
        _attempts = DataFolder.Attempts(projectFolder);
        Latest = latest;
        Warnings = warnings;
        _logRevisions = new(logRevisions);
    }

    /// <summary>The host clock and timers used by the runner. Tests inject a manually advanced clock.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>The budget for a protocol stop or deferral teardown. Tests shorten it.</summary>
    internal TimeSpan ShutdownTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a client that reported success may take to exit, for its own shutdown work such as session hooks, before
    /// iDevelop stops it and fails the turn. Only a client that never exits should reach it. Tests shorten it.
    /// </summary>
    internal TimeSpan SuccessExitTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long leaving waits for a stopped run to end before it gives up on it. Tests shorten it.</summary>
    internal TimeSpan LeaveTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal (long Revision, long LogRevision, ImmutableDictionary<string, LiveMessageBuffer> Buffers)? Live(TaskId task)
    {
        lock (_gate)
        {
            return _active.TryGetValue(task, out var run) ? run.Live : null;
        }
    }

    /// <summary>The newest attempt of each task that has one.</summary>
    public ImmutableDictionary<TaskId, AttemptRecord> Latest { get; private set; }

    /// <summary>Attempt folders that could not be read or settled. Each one is a sentence for the user.</summary>
    public ImmutableArray<string> Warnings { get; private set; }

    /// <summary>The attempts this window runs, while they run, in the order this window started them.</summary>
    public ImmutableArray<AttemptRecord> Active
    {
        get
        {
            lock (_gate)
            {
                return [.. _active.Values.OrderBy(run => run.Order).Select(run => run.Record).Where(record => record.Status == AttemptStatus.Running)];
            }
        }
    }

    /// <summary>
    /// False for an attempt another window started. That window may have ended it since, and <see cref="Latest"/> shows
    /// the end only after the next <see cref="Start"/> reads the attempts again, whether it starts or not.
    /// </summary>
    public bool StartedHere(AttemptId attempt)
    {
        lock (_gate)
        {
            return _started.Contains(attempt);
        }
    }

    /// <summary>
    /// Raised after <see cref="Latest"/> or <see cref="Active"/> changes: at each new state of an attempt this window
    /// starts, at a hand-off to the terminal, at marking a waiting task done or cancelling it, and at a start, a
    /// continuation, or a hand-off that another window's run refuses, which reads every task's newest attempt again. A
    /// start, a continuation, a hand-off, and a waiting task's end raise it on the caller's thread and a run from a worker
    /// thread, in order. By a run's last one, <see cref="Active"/> no longer holds it and its task is free. The attempt is
    /// settled or waits for the person, unless leaving gave up on a client that did not end; that attempt stays on record
    /// as running, and the next open settles it.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Reads the newest attempt of each task. It also settles each attempt that a crashed instance left running and that no
    /// instance runs now: it stops the client if it is provably the same process, and records it as interrupted. A folder
    /// without attempts stays untouched.
    /// </summary>
    public static ProjectRuns Open(string projectFolder, ClientDirectory clients, HostQuestions? questions = null)
    {
        var folder = Path.GetFullPath(projectFolder);
        var (latest, warnings, revisions) = ReadAndReconcile(DataFolder.Attempts(folder));
        return new ProjectRuns(folder, clients, latest, warnings, revisions, questions ?? new HostQuestions.Disabled());
    }

    /// <summary>The reason this task cannot start now, or null. A run of it in another window shows up only at
    /// <see cref="Start"/>, because checking for it would take the task's lock.</summary>
    public StartProblem? Check(TaskDefinition task)
    {
        lock (_gate)
        {
            return Verdict(task, null, task.Blueprint.Work is WorkSpec.Review ? Subject(task.Id, WorkflowOf(task.Id), Latest, readChanges: false) : null) is StartVerdict.Blocked blocked
                ? blocked.Problem
                : null;
        }
    }

    /// <summary>
    /// Follows this workflow without replacing other workflows. A review's loop goes on from each change of an attempt
    /// and from each call of this, which also retries a step that could not start, such as a fix round whose client was still
    /// being checked. A review that rests between its turns when the project opens goes on once this is first called.
    /// </summary>
    public void Follow(Workflow workflow)
    {
        lock (_gate)
        {
            if (_workflows.Values.FirstOrDefault(other => other.Id != workflow.Id && other.Tasks.Keys.Any(workflow.Tasks.ContainsKey)) is { } other)
            {
                throw new InvalidOperationException($"Workflow {workflow.Id} shares a task with workflow {other.Id}. A task belongs to one workflow of a project.");
            }

            _workflows = _workflows.SetItem(workflow.Id, workflow);
        }

        // A step can run Git and start a client, so the window's thread does not wait for it.
        NotifyChanged(null);
        ThreadPool.QueueUserWorkItem(_ => Advance());
    }

    /// <summary>
    /// Checks the task, takes the task's run lock, settles any attempt a crash left running, records the attempt, and
    /// launches the client. Every reason not to launch comes back as <see cref="StartResult.Refused"/>.
    /// </summary>
    /// <param name="planning">
    /// What a node whose agent proposes may fill and place. Its prompt lists them by handle, and the attempt records the
    /// handles. Other nodes ignore it.
    /// </param>
    public StartResult Start(TaskDefinition task, PlanningContext? planning = null)
    {
        StartResult result;
        ActiveRun? run = null;
        SubjectView? subject = null;
        if (task.Blueprint.Work is WorkSpec.Review)
        {
            // Git reads the subject's change outside the gate. Under it, the subject's attempt must still be the one read.
            (Workflow? Workflow, ImmutableDictionary<TaskId, AttemptRecord> Latest) now;
            lock (_gate)
            {
                now = (WorkflowOf(task.Id), Latest);
            }

            subject = Subject(task.Id, now.Workflow, now.Latest, readChanges: true);
        }

        // Git runs outside the gate, so a slow work tree holds up no other run.
        var tree = GitTree.Snapshot(_projectFolder);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            if (subject is not null && Latest.GetValueOrDefault(subject.Node.Id)?.Id != subject.Latest?.Id)
            {
                return new StartResult.Refused(new StartProblem.SubjectNotDone(subject.Node.Title));
            }

            var verdict = Verdict(task, planning, subject);
            if (verdict is StartVerdict.Blocked blocked)
            {
                return new StartResult.Refused(blocked.Problem);
            }

            switch (TakeLock(task.Id))
            {
                case LockTake.Failed failed:
                    return new StartResult.Refused(failed.Problem);
                case LockTake.HeldElsewhere elsewhere:
                    result = new StartResult.Refused(elsewhere.Problem);
                    break;
                // The read under the lock can find that another window's run ended waiting for the person.
                case LockTake.Taken taken when Latest.GetValueOrDefault(task.Id) is { Status: AttemptStatus.WaitingForInput } waiting:
                    taken.Lock.Dispose();
                    return new StartResult.Refused(new StartProblem.Waiting(waiting.TaskTitle));
                case LockTake.Taken taken when Latest.GetValueOrDefault(task.Id) is { Status: AttemptStatus.InReview } reviewing:
                    taken.Lock.Dispose();
                    return new StartResult.Refused(new StartProblem.InReview(reviewing.TaskTitle));
                case LockTake.Taken taken:
                    var handles = task.Blueprint.Work is WorkSpec.Agent { Proposes: true } ? (planning ?? PlanningContext.None).Handles(Guid.CreateVersion7()) : null;
                    (result, run) = RecordAndLaunch(task, ((StartVerdict.Allowed)verdict).Plan, taken.Lock, continues: null, tree, handles, subject: subject?.Node.Id);
                    if (result is StartResult.Refused)
                    {
                        return result;
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        Announce(task.Id, run);
        return result;
    }

    /// <summary>
    /// Sends the person's message to the task's agent. While this window runs the task, the message waits for the running
    /// turn to end, or <paramref name="stopTurn"/> stops that turn's process tree first, and the next turn resumes the
    /// session with it. A task that waits for the person takes it as the next turn of its waiting attempt. Otherwise a new
    /// attempt resumes the latest attempt's session with it, which leaves that attempt as it was. Every reason not to send
    /// comes back as <see cref="SendResult.Refused"/>.
    /// </summary>
    public async Task<SendResult> SendAsync(TaskDefinition task, string text, bool stopTurn, CancellationToken ct = default, TurnKey? expected = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (SendTarget(task, expected) is { } target)
            {
                return new SendResult.Refused(target);
            }

            task = Resolve(task.Id) ?? task;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new SendResult.Refused(new SendProblem.EmptyMessage());
        }

        switch ((NodeWorks.For(task.Blueprint.Work) as IConverses)?.Receive(text))
        {
            case null:
                return new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.NoConversation()));
            case MessageUse.Guidance guidance:
                return await GuideAsync(task, guidance.Text, ct, expected);
            case MessageUse.Turn turn:
                text = turn.Prompt;
                break;
        }

        ActiveRun? active;
        lock (_gate)
        {
            if (SendTarget(task, expected) is { } target)
            {
                return new SendResult.Refused(target);
            }

            task = Resolve(task.Id) ?? task;
            _active.TryGetValue(task.Id, out active);
        }

        if (active is not null)
        {
            return await active.SendAsync(task, text, stopTurn, ct, expected);
        }

        SendResult result;
        ActiveRun? run = null;
        var tree = GitTree.Snapshot(_projectFolder);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (SendTarget(task, expected) is { } target)
            {
                return new SendResult.Refused(target);
            }

            task = Resolve(task.Id) ?? task;
            if (_active.TryGetValue(task.Id, out var started))
            {
                return new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.AlreadyRunning(task.Id, started.Record.TaskTitle)));
            }

            switch (TakeLock(task.Id))
            {
                case LockTake.Failed failed:
                    return new SendResult.Refused(new SendProblem.CannotStart(failed.Problem));
                case LockTake.HeldElsewhere elsewhere:
                    result = new SendResult.Refused(new SendProblem.CannotStart(elsewhere.Problem));
                    break;
                case LockTake.Taken taken when SendTarget(task, expected) is { } lockedTarget:
                    taken.Lock.Dispose();
                    return new SendResult.Refused(lockedTarget);
                case LockTake.Taken taken:
                    (result, run) = Latest.GetValueOrDefault(task.Id) is { Status: AttemptStatus.WaitingForInput } waiting
                        ? Answer(task, text, waiting, taken.Lock, tree)
                        : Continue(task, text, taken.Lock, tree);
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        Announce(task.Id, run);
        return result;
    }

    /// <summary>
    /// Why <see cref="SendAsync"/> would refuse a message to this task now, or null, for the inspector before any click. Send
    /// checks again under the task's lock, where a run of it in another window also shows up.
    /// </summary>
    public SendProblem? CheckSend(TaskDefinition task)
    {
        lock (_gate)
        {
            if (SendTarget(task, null) is { } target)
            {
                return target;
            }

            task = Resolve(task.Id) ?? task;
            switch (task.Blueprint.Work)
            {
                case WorkSpec.Person:
                    return new SendProblem.CannotStart(new StartProblem.NoConversation());
                case WorkSpec.Review when _active.TryGetValue(task.Id, out var reviewing):
                    return reviewing.GuideProblem();
                case WorkSpec.Review:
                    return Latest.GetValueOrDefault(task.Id) is { Status: AttemptStatus.InReview } ? null : new SendProblem.NotReviewing(task.Title);
            }

            if (_active.TryGetValue(task.Id, out var run))
            {
                return run.SendProblem();
            }

            var last = Latest.GetValueOrDefault(task.Id);
            if (last is { Status: AttemptStatus.Running })
            {
                return new SendProblem.CannotStart(new StartProblem.AlreadyRunning(last.Task, last.TaskTitle));
            }

            if (last is not { Status: AttemptStatus.WaitingForInput } && ReviewOf(task.Id) is { } review)
            {
                return new SendProblem.CannotStart(new StartProblem.UnderReview(review.TaskTitle));
            }

            if (!TryContinue(task, last, out var from, out var problem))
            {
                return problem;
            }

            var settings = last is { Status: AttemptStatus.WaitingForInput } ? last.Requested : task.Execution;
            return StartCheck.Evaluate(task with { Execution = settings }, _projectFolder, _clients.Current, new Resumption(from.Session, ""), questions: _questions) is StartVerdict.Blocked blocked
                ? new SendProblem.CannotStart(blocked.Problem)
                : null;
        }
    }

    /// <summary>
    /// The attempts whose session <paramref name="attempt"/> continues, one continuation after another, oldest first, read
    /// from their logs. An attempt that cannot be read ends the list there. A continued attempt is settled and is no
    /// longer its task's latest, so it never changes again.
    /// </summary>
    public ImmutableArray<AttemptRecord> EarlierAttempts(AttemptRecord attempt)
    {
        List<AttemptRecord> earlier = [];
        // An edited log could link back to an attempt already read.
        HashSet<AttemptId> seen = [attempt.Id];
        var link = attempt.Continues;
        while (link is { } id && seen.Add(id) && AttemptLog.ReadAttempt(_attempts, attempt.Task, id) is { } continued)
        {
            earlier.Insert(0, continued);
            link = continued.Continues;
        }

        return [.. earlier];
    }

    /// <summary>
    /// Records on the task's latest attempt that the person took its session to the client's own terminal interface, and
    /// returns the command that opens it there. Refused unless the task waits for the person with a session.
    /// </summary>
    public TerminalResult OpenInTerminal(TaskId task, TurnKey? expected = null)
    {
        TerminalResult result;
        lock (_gate)
        {
            if (_leaving is not null)
            {
                return new TerminalResult.Refused(new TerminalProblem.ClosedOwner());
            }

            if (expected is { } target && Current(task) != target)
            {
                return new TerminalResult.Refused(new TerminalProblem.StaleTarget());
            }

            if (_active.TryGetValue(task, out var run))
            {
                return new TerminalResult.Refused(new TerminalProblem.TurnRunning(run.Record.TaskTitle));
            }

            switch (TakeLock(task))
            {
                case LockTake.Failed failed:
                    return new TerminalResult.Refused(new TerminalProblem.Blocked(failed.Problem));
                case LockTake.HeldElsewhere elsewhere:
                    result = new TerminalResult.Refused(new TerminalProblem.Blocked(elsewhere.Problem));
                    break;
                case LockTake.Taken taken:
                    using (taken.Lock)
                    {
                        result = expected is { } turn && Current(task) != turn
                            ? new TerminalResult.Refused(new TerminalProblem.StaleTarget()) : HandOff(task);
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        NotifyChanged(task);
        return result;
    }

    /// <summary>Stops the task's client and every process it started, unless the client already exited, and records the
    /// attempt as cancelled. A task that waits for the person is recorded as cancelled at once. Does nothing otherwise,
    /// unless this window runs that task's attempt. Returns why a waiting task could not be cancelled, or null.</summary>
    public async Task<StartProblem?> CancelAsync(TaskId task, CancellationToken ct = default) => (await CancelCoreAsync(task, null, ct)).Problem;

    private async Task<(StartProblem? Problem, bool Applied, bool Stale)> CancelCoreAsync(TaskId task, TurnKey? expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ActiveRun? run;
        AttemptRecord? before;
        lock (_gate)
        {
            if (_leaving is not null)
            {
                return (null, false, false);
            }

            if (expected is { } target && Current(task) != target)
            {
                return (null, false, true);
            }

            _active.TryGetValue(task, out run);
            before = run?.Record ?? Latest.GetValueOrDefault(task);
        }

        StartProblem? problem;
        bool applied;
        if (run is not null)
        {
            var accepted = await run.StopAsync(new AttemptEvent.CancelRequested(TimeProvider.GetUtcNow()), expected, ct);
            if (accepted is SendResult.Refused refusal)
            {
                return (refusal.Problem is SendProblem.CannotStart failed ? failed.Problem : null, false, refusal.Problem is SendProblem.StaleTarget);
            }

            (problem, applied) = (null, true);
        }
        else
        {
            problem = Append(task, record => (expected is null || Current(task) == expected)
                && record.Status is AttemptStatus.WaitingForInput or AttemptStatus.InReview, new AttemptEvent.CancelRequested(TimeProvider.GetUtcNow()), out applied);
            ThreadPool.QueueUserWorkItem(_ => Advance());
        }

        if (applied && before is { Subject: { } subject } && Latest.GetValueOrDefault(subject) is { Fix: { } link } fix
            && link.Attempt == before.Id && fix.Status is AttemptStatus.Running or AttemptStatus.WaitingForInput)
        {
            problem = await CancelAsync(subject);
        }

        return (problem, applied, expected is not null && !applied && problem is null);
    }

    /// <summary>Records a task that waits for the person as succeeded, with its last reply as its result. Null when it is
    /// done, else why not.</summary>
    public StartProblem? MarkDone(TaskId task) => MarkDoneCore(task, null).Problem;

    private (StartProblem? Problem, bool Applied) MarkDoneCore(TaskId task, TurnKey? expected)
    {
        var problem = Append(task, record => (expected is null || Current(task) == expected) && record.Status == AttemptStatus.WaitingForInput,
            new AttemptEvent.MarkedDone(TimeProvider.GetUtcNow()), out var applied);
        ThreadPool.QueueUserWorkItem(_ => Advance());
        return (problem, applied);
    }

    /// <summary>
    /// Appends <paramref name="e"/> to the task's latest attempt under its lock, when <paramref name="fits"/> it before and
    /// after the lock is taken. Does nothing otherwise, while this window runs the task, or once the project is leaving.
    /// Then a review may go on, off the caller's thread, because its step can run Git and start a client.
    /// </summary>
    private StartProblem? Append(TaskId task, Func<AttemptRecord, bool> fits, AttemptEvent e, out bool appended)
    {
        appended = false;
        StartProblem? problem = null;
        lock (_gate)
        {
            if (_leaving is not null || _active.ContainsKey(task) || Latest.GetValueOrDefault(task) is not { } current || !fits(current))
            {
                return null;
            }

            switch (TakeLock(task))
            {
                case LockTake.Failed failed:
                    return failed.Problem;
                case LockTake.HeldElsewhere elsewhere:
                    problem = elsewhere.Problem;
                    break;
                case LockTake.Taken taken:
                    using (taken.Lock)
                    {
                        if (Latest.GetValueOrDefault(task) is { } waiting && fits(waiting))
                        {
                            try
                            {
                                using var log = AttemptLog.Open(AttemptLog.FolderOf(_attempts, task, waiting.Id));
                                log.Append(e);
                                _logRevisions[(task, waiting.Id)] = log.LineCount;
                                Latest = Latest.SetItem(task, AttemptReducer.Apply(waiting, e));
                                appended = true;
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                            {
                                problem = CannotRecord(error);
                            }
                        }
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        NotifyChanged(task);
        return problem;
    }

    /// <summary>
    /// Leaving the project. It stops each running client's process tree, records each attempt as interrupted, and releases
    /// the tasks. If a run has not ended 10 seconds after the stop, its attempt stays on record as running, and the next
    /// open settles it.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_leaving is null)
            {
                _clients.Changed -= OnClientsChanged;
                _leaving = Task.WhenAll(_active.Values.ToList().Select(run => LeaveAsync(run, LeaveTimeout)));
                NotifyConversations(null);
            }

            return new ValueTask(_leaving);
        }
    }

    private async Task LeaveAsync(ActiveRun run, TimeSpan timeout)
    {
        await run.StopAsync(new AttemptEvent.InterruptRequested(TimeProvider.GetUtcNow(), LeaveReason));
        if (await Task.WhenAny(run.Completion, Task.Delay(timeout)) != run.Completion)
        {
            run.Abandon();
            await run.Completion;
        }
    }

    /// <summary>The newest attempt of each task, after <see cref="Reconcile"/>, and the warnings of both.</summary>
    private static (ImmutableDictionary<TaskId, AttemptRecord> Latest, ImmutableArray<string> Warnings,
        ImmutableDictionary<(TaskId Task, AttemptId Attempt), long> LogRevisions) ReadAndReconcile(
        string attempts, TaskId? held = null)
    {
        var (latest, warnings, revisions) = AttemptLog.ReadLatest(attempts);
        var counts = revisions.ToBuilder();
        var notes = warnings.ToBuilder();
        latest = Reconcile(attempts, latest, notes, counts, held);
        return (latest, notes.ToImmutable(), counts.ToImmutable());
    }

    /// <summary>
    /// Settles each attempt that reads as running and whose task's lock is free, so no live instance runs it. A run of this
    /// window holds its task's lock and is skipped. <paramref name="held"/> names a task whose lock the caller holds.
    /// </summary>
    private static ImmutableDictionary<TaskId, AttemptRecord> Reconcile(
        string attempts, ImmutableDictionary<TaskId, AttemptRecord> latest, ImmutableArray<string>.Builder warnings,
        ImmutableDictionary<(TaskId Task, AttemptId Attempt), long>.Builder revisions, TaskId? held)
    {
        foreach (var running in latest.Values.Where(record => record.Status == AttemptStatus.Running).ToList())
        {
            if (running.Task == held)
            {
                latest = SettleCrashed(attempts, latest, running, warnings, revisions);
                continue;
            }

            RunLock? taskLock;
            try
            {
                taskLock = RunLock.TryTake(attempts, running.Task);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"iDevelop could not settle \"{running.TaskTitle}\", which a closed window left running. {e.Message}");
                continue;
            }

            using (taskLock)
            {
                if (taskLock is not null)
                {
                    // The run may have ended between the read and the lock, so settle what its log says now.
                    var (record, lines) = AttemptLog.ReadLatest(attempts, running.Task);
                    if (record is not null)
                    {
                        revisions[(record.Task, record.Id)] = lines;
                    }

                    if (record is { Status: AttemptStatus.Running })
                    {
                        latest = SettleCrashed(attempts, latest, record, warnings, revisions);
                    }
                    else if (record is not null)
                    {
                        latest = latest.SetItem(running.Task, record);
                    }
                }
            }
        }

        return latest;
    }

    private static ImmutableDictionary<TaskId, AttemptRecord> SettleCrashed(
        string attempts, ImmutableDictionary<TaskId, AttemptRecord> latest, AttemptRecord record, ImmutableArray<string>.Builder warnings,
        ImmutableDictionary<(TaskId Task, AttemptId Attempt), long>.Builder revisions)
    {
        ProcessMatch? match = record.Process is { } process ? ProcessCheck.StopIfSame(process) : null;
        var reconciled = new AttemptEvent.Reconciled(System.TimeProvider.System.GetUtcNow(), match);
        try
        {
            using (var log = AttemptLog.Open(AttemptLog.FolderOf(attempts, record.Task, record.Id)))
            {
                log.Append(reconciled);
                revisions[(record.Task, record.Id)] = log.LineCount;
            }

            return latest.SetItem(record.Task, AttemptReducer.Apply(record, reconciled));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"iDevelop could not record that \"{record.TaskTitle}\" was interrupted. {e.Message}");
            return latest;
        }
    }

    /// <summary>
    /// Takes the task's run lock and then reads every task's newest attempt again, settling any attempt that a crash left
    /// running, this task's included. Called under the gate.
    /// </summary>
    private LockTake TakeLock(TaskId task)
    {
        RunLock? held;
        try
        {
            held = RunLock.TryTake(_attempts, task);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LockTake.Failed(CannotRecord(e));
        }

        if (held is null)
        {
            // Another instance holds the lock, so the read settles nothing.
            (Latest, Warnings, var revisions) = AttemptLog.ReadLatest(_attempts);
            foreach (var (key, lines) in revisions)
            {
                _logRevisions[key] = lines;
            }

            NotifyConversations(null);
            return new LockTake.HeldElsewhere(AnotherWindowsRun(task));
        }

        (Latest, Warnings, var counts) = ReadAndReconcile(_attempts, held: task);
        foreach (var (key, lines) in counts)
        {
            _logRevisions[key] = lines;
        }

        NotifyConversations(null);
        return new LockTake.Taken(held);
    }

    /// <summary>Raises <see cref="Changed"/> on the caller's thread, then starts reading the new run.</summary>
    private void Announce(TaskId task, ActiveRun? run)
    {
        try
        {
            NotifyChanged(task);
        }
        finally
        {
            run?.Start();
        }
    }

    /// <summary>
    /// Starts a new attempt whose first turn resumes the task's latest session with the message. Called under the gate
    /// with the task's lock, which it releases on a refusal.
    /// </summary>
    private (SendResult Result, ActiveRun? Run) Continue(TaskDefinition task, string message, RunLock held, string? tree)
    {
        if (ReviewOf(task.Id) is { } review)
        {
            held.Dispose();
            return (new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.UnderReview(review.TaskTitle))), null);
        }

        var last = Latest.GetValueOrDefault(task.Id);
        if (!TryContinue(task, last, out var from, out var problem))
        {
            held.Dispose();
            return (new SendResult.Refused(problem), null);
        }

        var verdict = StartCheck.Evaluate(task, _projectFolder, _clients.Current, new Resumption(from.Session, message), questions: _questions);
        if (verdict is StartVerdict.Blocked blocked)
        {
            held.Dispose();
            return (new SendResult.Refused(new SendProblem.CannotStart(blocked.Problem)), null);
        }

        // The session read the handles the continued attempt recorded, so its proposals name them.
        var (result, run) = RecordAndLaunch(task, ((StartVerdict.Allowed)verdict).Plan, held, from, tree, last?.Planning);
        return result switch
        {
            StartResult.Started started => (new SendResult.Continued(started.Attempt), run),
            StartResult.Refused refused => (new SendResult.Refused(new SendProblem.CannotStart(refused.Problem)), null),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Starts the next turn of the task's waiting attempt with the message, under the same log and with the model and
    /// reasoning the attempt started with. Called under the gate with the task's lock, which it releases unless the turn runs.
    /// </summary>
    /// <param name="tree">The project's files before the turn, read outside the gate.</param>
    private (SendResult Result, ActiveRun? Run) Answer(TaskDefinition task, string message, AttemptRecord waiting, RunLock held, string? tree, FixReport? report = null)
    {
        if (!TryContinue(task, waiting, out var from, out var problem))
        {
            held.Dispose();
            return (new SendResult.Refused(problem), null);
        }

        var prompt = waiting.Status == AttemptStatus.WaitingForInput
            ? string.Join("\n\n", waiting.Queued.Select(item => item.Text).Append(message)) : message;
        var verdict = StartCheck.Evaluate(task with { Execution = waiting.Requested }, _projectFolder, _clients.Current, new Resumption(from.Session, prompt), questions: _questions);
        if (verdict is StartVerdict.Blocked blocked)
        {
            held.Dispose();
            return (new SendResult.Refused(new SendProblem.CannotStart(blocked.Problem)), null);
        }

        var plan = ((StartVerdict.Allowed)verdict).Plan;
        AttemptLog log;
        AttemptRecord record;
        try
        {
            log = AttemptLog.Open(AttemptLog.FolderOf(_attempts, task.Id, waiting.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            held.Dispose();
            return (new SendResult.Refused(new SendProblem.CannotStart(CannotRecord(e))), null);
        }

        try
        {
            record = waiting;
            if (waiting.Status == AttemptStatus.WaitingForInput)
            {
                var queued = new AttemptEvent.MessageQueued(TimeProvider.GetUtcNow(), message, false) { Id = Guid.CreateVersion7().ToString() };
                log.Append(queued);
                record = AttemptReducer.Apply(record, queued);
            }

            var turn = new AttemptEvent.TurnRequested(TimeProvider.GetUtcNow(), plan.Request.Prompt, plan.Command.Path, plan.Launch.Arguments)
            {
                Conversation = task.Conversation,
                Tree = tree,
                Report = report,
                Consumed = waiting.Status == AttemptStatus.WaitingForInput ? [.. record.Queued.Select(item => item.Id)] : default,
                Replies = waiting.Status == AttemptStatus.WaitingForInput
                    ? [.. waiting.Requests.Values.OfType<RequestRecord.Question>()
                        .Where(request => request.Key.Turn.Number == waiting.Turns.Count
                            && request.State is QuestionState.Closed { Reason: RequestCloseReason.Deferred })
                        .OrderBy(request => request.Key.Turn.Number).ThenBy(request => request.Key.Id, StringComparer.Ordinal)
                        .Select(request => new TurnRequestId(request.Key.Turn.Number, request.Key.Id))] : default,
            };
            log.Append(turn);
            record = AttemptReducer.Apply(record, turn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Dispose();
            held.Dispose();
            return (new SendResult.Refused(new SendProblem.CannotStart(CannotRecord(e))), null);
        }

        var (launched, process) = LaunchTurn(plan, record, log);
        _started.Add(launched.Id);
        ActiveRun? run = null;
        if (process is null)
        {
            log.Dispose();
            held.Dispose();
        }
        else
        {
            run = new ActiveRun(this, ++_launches, plan, process, log, held, launched);
            _active[task.Id] = run;
        }

        Latest = Latest.SetItem(task.Id, launched);
        _logRevisions[(task.Id, launched.Id)] = log.LineCount;
        return (new SendResult.Answered(launched), run);
    }

    /// <summary>The session of the task's latest attempt that a message can resume, or why there is none.</summary>
    private static bool TryContinue(
        TaskDefinition task, AttemptRecord? last, [NotNullWhen(true)] out Continuation? from, [NotNullWhen(false)] out SendProblem? problem)
    {
        (from, problem) = last switch
        {
            null => (null, new SendProblem.NeverRan()),
            { SessionId: null } => (null, new SendProblem.NoSession(last.Requested.Client)),
            _ when task.Execution is { } settings && settings.Client != last.Requested.Client =>
                (null, new SendProblem.ClientChanged(last.Requested.Client, settings.Client)),
            { SessionId: { } session } => (new Continuation(last.Id, session), (SendProblem?)null),
        };
        return from is not null;
    }

    /// <summary>Appends the hand-off to the task's latest attempt. Called under the gate with the task's lock.</summary>
    private TerminalResult HandOff(TaskId task)
    {
        if (Latest.GetValueOrDefault(task) is not { } last)
        {
            return new TerminalResult.Refused(new TerminalProblem.NeverRan());
        }

        if (last.SessionId is not { } session)
        {
            return new TerminalResult.Refused(new TerminalProblem.NoSession(last.Requested.Client));
        }

        if (last.Status != AttemptStatus.WaitingForInput)
        {
            return new TerminalResult.Refused(new TerminalProblem.NotWaiting(last.TaskTitle));
        }

        var command = TerminalCommand.For(_projectFolder, Clients.Get(last.Requested.Client).Terminal(session), TerminalCommand.Current);
        var handoff = new AttemptEvent.HandedToTerminal(TimeProvider.GetUtcNow(), _projectFolder, command);
        try
        {
            using var log = AttemptLog.Open(AttemptLog.FolderOf(_attempts, task, last.Id));
            log.Append(handoff);
            _logRevisions[(task, last.Id)] = log.LineCount;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TerminalResult.Refused(new TerminalProblem.Blocked(CannotRecord(e)));
        }

        Latest = Latest.SetItem(task, AttemptReducer.Apply(last, handoff));
        return new TerminalResult.HandedOff(handoff.Folder, handoff.Command);
    }

    /// <summary>
    /// Records the attempt and launches its first turn. An attempt that cannot be recorded is refused, and the lock is
    /// released. Called under the gate with the task's lock.
    /// </summary>
    private (StartResult Result, ActiveRun? Run) RecordAndLaunch(
        TaskDefinition task, LaunchPlan plan, RunLock held, Continuation? continues, string? tree, PlanningHandles? planning = null,
        TaskId? subject = null, ReviewLink? fix = null)
    {
        AttemptLog log;
        AttemptEvent.Requested requested;
        try
        {
            DataFolder.EnsureGitIgnore(_projectFolder);
            requested = new AttemptEvent.Requested(
                TimeProvider.GetUtcNow(), AttemptId.New(), task.Id, task.Title, plan.Settings, plan.Request.Prompt, plan.Command.Path, plan.Launch.Arguments)
            {
                Continues = continues,
                Conversation = task.Conversation,
                Planning = planning,
                Tree = tree,
                ReadOnly = plan.Request.ReadOnly,
                Subject = subject,
                Fix = fix,
            };
            log = AttemptLog.Create(_attempts, requested);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            held.Dispose();
            return (new StartResult.Refused(CannotRecord(e)), null);
        }

        var (record, process) = LaunchTurn(plan, AttemptReducer.Start(requested), log);
        _started.Add(record.Id);
        ActiveRun? run = null;
        if (process is null)
        {
            log.Dispose();
            held.Dispose();
        }
        else
        {
            run = new ActiveRun(this, ++_launches, plan, process, log, held, record);
            _active[task.Id] = run;
        }

        Latest = Latest.SetItem(task.Id, record);
        _logRevisions[(task.Id, record.Id)] = log.LineCount;
        return (new StartResult.Started(record), run);
    }

    /// <summary>Whether this window runs the task, then whether it waits for the person or reviews, then whether a review
    /// has a finished subject, then what <see cref="StartCheck"/> says. Called under the gate.</summary>
    private StartVerdict Verdict(TaskDefinition task, PlanningContext? planning = null, SubjectView? subject = null) =>
        (_active.TryGetValue(task.Id, out var run), Latest.GetValueOrDefault(task.Id)) switch
        {
            (true, _) => new StartVerdict.Blocked(new StartProblem.AlreadyRunning(run!.Record.Task, run.Record.TaskTitle)),
            (_, { Status: AttemptStatus.WaitingForInput } waiting) => new StartVerdict.Blocked(new StartProblem.Waiting(waiting.TaskTitle)),
            (_, { Status: AttemptStatus.InReview } reviewing) =>
                new StartVerdict.Blocked(_stalls.GetValueOrDefault(task.Id) ?? new StartProblem.InReview(reviewing.TaskTitle)),
            _ when ReviewOf(task.Id) is { } review => new StartVerdict.Blocked(new StartProblem.UnderReview(review.TaskTitle)),
            _ when task.Blueprint.Work is WorkSpec.Review && ReviewProblem(task.Id, subject) is { } problem => new StartVerdict.Blocked(problem),
            _ => StartCheck.Evaluate(task, _projectFolder, _clients.Current, planning: planning, subject: subject, questions: _questions),
        };

    private TaskDefinition? Resolve(TaskId task) => WorkflowOf(task)?.Tasks.GetValueOrDefault(task);

    private SendProblem? SendTarget(TaskDefinition task, TurnKey? expected)
    {
        if (_leaving is not null)
        {
            return new SendProblem.ClosedOwner();
        }

        if ((expected is not null || !_workflows.IsEmpty) && Resolve(task.Id) is null)
        {
            return new SendProblem.MissingTask();
        }

        if (expected is { } turn && Current(task.Id) != turn)
        {
            return new SendProblem.StaleTarget();
        }

        var current = Resolve(task.Id) ?? task;
        return Latest.GetValueOrDefault(task.Id) is { } last && current.Execution is { } settings && settings.Client != last.Requested.Client
            ? new SendProblem.ClientChanged(last.Requested.Client, settings.Client) : null;
    }

    private TurnKey? Current(TaskId task) => (_active.GetValueOrDefault(task)?.Record ?? Latest.GetValueOrDefault(task)) is { } record ? new TurnKey(record.Id, record.Turns.Count) : null;

    private void OnClientsChanged(object? sender, EventArgs e) => NotifyChanged(null);

    /// <summary>Raises <see cref="Changed"/> and invalidates the sessions of <paramref name="task"/>, or of every task when it is null.</summary>
    private void NotifyChanged(TaskId? task)
    {
        NotifyConversations(task);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyConversations(TaskId? task) => ConversationChanged?.Invoke(task, Interlocked.Increment(ref _revision));

    private Workflow? WorkflowOf(TaskId task) => _workflows.Values.FirstOrDefault(workflow => workflow.Tasks.ContainsKey(task));

    /// <summary>
    /// The review attempt that goes on with this task as its subject, or null. Its fix rounds are the task's only new
    /// attempts until it ends, so no other attempt takes the place of a round. A review held by no followed workflow takes no
    /// next step, so it no longer holds a subject that a followed workflow holds. Until the subject's workflow is followed,
    /// the review still holds it. Called under the gate.
    /// </summary>
    private AttemptRecord? ReviewOf(TaskId task) => Latest.Values.FirstOrDefault(record =>
        record.Subject == task && record.Status is AttemptStatus.Running or AttemptStatus.InReview && (WorkflowOf(record.Task) is not null || WorkflowOf(task) is null));

    /// <summary>
    /// Why a review cannot start on its subject as it stands, or null. Another review of the subject would take the fix
    /// rounds' place. Called under the gate.
    /// </summary>
    private StartProblem? ReviewProblem(TaskId review, SubjectView? subject) => subject switch
    {
        null => new StartProblem.NoSubject(),
        { Latest.Status: not AttemptStatus.Succeeded } or { Latest: null } => new StartProblem.SubjectNotDone(subject.Node.Title),
        { Change: null } => new StartProblem.NoChange(subject.Node.Title),
        _ when ReviewOf(subject.Node.Id) is { } other && other.Task != review => new StartProblem.SubjectInReview(subject.Node.Title, other.TaskTitle),
        _ => null,
    };

    /// <summary>
    /// The subject of <paramref name="review"/> as the review reads it, or null when it has none. With
    /// <paramref name="readChanges"/>, Git reads its changes. Without, <see cref="SubjectView.Change"/> is only empty
    /// rather than null when the latest attempt recorded its trees, which is enough to check a start before any click.
    /// </summary>
    private SubjectView? Subject(TaskId review, Workflow? workflow, ImmutableDictionary<TaskId, AttemptRecord> latest, bool readChanges, TaskId? recorded = null)
    {
        if ((recorded ?? workflow?.SubjectOf(review)) is not { } id || workflow?.Tasks.GetValueOrDefault(id) is not { } node)
        {
            return null;
        }

        var last = latest.GetValueOrDefault(id);
        var canResume = last is { SessionId: not null } && (node.Execution is null || node.Execution.Client == last.Requested.Client);
        var view = new SubjectView(node, last) { CanResume = canResume };
        if (last is not { Status: AttemptStatus.Succeeded, StartTree: { } start, EndTree: { } end })
        {
            return view;
        }

        if (!readChanges)
        {
            return view with { Change = "", LatestChange = "" };
        }

        // The whole change starts where the conversation that the latest attempt continues started.
        var first = EarlierAttempts(last).FirstOrDefault() ?? last;
        return view with
        {
            Change = first.StartTree is { } origin ? GitTree.Diff(_projectFolder, origin, end) : null,
            LatestChange = GitTree.Diff(_projectFolder, start, end),
        };
    }

    /// <summary>
    /// Starts a turn's client and records its process. A client that does not start fails the attempt, and so does a log
    /// that can no longer be written, which also stops the client. The process is null once the attempt has ended.
    /// </summary>
    private (AttemptRecord Record, ChildProcess? Process) LaunchTurn(LaunchPlan plan, AttemptRecord record, AttemptLog log)
    {
        ChildProcess process;
        try
        {
            process = ChildProcess.Start(plan.Command, plan.Launch.Arguments, _projectFolder);
        }
        catch (LaunchException e)
        {
            var failed = new AttemptEvent.LaunchFailed(TimeProvider.GetUtcNow(), e.Message);
            try
            {
                log.Append(failed);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The log then ends at the turn's request, and the next open settles it.
            }

            return (AttemptReducer.Apply(record, failed), null);
        }

        // On Windows a crash stops the client through its job. On Linux and macOS, a crash before this line is on disk
        // leaves a client that reconciliation cannot identify.
        var launched = new AttemptEvent.Launched(TimeProvider.GetUtcNow(), process.Identity.Id, process.Identity.StartedAt);
        try
        {
            log.Append(launched);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            process.StopTree();
            process.Dispose();
            return (AttemptReducer.Abandon(record, CannotWriteLog(e), TimeProvider.GetUtcNow()), null);
        }

        return (AttemptReducer.Apply(record, launched), process);
    }

    private static string CannotWriteLog(Exception e) => $"iDevelop could not write this attempt's log, so it stopped the client. {e.Message}";

    private StartProblem.CannotRecord CannotRecord(Exception e) => new($"iDevelop could not write {_attempts}. {e.Message}");

    /// <summary>The run of another instance that holds the task's lock, as <see cref="Latest"/> shows it.</summary>
    private StartProblem AnotherWindowsRun(TaskId task) =>
        Latest.TryGetValue(task, out var running) && running.Status == AttemptStatus.Running
            ? new StartProblem.AlreadyRunning(running.Task, running.TaskTitle)
            : new StartProblem.RunInAnotherWindow();

    private void Publish(AttemptRecord record, long logRevision)
    {
        lock (_gate)
        {
            Latest = Latest.SetItem(record.Task, record);
            _logRevisions[(record.Task, record.Id)] = logRevision;
        }

        if (record.Status == AttemptStatus.Running)
        {
            NotifyChanged(record.Task);
        }
    }

    private void Finish(ActiveRun run)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(run.Record.Task, out var current) && current == run)
            {
                _active.Remove(run.Record.Task);
            }

            Latest = Latest.SetItem(run.Record.Task, run.Record);
            _logRevisions[(run.Record.Task, run.Record.Id)] = run.Live.LogRevision;
        }

        NotifyChanged(run.Record.Task);
        Advance();
    }

    private abstract record LockTake
    {
        /// <summary>The caller holds the lock, and <see cref="Latest"/> is read again and reconciled.</summary>
        public sealed record Taken(RunLock Lock) : LockTake;

        /// <summary>Another instance holds the lock, and <see cref="Latest"/> is read again.</summary>
        public sealed record HeldElsewhere(StartProblem Problem) : LockTake;

        /// <summary>The lock file could not be opened. Nothing was read.</summary>
        public sealed record Failed(StartProblem Problem) : LockTake;
    }
}
