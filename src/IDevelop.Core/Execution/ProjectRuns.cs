using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// <c>.idp/attempts/run.lock</c>, held with FileShare.None for a run's whole life: an exclusive handle on Windows and an
/// exclusive flock on Linux and macOS. The operating system releases it when its holder dies, so holding it is owning the
/// project's run, and an attempt that reads as running while the lock is free was left by a crash. The file is never
/// deleted, because then two instances could lock two different files.
/// </summary>
internal sealed class RunLock : IDisposable
{
    private readonly FileStream _handle;

    private RunLock(FileStream handle) => _handle = handle;

    /// <summary>Null when another handle holds it, in this process or another one. Never waits.</summary>
    public static RunLock? TryTake(string attemptsFolder)
    {
        Directory.CreateDirectory(attemptsFolder);
        try
        {
            return new RunLock(new FileStream(Path.Combine(attemptsFolder, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
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
/// At most one task of a project folder runs at a time, across every iDevelop instance.
/// </summary>
public sealed class ProjectRuns : IAsyncDisposable
{
    private const string LeaveReason = "The project was closed while this task ran.";

    private static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly ClientDirectory _clients;
    private readonly string _attempts;
    private ActiveRun? _active;
    private Task? _leaving;

    private ProjectRuns(string projectFolder, ClientDirectory clients, ImmutableDictionary<TaskId, AttemptRecord> latest, ImmutableArray<string> warnings)
    {
        ProjectFolder = projectFolder;
        _clients = clients;
        _attempts = DataFolder.Attempts(projectFolder);
        Latest = latest;
        Warnings = warnings;
    }

    public string ProjectFolder { get; }

    /// <summary>The newest attempt of each task that has one.</summary>
    public ImmutableDictionary<TaskId, AttemptRecord> Latest { get; private set; }

    /// <summary>Attempt folders that could not be read or settled. Each one is a sentence for the user.</summary>
    public ImmutableArray<string> Warnings { get; private set; }

    /// <summary>The attempt this window runs, while it runs.</summary>
    public AttemptRecord? Active
    {
        get
        {
            lock (_gate)
            {
                return _active?.Record is { Status: AttemptStatus.Running } record ? record : null;
            }
        }
    }

    /// <summary>
    /// Raised with each new state of an attempt this window starts. The first comes from <see cref="Start"/> on its caller's
    /// thread and the rest from a worker thread, in order. By the last one, <see cref="Active"/> is null and the folder is
    /// free. Its status is settled, unless leaving gave up on a client that did not end; that attempt stays on record as
    /// running, and the next open settles it.
    /// </summary>
    public event EventHandler<AttemptRecord>? Changed;

    /// <summary>
    /// Reads the newest attempt of each task. When no instance runs a task of this folder, it also settles any attempt that
    /// a crashed instance left running: it stops the client if it is provably the same process, and records it as
    /// interrupted. A folder without attempts stays untouched.
    /// </summary>
    public static ProjectRuns Open(string projectFolder, ClientDirectory clients)
    {
        var folder = Path.GetFullPath(projectFolder);
        var attempts = DataFolder.Attempts(folder);
        var (latest, warnings) = AttemptLog.ReadLatest(attempts);
        var notes = warnings.ToBuilder();
        if (latest.Values.Any(record => record.Status == AttemptStatus.Running))
        {
            try
            {
                using var held = RunLock.TryTake(attempts);
                if (held is not null)
                {
                    // The run may have ended between the first read and the lock, so settle what the logs say now.
                    (latest, warnings) = AttemptLog.ReadLatest(attempts);
                    notes = warnings.ToBuilder();
                    latest = Reconcile(attempts, latest, notes);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                notes.Add($"iDevelop could not settle the attempts that a closed window left running. {e.Message}");
            }
        }

        return new ProjectRuns(folder, clients, latest, notes.ToImmutable());
    }

    /// <summary>The reason this task cannot start now, or null. A run in another window shows up only at <see cref="Start"/>,
    /// because checking for it would take the lock.</summary>
    public StartProblem? Check(TaskDefinition task)
    {
        lock (_gate)
        {
            if (_active is { } run)
            {
                return new StartProblem.AlreadyRunning(run.Record.Task, run.Record.TaskTitle);
            }
        }

        return StartCheck.Evaluate(task, _clients.Current) is StartVerdict.Blocked blocked ? blocked.Problem : null;
    }

    /// <summary>
    /// Checks the task, takes the project's run lock, settles any attempt a crash left running, records the attempt, and
    /// launches the client. Every reason not to launch comes back as <see cref="StartResult.Refused"/>.
    /// </summary>
    public StartResult Start(TaskDefinition task)
    {
        AttemptRecord record;
        ActiveRun? run;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            if (_active is { } running)
            {
                return new StartResult.Refused(new StartProblem.AlreadyRunning(running.Record.Task, running.Record.TaskTitle));
            }

            var verdict = StartCheck.Evaluate(task, _clients.Current);
            if (verdict is StartVerdict.Blocked blocked)
            {
                return new StartResult.Refused(blocked.Problem);
            }

            var plan = ((StartVerdict.Allowed)verdict).Plan;
            RunLock? held;
            try
            {
                held = RunLock.TryTake(_attempts);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return CannotRecord(e);
            }

            if (held is null)
            {
                return new StartResult.Refused(RunningElsewhere());
            }

            AttemptLog log;
            AttemptEvent.Requested requested;
            try
            {
                var (latest, warnings) = AttemptLog.ReadLatest(_attempts);
                var notes = warnings.ToBuilder();
                Latest = Reconcile(_attempts, latest, notes);
                Warnings = notes.ToImmutable();
                DataFolder.EnsureGitIgnore(ProjectFolder);
                requested = new AttemptEvent.Requested(
                    DateTimeOffset.UtcNow, AttemptId.New(), task.Id, task.Title, plan.Settings, plan.Prompt, plan.Command.Path, plan.Launch.Arguments);
                log = AttemptLog.Create(_attempts, requested);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                held.Dispose();
                return CannotRecord(e);
            }

            (record, run) = Launch(plan, AttemptReducer.Start(requested), log, held);
            _active = run;
            Latest = Latest.SetItem(task.Id, record);
        }

        try
        {
            Changed?.Invoke(this, record);
        }
        finally
        {
            run?.Start();
        }

        return new StartResult.Started(record);
    }

    /// <summary>Stops the task's client and every process it started, and records the attempt as cancelled.
    /// Does nothing unless this window runs that task's attempt.</summary>
    public void Cancel(TaskId task)
    {
        ActiveRun? run;
        lock (_gate)
        {
            run = _active;
        }

        if (run?.Record.Task == task)
        {
            run.Stop(new AttemptEvent.CancelRequested(DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Leaving the project. It stops a running client's process tree, records the attempt as interrupted, and releases the
    /// folder. If the client has not ended 10 seconds after the stop, the attempt stays on record as running, and the
    /// next open settles it.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _leaving ??= LeaveAsync(_active);
            return new ValueTask(_leaving);
        }
    }

    private static async Task LeaveAsync(ActiveRun? run)
    {
        if (run is null)
        {
            return;
        }

        run.Stop(new AttemptEvent.InterruptRequested(DateTimeOffset.UtcNow, LeaveReason));
        if (await Task.WhenAny(run.Completion, Task.Delay(LeaveTimeout)) != run.Completion)
        {
            run.Abandon();
            await run.Completion;
        }
    }

    /// <summary>Settles each attempt that reads as running. The caller holds the run lock, so no live instance owns them.</summary>
    private static ImmutableDictionary<TaskId, AttemptRecord> Reconcile(
        string attempts, ImmutableDictionary<TaskId, AttemptRecord> latest, ImmutableArray<string>.Builder warnings)
    {
        foreach (var record in latest.Values.Where(record => record.Status == AttemptStatus.Running))
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

                latest = latest.SetItem(record.Task, AttemptReducer.Apply(record, reconciled));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"iDevelop could not record that \"{record.TaskTitle}\" was interrupted. {e.Message}");
            }
        }

        return latest;
    }

    /// <summary>Starts the client and records its process. A client that does not start is a failed attempt, and then the
    /// log and the lock are released at once.</summary>
    private (AttemptRecord Record, ActiveRun? Run) Launch(LaunchPlan plan, AttemptRecord record, AttemptLog log, RunLock held)
    {
        ChildProcess process;
        try
        {
            process = ChildProcess.Start(plan.Command, plan.Launch.Arguments, ProjectFolder);
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

        // Known gap: a crash before this line is on disk leaves a client that reconciliation cannot identify.
        // A Windows Job Object that kills its processes on close could cover it later.
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

    private StartProblem RunningElsewhere() =>
        AttemptLog.ReadLatest(_attempts).Latest.Values.Where(record => record.Status == AttemptStatus.Running).MaxBy(record => record.Id) is { } running
            ? new StartProblem.AlreadyRunning(running.Task, running.TaskTitle)
            : new StartProblem.RunInAnotherWindow();

    private void Publish(AttemptRecord record)
    {
        lock (_gate)
        {
            Latest = Latest.SetItem(record.Task, record);
        }

        if (record.Status == AttemptStatus.Running)
        {
            Changed?.Invoke(this, record);
        }
    }

    private void Finish(ActiveRun run)
    {
        lock (_gate)
        {
            if (_active == run)
            {
                _active = null;
            }

            Latest = Latest.SetItem(run.Record.Task, run.Record);
        }

        Changed?.Invoke(this, run.Record);
    }

    /// <summary>
    /// One run. A channel puts every event in one order: stdout lines, stop requests, and the exit. One drain appends
    /// each event to the log, folds it, and publishes the record, in that order. A stop request enqueued before the kill
    /// therefore always precedes the exit the kill causes. The lock is released only after the last event is on disk,
    /// or after the drain stops and the log is closed, so nothing appends after another instance could take over.
    /// The run never disposes its process: the open handle keeps a late stop from reaching a process that reused the id.
    /// </summary>
    private sealed class ActiveRun(ProjectRuns owner, LaunchPlan plan, ChildProcess process, AttemptLog log, RunLock held, AttemptRecord record)
    {
        // A process the client started can hold the pipes open after the client exits.
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

        /// <summary>Stops the drain without waiting for the client's exit. The attempt stays on record as running.</summary>
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
                if (!exited)
                {
                    process.StopTree();
                }

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
