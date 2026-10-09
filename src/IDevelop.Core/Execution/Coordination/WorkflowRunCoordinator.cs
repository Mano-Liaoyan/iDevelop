using System.Collections.Immutable;
using System.Threading.Channels;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Runs one approved workflow run in this window: it schedules the run's tasks along their dependency connections,
/// starts every task that is ready at once, each with its own client root, and gives each settled turn its disposition
/// through E3a. It holds the run's <see cref="CoordinatorPermit"/>, so a second window only reads the run.
/// <para>
/// Every decision happens on one loop, after a durable reread of the journal. Preparation, launch, settlement,
/// publication, closure, and reconciliation run outside it as work items that post their outcome back. Only the loop
/// changes the coordinator's own state, which is just the work in flight and the refusals it has not recorded.
/// </para>
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    internal const string ElsewhereMessage = "This run is controlled by another iDevelop window.";
    internal const string StoppedReason = "The workflow was stopped before this task started.";

    /// <summary>
    /// A safety bound on the client roots one run starts or runs at once, far above any workflow a person draws, so in
    /// practice every ready task starts at once. It is no setting (ADR 0018).
    /// </summary>
    internal const int ClientRootLimit = 32;

    private static readonly RunCommand Accepted = new RunCommand.Accepted();

    private readonly ProjectRuns _runs;
    private readonly RunStore _store;
    private readonly Func<Materializer> _materializer;
    private readonly CoordinatorPermit? _permit;
    private readonly Channel<Action> _inbox = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly Task _loop;
    private readonly Lock _viewGate = new();
    private readonly List<(Func<RunView, bool> Condition, TaskCompletionSource<RunView> Done)> _waiters = [];
    private RunView _view;
    private volatile bool _halting;
    private Task? _disposed;

    // Owned by the loop.
    private bool _resumed;
    private bool _stopping;
    private bool _retrying;
    private bool _pinsReleasing;
    private bool _pinsReleased;
    private string? _problem;
    private long _decisions;
    private readonly Dictionary<TaskId, Live> _live = [];
    private readonly Dictionary<TaskId, TaskHold> _holds = [];
    private readonly Dictionary<TaskId, SettledTurn> _handles = [];
    // Turns whose settlement stayed unresolved, which keep their task's lease until a recorded receipt lets them go.
    private readonly Dictionary<TaskId, UnresolvedTurn> _unresolved = [];
    private readonly List<Task> _inflight = [];

    internal WorkflowRunCoordinator(ProjectRuns runs, RunStore store, Func<Materializer> materializer, RunAddress address, CoordinatorPermit? permit)
    {
        _runs = runs;
        _store = store;
        _materializer = materializer;
        _permit = permit;
        Address = address;
        _view = Project(Read() ?? throw new InvalidOperationException("The run could not be read."));
        _loop = Task.Run(Loop);
        Post(() => { });
    }

    public RunAddress Address { get; }

    /// <summary>False in a window that only reads the run because another window controls it.</summary>
    public bool Controlled => _permit is not null;

    /// <summary>The newest projection. <see cref="Changed"/> follows each new one.</summary>
    public RunView View
    {
        get { lock (_viewGate) return _view; }
    }

    /// <summary>Raised on the coordinator's loop after each decision.</summary>
    public event EventHandler? Changed;

    /// <summary>The permit this window controls the run with, for the commands of later steps that need it.</summary>
    internal CoordinatorPermit? Permit => _permit;

    /// <summary>
    /// Authorizes scheduling in this window. A new run starts its ready tasks. A reopened run first reconciles every
    /// unclosed claim and finishes pending publications, and never launches a claimed turn again. A repeat is harmless,
    /// and it clears the refusals this window has not recorded, so they are tried again.
    /// </summary>
    public Task<RunCommand> Resume(RunAddress address, CancellationToken wait = default) => Command(address, record =>
    {
        if (record.Phase != RunPhase.Approved) return new RunCommand.Refused(new(RunProblem.RunStopped));
        _resumed = true;
        _holds.Clear();
        return Accepted;
    }, wait);

    /// <summary>
    /// Records the run's stop before anything else. Then no turn can be claimed, running turns are cancelled, waiting
    /// attempts close without sending queued text, and the run settles as stopped once every attempt is closed. A claim
    /// that won before the stop settles or reconciles first. Cancelling <paramref name="wait"/> does not revoke a recorded stop.
    /// </summary>
    public Task<RunCommand> Stop(RunAddress address, OperationId command, CancellationToken wait = default) => Command(address, _ =>
        _store.Stop(_permit!, command) switch
        {
            RunDecision.Rejected rejected => new RunCommand.Refused(rejected.Reason),
            _ => Accepted,
        }, wait);

    /// <summary>
    /// Amends the run from a planner's proposal, against the revision the person saw. The journal records the amendment
    /// first, and the workflow document follows it through <see cref="AmendmentProjection"/>. A repeat, under the same
    /// confirmation or another one, returns the recorded amendment. A task that is reserved or started keeps its
    /// definition and inputs.
    /// </summary>
    public Task<RunCommand> Amend(RunAddress address, RunAmendment amendment, CancellationToken wait = default) => Command(address, _ =>
    {
        var decision = _store.AmendFromProposal(_permit!, RunOperations.Amend(amendment.Confirmation), amendment.Previous, amendment.Proposal,
            amendment.Chosen.ToHashSet(), amendment.Confirmation, amendment.Fallback);
        return decision is RunDecision.Rejected rejected ? new RunCommand.Refused(rejected.Reason) : Accepted;
    }, wait);

    /// <summary>
    /// Adds <paramref name="task"/> to a run that a node's Run started, as the person's Run of another node does while the
    /// run is active (#90). The journal records it once under <paramref name="confirmation"/>, so a repeat or a retry after
    /// a busy refusal adds it once, and a reopened run starts it after Resume. A task with a dependency predecessor that has
    /// no current result is refused with <see cref="RunProblem.MissingDependencyResult"/>, which names that predecessor.
    /// </summary>
    public Task<RunCommand> Request(RunAddress address, TaskId task, OperationId confirmation, CancellationToken wait = default) => Command(address, _ =>
        _store.Request(_permit!, RunOperations.Request(confirmation, task), task) switch
        {
            RunDecision.Rejected rejected => new RunCommand.Refused(rejected.Reason),
            _ => Accepted,
        }, wait);

    /// <summary>Rereads the journal, after a change this window did not make, such as a restore or a person's recovery.</summary>
    public void Refresh() => Post(() => { });

    /// <summary>The waits of <see cref="Until"/> still registered.</summary>
    internal int Waiting
    {
        get { lock (_viewGate) return _waiters.Count; }
    }

    /// <summary>Completes with the first projection that satisfies <paramref name="condition"/>.</summary>
    internal Task<RunView> Until(Func<RunView, bool> condition, CancellationToken wait = default)
    {
        var done = new TaskCompletionSource<RunView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = (condition, done);
        lock (_viewGate)
        {
            if (condition(_view)) return Task.FromResult(_view);
            _waiters.Add(waiter);
        }
        if (wait.CanBeCanceled)
        {
            // A cancelled wait takes its waiter with it.
            var registration = wait.Register(() =>
            {
                lock (_viewGate) _waiters.Remove(waiter);
                done.TrySetCanceled(wait);
            });
            _ = done.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        return done.Task;
    }

    /// <summary>Stops scheduling at once. Work in flight still finishes, so settled turns can still give up their leases.</summary>
    internal void Halt()
    {
        _halting = true;
        Post(() => { });
    }

    /// <summary>
    /// Halts, waits up to the project's leave timeout for work in flight, gives up the leases of settled turns whose
    /// disposition did not release, and gives up the permit. The host calls it after its turns have left, so their
    /// settlements still have the permit's authority.
    /// </summary>
    internal Task DisposeAsync()
    {
        lock (_viewGate) _disposed ??= DisposeCore();
        return _disposed;
    }

    private async Task DisposeCore()
    {
        Halt();
        var deadline = Task.Delay(_runs.LeaveTimeout, _runs.TimeProvider);
        while (true)
        {
            var snapshot = new TaskCompletionSource<Task[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Post(() => snapshot.TrySetResult([.. _inflight]))) break;
            var pending = await snapshot.Task.ConfigureAwait(false);
            if (pending.Length == 0) break;
            var all = Task.WhenAll(pending);
            if (await Task.WhenAny(all, deadline).ConfigureAwait(false) != all) break;
        }
        _inbox.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        // A settled turn whose disposition did not release keeps its lease only while this window can still dispose of
        // it. Its durable record stays, and the next window reconciles it.
        foreach (var handle in _handles.Values) handle.Lease.Dispose();
        _handles.Clear();
        _permit?.Dispose();
    }

    private Task<RunCommand> Command(RunAddress address, Func<RunRecord, RunCommand> body, CancellationToken wait)
    {
        var done = new TaskCompletionSource<RunCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            if (_permit is null) done.TrySetResult(new RunCommand.Unavailable(ElsewhereMessage));
            else if (!Same(address)) done.TrySetResult(new RunCommand.Refused(new(RunProblem.IdentityMismatch)));
            else if (Read() is not { } record) done.TrySetResult(new RunCommand.Refused(new(RunProblem.StorageUnavailable)));
            else done.TrySetResult(body(record));
        }
        if (!Post(() =>
            {
                try { Run(); }
                catch (Exception error) { done.TrySetException(error); }
            }))
            done.TrySetResult(new RunCommand.Refused(new(RunProblem.RunStopped)));
        return done.Task.WaitAsync(wait);
    }

    private bool Same(RunAddress address) => address.Workflow == Address.Workflow && address.Run == Address.Run &&
        ProjectFolders.Comparer.Equals(ProjectFolders.OnDisk(address.Project), Address.Project);

    private bool Post(Action action) => _inbox.Writer.TryWrite(action);

    private async Task Loop()
    {
        while (await _inbox.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_inbox.Reader.TryRead(out var action))
            {
                try { action(); }
                catch (Exception error) { _problem = error.Message; }
            }
            try { Decide(); }
            catch (Exception error) { _problem = error.Message; }
        }
    }

    /// <summary>The journal now, or null when it cannot be read. Safe off the loop.</summary>
    private RunRecord? Record() => _store.Read(Address.Workflow, Address.Run) is RunRead.Loaded loaded ? loaded.Record : null;

    private RunRecord? Read()
    {
        if (Record() is { } record) return record;
        _problem = "The run's journal could not be read.";
        return null;
    }

    private RunView Project(RunRecord record) => RunProjection.Of(Address, record, Log,
        _live.ToDictionary(pair => pair.Key, pair => pair.Value.Stage), _holds, Controlled, _resumed) with { Problem = _problem, PinsReleased = _pinsReleased, Decision = _decisions };

    private AttemptRecord? Log(RunAttempt attempt)
    {
        try { return AttemptEvidence.Read(_store.AttemptFolder(Address.Workflow, Address.Run, attempt.Task, attempt.Id)).Record; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Decide()
    {
        _decisions++;
        if (Read() is not { } record)
        {
            Publish(View with { Problem = _problem, Decision = _decisions });
            return;
        }
        if (_permit is not null && !_halting) record = Act(record);
        Publish(Project(record));
    }

    private void Publish(RunView view)
    {
        List<TaskCompletionSource<RunView>> satisfied = [];
        lock (_viewGate)
        {
            _view = view;
            foreach (var waiter in _waiters.ToArray())
            {
                if (!waiter.Condition(view)) continue;
                _waiters.Remove(waiter);
                satisfied.Add(waiter.Done);
            }
        }
        foreach (var done in satisfied) done.TrySetResult(view);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Transient(RunProblem? problem) =>
        problem is RunProblem.TaskBusy or RunProblem.JournalBusy or RunProblem.StorageUnavailable or RunProblem.RunBusy;

    private void Hold(TaskId task, TaskHold hold)
    {
        _holds[task] = hold;
        if (hold.Retried) RetryLater();
    }

    /// <summary>Clears every transient hold after the project's retry delay, so their steps run again.</summary>
    private void RetryLater()
    {
        if (_retrying) return;
        _retrying = true;
        Background(async () =>
        {
            await Task.Delay(_runs.CoordinatorRetry, _runs.TimeProvider).ConfigureAwait(false);
            return true;
        }, _ =>
        {
            _retrying = false;
            foreach (var task in _holds.Where(pair => pair.Value.Retried).Select(pair => pair.Key).ToArray()) _holds.Remove(task);
        }, _ => _retrying = false);
    }

    /// <summary>Runs <paramref name="work"/> off the loop and its outcome back on it. Disposal waits for both.</summary>
    private void Background<T>(Func<Task<T>> work, Action<T> done, Action<Exception> failed)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inflight.Add(finished.Task);
        _ = Task.Run(work).ContinueWith(completed =>
        {
            if (!Post(() =>
                {
                    _inflight.Remove(finished.Task);
                    try
                    {
                        if (completed.IsCompletedSuccessfully) done(completed.Result);
                        else failed(completed.Exception?.GetBaseException() ?? new OperationCanceledException());
                    }
                    finally { finished.TrySetResult(); }
                }))
                finished.TrySetResult();
        }, TaskScheduler.Default);
    }

    private void Offload<T>(Func<T> work, Action<T> done, Action<Exception> failed) => Background(() => Task.FromResult(work()), done, failed);

    /// <summary>This window's work for a task. Starting and Running each hold one of the run's client slots.</summary>
    private sealed record Live(LiveStage Stage, RunningTurn? Turn = null, bool Cancelled = false);

    private ImmutableArray<TaskId> Slotted => [.. _live.Where(pair => pair.Value.Stage is LiveStage.Starting or LiveStage.Running).Select(pair => pair.Key)];
}
