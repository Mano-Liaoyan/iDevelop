using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// <c>.idp/attempts/&lt;task-id&gt;/run.lock</c>, held with FileShare.None for a run's whole life: an exclusive handle on
/// Windows and an exclusive flock on Linux and macOS. The operating system releases it when its holder dies, so holding it
/// is owning the task's run, and an attempt that reads as running while its task's lock is free was left by a crash. The
/// file is never deleted, because then two instances could lock two different files.
/// </summary>
internal sealed class RunLock : IDisposable
{
    private readonly FileStream _handle;

    private RunLock(FileStream handle) => _handle = handle;

    /// <summary>Null when another handle holds it, in this process or another one. Never waits.</summary>
    public static RunLock? TryTake(string attemptsFolder, TaskId task)
    {
        var folder = Directory.CreateDirectory(Path.Combine(attemptsFolder, task.ToString())).FullName;
        try
        {
            return new RunLock(new FileStream(Path.Combine(folder, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// The attempts of one open project. It lives as long as the project is open in a window.
/// Several tasks of a project folder can run at once, and each task runs at most once at a time, across every iDevelop
/// instance.
/// </summary>
public sealed class ProjectRuns : IAsyncDisposable
{
    private const string LeaveReason = "The project was closed while this task ran.";

    private readonly Lock _gate = new();
    private readonly string _projectFolder;
    private readonly ClientDirectory _clients;
    private readonly string _attempts;
    private readonly HashSet<AttemptId> _started = [];
    private readonly Dictionary<TaskId, ActiveRun> _active = [];
    private Task? _leaving;

    private ProjectRuns(string projectFolder, ClientDirectory clients, ImmutableDictionary<TaskId, AttemptRecord> latest, ImmutableArray<string> warnings)
    {
        _projectFolder = projectFolder;
        _clients = clients;
        _attempts = DataFolder.Attempts(projectFolder);
        Latest = latest;
        Warnings = warnings;
    }

    /// <summary>How long leaving waits for a stopped run to end before it gives up on it. Tests shorten it.</summary>
    internal TimeSpan LeaveTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The newest attempt of each task that has one.</summary>
    public ImmutableDictionary<TaskId, AttemptRecord> Latest { get; private set; }

    /// <summary>Attempt folders that could not be read or settled. Each one is a sentence for the user.</summary>
    public ImmutableArray<string> Warnings { get; private set; }

    /// <summary>The attempts this window runs, while they run, oldest first.</summary>
    public ImmutableArray<AttemptRecord> Active
    {
        get
        {
            lock (_gate)
            {
                return [.. _active.Values.Select(run => run.Record).Where(record => record.Status == AttemptStatus.Running).OrderBy(record => record.Id)];
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
    /// starts, and at a start that another window's run refuses, which reads every task's newest attempt again. A start
    /// raises it on its caller's thread and a run from a worker thread, in order. By a run's last one,
    /// <see cref="Active"/> no longer holds it and its task is free. The attempt is settled, unless leaving gave up on a client
    /// that did not end; that attempt stays on record as running, and the next open settles it.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Reads the newest attempt of each task. It also settles each attempt that a crashed instance left running and that no
    /// instance runs now: it stops the client if it is provably the same process, and records it as interrupted. A folder
    /// without attempts stays untouched.
    /// </summary>
    public static ProjectRuns Open(string projectFolder, ClientDirectory clients)
    {
        var folder = Path.GetFullPath(projectFolder);
        var attempts = DataFolder.Attempts(folder);
        var (latest, warnings) = AttemptLog.ReadLatest(attempts);
        var notes = warnings.ToBuilder();
        latest = Reconcile(attempts, latest, notes);
        return new ProjectRuns(folder, clients, latest, notes.ToImmutable());
    }

    /// <summary>The reason this task cannot start now, or null. A run of it in another window shows up only at
    /// <see cref="Start"/>, because checking for it would take the task's lock.</summary>
    public StartProblem? Check(TaskDefinition task)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(task.Id, out var run))
            {
                return new StartProblem.AlreadyRunning(run.Record.Task, run.Record.TaskTitle);
            }
        }

        return StartCheck.Evaluate(task, _projectFolder, _clients.Current) is StartVerdict.Blocked blocked ? blocked.Problem : null;
    }

    /// <summary>
    /// Checks the task, takes the task's run lock, settles any attempt a crash left running, records the attempt, and
    /// launches the client. Every reason not to launch comes back as <see cref="StartResult.Refused"/>.
    /// </summary>
    public StartResult Start(TaskDefinition task)
    {
        StartResult result;
        ActiveRun? run = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            if (_active.TryGetValue(task.Id, out var running))
            {
                return new StartResult.Refused(new StartProblem.AlreadyRunning(running.Record.Task, running.Record.TaskTitle));
            }

            var verdict = StartCheck.Evaluate(task, _projectFolder, _clients.Current);
            if (verdict is StartVerdict.Blocked blocked)
            {
                return new StartResult.Refused(blocked.Problem);
            }

            var plan = ((StartVerdict.Allowed)verdict).Plan;
            RunLock? held;
            try
            {
                held = RunLock.TryTake(_attempts, task.Id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return CannotRecord(e);
            }

            if (held is null)
            {
                result = new StartResult.Refused(ReadAnotherWindowsRun(task));
            }
            else
            {
                AttemptLog log;
                AttemptEvent.Requested requested;
                try
                {
                    var (latest, warnings) = AttemptLog.ReadLatest(_attempts);
                    var notes = warnings.ToBuilder();
                    Latest = Reconcile(_attempts, latest, notes, held: task.Id);
                    Warnings = notes.ToImmutable();
                    DataFolder.EnsureGitIgnore(_projectFolder);
                    requested = new AttemptEvent.Requested(
                        DateTimeOffset.UtcNow, AttemptId.New(), task.Id, task.Title, plan.Settings, plan.Prompt, plan.Command.Path, plan.Launch.Arguments);
                    log = AttemptLog.Create(_attempts, requested);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    held.Dispose();
                    return CannotRecord(e);
                }

                (var record, run) = Launch(plan, AttemptReducer.Start(requested), log, held);
                _started.Add(record.Id);
                if (run is not null)
                {
                    _active[task.Id] = run;
                }

                Latest = Latest.SetItem(task.Id, record);
                result = new StartResult.Started(record);
            }
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            run?.Start();
        }

        return result;
    }

    /// <summary>Stops the task's client and every process it started, and records the attempt as cancelled.
    /// Does nothing unless this window runs that task's attempt.</summary>
    public void Cancel(TaskId task)
    {
        ActiveRun? run;
        lock (_gate)
        {
            _active.TryGetValue(task, out run);
        }

        run?.Stop(new AttemptEvent.CancelRequested(DateTimeOffset.UtcNow));
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
            _leaving ??= Task.WhenAll(_active.Values.ToList().Select(run => LeaveAsync(run, LeaveTimeout)));
            return new ValueTask(_leaving);
        }
    }

    private static async Task LeaveAsync(ActiveRun run, TimeSpan timeout)
    {
        run.Stop(new AttemptEvent.InterruptRequested(DateTimeOffset.UtcNow, LeaveReason));
        if (await Task.WhenAny(run.Completion, Task.Delay(timeout)) != run.Completion)
        {
            run.Abandon();
            await run.Completion;
        }
    }

    /// <summary>
    /// Settles each attempt that reads as running and whose task's lock is free, so no live instance runs it. A run of this
    /// window holds its task's lock and is skipped. <paramref name="held"/> names a task whose lock the caller holds.
    /// </summary>
    private static ImmutableDictionary<TaskId, AttemptRecord> Reconcile(
        string attempts, ImmutableDictionary<TaskId, AttemptRecord> latest, ImmutableArray<string>.Builder warnings, TaskId? held = null)
    {
        foreach (var running in latest.Values.Where(record => record.Status == AttemptStatus.Running).ToList())
        {
            if (running.Task == held)
            {
                latest = Settle(attempts, latest, running, warnings);
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
                    var (now, _) = AttemptLog.ReadLatest(attempts);
                    if (now.TryGetValue(running.Task, out var record) && record is { Status: AttemptStatus.Running })
                    {
                        latest = Settle(attempts, latest, record, warnings);
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

    private static ImmutableDictionary<TaskId, AttemptRecord> Settle(
        string attempts, ImmutableDictionary<TaskId, AttemptRecord> latest, AttemptRecord record, ImmutableArray<string>.Builder warnings)
    {
        ProcessMatch? match = record.Process is { } process ? ProcessCheck.Match(process) : null;
        if (match == ProcessMatch.Same)
        {
            ProcessCheck.KillTree(record.Process!.Value);
        }

        var reconciled = new AttemptEvent.Reconciled(DateTimeOffset.UtcNow, match);
        try
        {
            using (var log = AttemptLog.Open(AttemptLog.FolderOf(attempts, record.Task, record.Id)))
            {
                log.Append(reconciled);
            }

            return latest.SetItem(record.Task, AttemptReducer.Apply(record, reconciled));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"iDevelop could not record that \"{record.TaskTitle}\" was interrupted. {e.Message}");
            return latest;
        }
    }

    /// <summary>Starts the client and records its process. A client that does not start is a failed attempt, and then the
    /// log and the lock are released at once.</summary>
    private (AttemptRecord Record, ActiveRun? Run) Launch(LaunchPlan plan, AttemptRecord record, AttemptLog log, RunLock held)
    {
        ChildProcess process;
        try
        {
            process = ChildProcess.Start(plan.Command, plan.Launch.Arguments, _projectFolder);
        }
        catch (LaunchException e)
        {
            var failed = new AttemptEvent.LaunchFailed(DateTimeOffset.UtcNow, e.Message);
            try
            {
                log.Append(failed);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The log then ends at its request, and the next open settles it.
            }
            finally
            {
                log.Dispose();
                held.Dispose();
            }

            return (AttemptReducer.Apply(record, failed), null);
        }

        // On Windows a crash stops the client through its job. On Linux and macOS, a crash before this line is on disk
        // leaves a client that reconciliation cannot identify.
        var launched = new AttemptEvent.Launched(DateTimeOffset.UtcNow, process.Identity.Id, process.Identity.StartedAt);
        try
        {
            log.Append(launched);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            process.StopTree();
            process.Dispose();
            log.Dispose();
            held.Dispose();
            return (AttemptReducer.Abandon(record, CannotWriteLog(e), DateTimeOffset.UtcNow), null);
        }

        var running = AttemptReducer.Apply(record, launched);
        return (running, new ActiveRun(this, plan, process, log, held, running));
    }

    private static string CannotWriteLog(Exception e) => $"iDevelop could not write this attempt's log, so it stopped the client. {e.Message}";

    private StartResult.Refused CannotRecord(Exception e) => new(new StartProblem.CannotRecord($"iDevelop could not write {_attempts}. {e.Message}"));

    /// <summary>Another instance holds the lock, so the read settles nothing.</summary>
    private StartProblem ReadAnotherWindowsRun(TaskDefinition task)
    {
        (Latest, Warnings) = AttemptLog.ReadLatest(_attempts);
        return Latest.TryGetValue(task.Id, out var running) && running.Status == AttemptStatus.Running
            ? new StartProblem.AlreadyRunning(running.Task, running.TaskTitle)
            : new StartProblem.RunInAnotherWindow();
    }

    private void Publish(AttemptRecord record)
    {
        lock (_gate)
        {
            Latest = Latest.SetItem(record.Task, record);
        }

        if (record.Status == AttemptStatus.Running)
        {
            Changed?.Invoke(this, EventArgs.Empty);
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
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One run. A channel puts every event in one order: stdout lines, stop requests, and the exit. One drain appends
    /// each event to the log, folds it, and publishes the record, in that order. A stop request enqueued before the kill
    /// therefore always precedes the exit the kill causes. The lock is released only after the last event is on disk,
    /// or after the drain stops and the log is closed, so nothing appends after another instance could take over.
    /// The run disposes its process just before it releases the lock, so a late stop does nothing. While the client runs,
    /// Cancel and leaving stop everything it started, and so does a crash on Windows. What it leaves running when it exits
    /// on its own keeps running, as after a command in a terminal.
    /// </summary>
    private sealed class ActiveRun(ProjectRuns owner, LaunchPlan plan, ChildProcess process, AttemptLog log, RunLock held, AttemptRecord record)
    {
        // A process the client started can hold the pipes open after the client exits. The attempt settles without the
        // rest of its output, which the closed log ignores.
        private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

        private readonly Channel<AttemptEvent> _events = Channel.CreateUnbounded<AttemptEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _abandon = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AttemptRecord Record { get; private set; } = record;

        /// <summary>Completes once the lock is released.</summary>
        public Task Completion => _finished.Task;

        public void Start() => _ = Task.Run(RunAsync);

        public void Stop(AttemptEvent request)
        {
            Request(request);
            process.StopTree();
        }

        /// <summary>Stops the drain at its next event, without waiting for the client's exit. The attempt stays on record as running.</summary>
        public void Abandon() => _abandon.Cancel();

        private void Request(AttemptEvent e) => _events.Writer.TryWrite(e);

        private async Task RunAsync()
        {
            try
            {
                var stderrTail = new Tail();
                _ = process.WriteStdinAsync(plan.Launch.Stdin, close: true);
                var stdout = process.ReadStdoutAsync(Interpret);
                var stderr = process.ReadStderrAsync(line =>
                {
                    log.AppendStderr(line);
                    stderrTail.Add(line);
                });
                _ = WatchExitAsync(stdout, stderr, stderrTail);
                await DrainAsync();
            }
            finally
            {
                _finished.TrySetResult();
            }
        }

        private async Task DrainAsync()
        {
            var exited = false;
            try
            {
                await foreach (var e in _events.Reader.ReadAllAsync(_abandon.Token))
                {
                    // Leaving gave up while this drain was busy. Events already queued stay off the log too.
                    _abandon.Token.ThrowIfCancellationRequested();
                    log.Append(e);
                    exited |= e is AttemptEvent.Exited;
                    Record = AttemptReducer.Apply(Record, e);
                    owner.Publish(Record);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Record = AttemptReducer.Abandon(Record, CannotWriteLog(e), DateTimeOffset.UtcNow);
            }
            finally
            {
                // Whatever ended the drain early, including a Changed handler that threw, no client runs on unobserved.
                // After its exit, whatever the client started for the user, such as a dev server, keeps running.
                if (exited)
                {
                    process.LeaveDescendantsRunning();
                }
                else
                {
                    process.StopTree();
                }

                process.Dispose();
                log.Dispose();
                held.Dispose();
                owner.Finish(this);
            }
        }

        private void Interpret(string line)
        {
            log.AppendOutput(line);
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            ImmutableArray<AgentEvent> events;
            try
            {
                events = plan.Client.Interpret(line);
            }
            catch (JsonException)
            {
                events = [new AgentEvent.Notice($"iDevelop could not read a line that {Clients.Name(plan.Settings.Client)} printed.")];
            }

            foreach (var e in events)
            {
                Request(new AttemptEvent.Agent(DateTimeOffset.UtcNow, e));
            }
        }

        private async Task WatchExitAsync(Task stdout, Task stderr, Tail stderrTail)
        {
            try
            {
                var code = await process.WaitForExitAsync();
                await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(ExitGrace));
                Request(new AttemptEvent.Exited(DateTimeOffset.UtcNow, code, stderrTail.Text));
            }
            finally
            {
                _events.Writer.TryComplete();
            }
        }
    }

    /// <summary>The last few kilobytes of the client's stderr, for the exit event.</summary>
    private sealed class Tail
    {
        private const int Limit = 4096;

        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public void Add(string line)
        {
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > Limit)
                {
                    _text.Remove(0, _text.Length - Limit);
                }
            }
        }
    }
}
