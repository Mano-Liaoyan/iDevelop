using System.Diagnostics;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    private readonly Dictionary<(WorkflowId Workflow, RunId Run, OperationId Operation), TurnCommand> _commands = [];
    private readonly Dictionary<(WorkflowId Workflow, RunId Run, LaunchKey Launch), TurnOwner> _owned = [];

    internal Action<string>? Probe { get; set; }
    internal Func<Stream, Stream>? RequestStream { get; set; }
    internal IReadOnlyDictionary<string, string>? GitEnvironment { get; set; }
    internal TimeProvider? MaterializerClock { get; set; }
    internal RunStore? Store { get; set; }
    internal Func<ChildProcess, bool>? StopSeam { get; set; }

    private RunStore TurnStore => Store ?? RunStore.Open(_projectFolder);
    private Materializer TurnMaterializer(RunStore store) => Materializer.Open(_projectFolder, store, null,
        MaterializerClock ?? TimeProvider, GitEnvironment, point => Probe?.Invoke(point));

    internal Task<TurnStart> StartTurn(CoordinatorPermit permit, TurnIntent intent, CancellationToken wait = default)
    {
        Task<TurnStart>? command = null;
        LaunchKey? existing = null;
        var duplicate = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            var operation = Operation(intent);
            var key = (permit.Workflow, permit.Run, operation);
            if (_commands.TryGetValue(key, out var prior))
            {
                if (prior.Intent != intent) return Task.FromResult<TurnStart>(new TurnStart.Refused(new(RunProblem.OperationConflict)));
                if (!prior.Task.IsCompleted)
                {
                    command = prior.Task;
                    duplicate = true;
                }
                else if (prior.Task.IsCompletedSuccessfully && LaunchOf(prior.Task.Result) is { } launch)
                    existing = launch;
                else _commands.Remove(key);
            }
            if (command is null && existing is null)
            {
                command = Task.Run(() => StartTurnCore(permit, intent));
                _commands.Add(key, new(intent, command));
            }
        }
        if (existing is { } completed) return Task.FromResult<TurnStart>(Existing(permit, completed)).WaitAsync(wait);
        return duplicate ? ExistingWhenComplete(command!, permit, wait) : command!.WaitAsync(wait);
    }

    private async Task<TurnStart> ExistingWhenComplete(Task<TurnStart> command, CoordinatorPermit permit, CancellationToken wait)
    {
        var outcome = await command.WaitAsync(wait);
        return LaunchOf(outcome) is { } launch ? Existing(permit, launch) : outcome;
    }

    private static OperationId Operation(TurnIntent intent) => intent switch
    {
        TurnIntent.First first => first.Operation,
        TurnIntent.Next next => next.Operation,
        _ => throw new InvalidOperationException(),
    };

    private static LaunchKey? LaunchOf(TurnStart start) => start switch
    {
        TurnStart.Started started => started.Turn.Address.Launch,
        TurnStart.Existing existing => existing.Launch,
        TurnStart.Settled { Settlement: TurnSettlement.Settled settled } => settled.Turn.Address.Launch,
        TurnStart.Settled { Settlement: TurnSettlement.Unresolved unresolved } => unresolved.Turn.Address.Launch,
        _ => null,
    };

    private TurnStart.Existing Existing(CoordinatorPermit permit, LaunchKey launch)
    {
        TurnOwner? owner;
        lock (_gate) owner = _owned.GetValueOrDefault((permit.Workflow, permit.Run, launch));
        if (owner is null) return new(launch, null, null);
        var snapshot = owner.Snapshot();
        return new(launch, snapshot.Running, snapshot.Outcome);
    }

    private async Task<TurnStart> StartTurnCore(CoordinatorPermit permit, TurnIntent intent)
    {
        if (intent is TurnIntent.First { Cause: not (AttemptCause.Initial or AttemptCause.Retry) })
            return new TurnStart.Refused(new(RunProblem.UnsupportedWork));
        var store = TurnStore;
        var materializer = TurnMaterializer(store);
        RunLease? lease = null;
        AttemptLog? log = null;
        TurnOwner? owner = null;
        ActiveRun? active = null;
        try
        {
            Probe?.Invoke("runner.lookup");
            ClaimCheck? LookupClaim() => intent switch
            {
                TurnIntent.First first => materializer.ClaimedLaunch(permit, first.Operation, first.Task, first.Cause, first.BasePrompt),
                TurnIntent.Next next => materializer.ClaimedLaunch(permit, next.Operation, next.Launch, next.Prompt),
                _ => throw new InvalidOperationException(),
            };
            var lookup = LookupClaim();
            if (lookup is ClaimCheck.Existing existing) return Existing(permit, existing.Claim.Key);
            if (lookup is ClaimCheck.Rejected rejectedLookup) return new TurnStart.Refused(rejectedLookup.Reason);
            var read = store.Read(permit.Workflow, permit.Run);
            if (read is RunRead.Rejected unavailable) return new TurnStart.Refused(unavailable.Reason);
            var record = ((RunRead.Loaded)read).Record;
            TaskId task;
            if (intent is TurnIntent.First initial) task = initial.Task;
            else if (intent is TurnIntent.Next later && record.Attempts.TryGetValue(later.Launch.Attempt, out var original)) task = original.Task;
            else return new TurnStart.Refused(new(RunProblem.UnknownAttempt));
            if (permit.TakeTask(task) is not LeaseTake.Taken taken) return new TurnStart.Refused(new(RunProblem.TaskBusy));
            lease = taken.Lease;
            var preparation = intent switch
            {
                TurnIntent.First first => await materializer.Prepare(lease, first.Operation, first.Cause, first.BasePrompt),
                TurnIntent.Next next => await materializer.PrepareTurn(lease, next.Operation, next.Launch, next.Prompt),
                _ => throw new InvalidOperationException(),
            };
            if (preparation is Preparation.Blocked blocked) return new TurnStart.Blocked(blocked.Block);
            if (preparation is Preparation.Rejected rejected) return new TurnStart.Refused(rejected.Reason);
            var ready = (Preparation.Ready)preparation;
            var prepared = ready.Execution;
            Probe?.Invoke("runner.prepared");
            read = store.Read(permit.Workflow, permit.Run);
            if (read is RunRead.Rejected unavailablePreparation) return new TurnStart.Refused(unavailablePreparation.Reason);
            record = ((RunRead.Loaded)read).Record;
            var attempt = record.Attempts[prepared.Launch.Attempt];
            var definition = record.Revisions[attempt.Revision].Snapshot.Tasks[task];
            var folder = store.AttemptFolder(permit.Workflow, permit.Run, task, attempt.Id);
            var evidence = intent is TurnIntent.Next ? AttemptEvidence.Read(folder) : null;
            if (evidence is { Rejection: { } invalid }) return new TurnStart.Refused(invalid);
            var verdict = StartCheck.Evaluate(definition, ready.Checkout, _clients.Current,
                new Resumption(evidence?.Record?.SessionId, prepared.Prompt), questions: _questions);
            if (verdict is StartVerdict.Blocked client) return new TurnStart.Refused(new(RunProblem.TaskUnconfigured), client.Problem);
            var plan = ((StartVerdict.Allowed)verdict).Plan;
            plan = plan with { Request = plan.Request with { WorkingFolder = ready.Checkout } };
            var readOnly = definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review;
            var tree = readOnly ? GitTree.Snapshot(ready.Checkout) : null;
            if (intent is TurnIntent.First)
            {
                Probe?.Invoke("runner.request.before");
                var binding = new RunBinding(permit.Workflow, permit.Run, attempt.Revision, prepared.Inputs);
                if (File.Exists(Path.Combine(folder, "events.jsonl")))
                {
                    evidence = AttemptEvidence.Read(folder);
                    if (evidence.Rejection is not null || evidence.Events.FirstOrDefault() is not AttemptEvent.Requested requested ||
                        requested.Attempt != attempt.Id || requested.Task != task || requested.RunBinding != binding || requested.Prompt != prepared.Prompt)
                        return new TurnStart.Refused(new(RunProblem.EvidenceMismatch));
                    log = AttemptLog.Open(folder);
                }
                else
                {
                    log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
                        new AttemptEvent.Requested(TimeProvider.GetUtcNow(), attempt.Id, task, definition.Title, plan.Settings,
                            prepared.Prompt, plan.Command.Path, plan.Launch.Arguments)
                        {
                            RunBinding = binding, Conversation = definition.Conversation, ReadOnly = readOnly,
                            Fix = record.ReviewOf(attempt.Id), Tree = tree,
                        }, RequestStream);
                    evidence = AttemptEvidence.Read(folder);
                }
                Probe?.Invoke("runner.request.after");
            }
            else
            {
                if (evidence?.Record is not { BetweenTurns: true }) return new TurnStart.Refused(new(RunProblem.EvidenceMismatch));
                log = AttemptLog.Open(folder);
            }
            lock (_gate)
            {
                if (_leaving is not null) return new TurnStart.Refused(new(RunProblem.RunStopped));
            }
            Probe?.Invoke("runner.claim.before");
            ClaimCheck claim;
            string? claimFailure = null;
            try { claim = materializer.CheckInputsAndClaim(lease, Operation(intent), prepared.Launch); }
            catch (Exception error)
            {
                if (LookupClaim() is not ClaimCheck.Existing recorded) throw;
                claim = new ClaimCheck.Granted(recorded.Claim);
                claimFailure = error.Message;
            }
            if (claim is ClaimCheck.Blocked or ClaimCheck.Rejected && LookupClaim() is ClaimCheck.Existing claimed)
            {
                claimFailure = claim is ClaimCheck.Blocked failure ? failure.Block.Detail : ((ClaimCheck.Rejected)claim).Reason.Problem.ToString();
                claim = new ClaimCheck.Granted(claimed.Claim);
            }
            if (claim is ClaimCheck.Blocked blockedClaim) return new TurnStart.Blocked(blockedClaim.Block);
            if (claim is ClaimCheck.Rejected rejectedClaim) return new TurnStart.Refused(rejectedClaim.Reason);
            if (claim is ClaimCheck.Existing existingClaim) return Existing(permit, existingClaim.Claim.Key);
            owner = new TurnOwner(this, store, materializer, lease.Transfer(),
                new(record.Repository!, permit.Workflow, permit.Run, task, prepared.Launch), Operation(intent), prepared, log, definition);
            lease = null;
            log = null;
            lock (_gate) _owned.Add((permit.Workflow, permit.Run, prepared.Launch), owner);
            if (claimFailure is not null) return new TurnStart.Settled(await owner.NotStarted(claimFailure));
            Probe?.Invoke("runner.claim.after");
            if (intent is TurnIntent.Next)
            {
                Probe?.Invoke("runner.turn-requested.before");
                owner.Log!.Append(new AttemptEvent.TurnRequested(TimeProvider.GetUtcNow(), prepared.Prompt, plan.Command.Path, plan.Launch.Arguments)
                {
                    Conversation = definition.Conversation, Consumed = [.. evidence!.Record!.Queued.Select(message => message.Id)], Tree = tree,
                });
                owner.MarkRequested();
                Probe?.Invoke("runner.turn-requested.after");
            }
            Probe?.Invoke("runner.launch.before");
            var launchEvidence = AttemptEvidence.Read(folder);
            if (launchEvidence.Record is null || launchEvidence.Rejection is not null)
                return new TurnStart.Settled(await owner.NotStarted("The turn's request could not be read."));
            active = owner.Launch(() =>
            {
                var process = ChildProcess.Start(plan.Command, plan.Launch.Arguments, ready.Checkout, ProcessLifetime.Workflow);
                return new ActiveRun(this, 0, plan, process, owner.Log!, new RunOwnership.Workflow(owner), launchEvidence.Record);
            }, run => new(owner.Address, owner.RootExited.Task, owner.Settlement.Task,
                () => run.StopAsync(new AttemptEvent.CancelRequested(TimeProvider.GetUtcNow())),
                (text, stopTurn) => owner.SendAsync(run, text, stopTurn)));
            if (active is null) return new TurnStart.Settled(await owner.NotStarted("The project was closed before the client started."));
            Probe?.Invoke("runner.launch.after");
            active.Launched();
            active.Start();
            Probe?.Invoke("runner.running");
            return new TurnStart.Started(owner.Snapshot().Running!);
        }
        catch (Exception error) when (owner is not null)
        {
            if (active is not null)
            {
                active.BreakLog();
                active.Start();
                return new TurnStart.Started(owner.Snapshot().Running!);
            }
            return new TurnStart.Settled(await owner.NotStarted(error.Message));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new TurnStart.Refused(new(RunProblem.StorageUnavailable));
        }
        finally
        {
            log?.Dispose();
            lease?.Dispose();
        }
    }

    internal Task<Reconciliation> Reconcile(CoordinatorPermit permit, OperationId operation, LaunchKey launch, CancellationToken wait = default)
    {
        TurnOwner? owner;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            owner = _owned.GetValueOrDefault((permit.Workflow, permit.Run, launch));
        }
        var settlement = owner?.Reconcile();
        var reconciliation = settlement is null ? Task.Run(() => ReconcileCore(permit, operation, launch)) : Found(settlement);
        return reconciliation.WaitAsync(wait);

        static async Task<Reconciliation> Found(Task<TurnSettlement> pending) => new Reconciliation.Found(await pending);
    }

    private async Task<Reconciliation> ReconcileCore(CoordinatorPermit permit, OperationId operation, LaunchKey launch)
    {
        var store = TurnStore;
        if (store.Read(permit.Workflow, permit.Run) is not RunRead.Loaded loaded) return new Reconciliation.Refused(new(RunProblem.StorageUnavailable));
        var record = loaded.Record;
        if (!record.Attempts.TryGetValue(launch.Attempt, out var attempt)) return new Reconciliation.Refused(new(RunProblem.InvalidClaim));
        var task = attempt.Task;
        if (permit.TakeTask(task) is not LeaseTake.Taken taken) return new Reconciliation.Refused(new(RunProblem.TaskBusy));
        if (!record.Claims.ContainsKey(launch))
        {
            taken.Lease.Dispose();
            return new Reconciliation.Refused(new(RunProblem.InvalidClaim));
        }
        var owner = new TurnOwner(this, store, TurnMaterializer(store), taken.Lease,
            new(record.Repository!, permit.Workflow, permit.Run, task, launch), operation, record.Preparations[launch], null,
            record.Revisions[record.Attempts[launch.Attempt].Revision].Snapshot.Tasks[task], adopted: true);
        bool closing;
        lock (_gate)
        {
            closing = _leaving is not null;
            if (!closing) _owned.Add((permit.Workflow, permit.Run, launch), owner);
        }
        if (closing)
        {
            taken.Lease.Dispose();
            return new Reconciliation.Refused(new(RunProblem.RunStopped));
        }
        return new Reconciliation.Found(await owner.Reconcile()!);
    }

    private void Forget(TurnOwner owner)
    {
        var address = owner.Address;
        lock (_gate)
        {
            var key = (address.Workflow, address.Run, address.Launch);
            if (_owned.GetValueOrDefault(key) == owner) _owned.Remove(key);
            foreach (var command in _commands.Where(pair => pair.Key.Workflow == address.Workflow && pair.Key.Run == address.Run &&
                pair.Value.Task.IsCompletedSuccessfully && LaunchOf(pair.Value.Task.Result) == address.Launch).Select(pair => pair.Key).ToArray())
                _commands.Remove(command);
        }
    }

    private StartProblem? WorkflowOwner(TaskId task)
    {
        var owner = _owned.Values.FirstOrDefault(value => value.Address.Task == task && value.Lease.Held);
        if (owner is null) return null;
        var name = WorkflowOf(task)?.DisplayName;
        if (owner.Store.Read(owner.Address.Workflow, owner.Address.Run) is RunRead.Loaded loaded)
        {
            var attempt = loaded.Record.Attempts[owner.Address.Launch.Attempt];
            name = loaded.Record.Revisions[attempt.Revision].Snapshot.DisplayName;
        }
        return new StartProblem.RunOwned(name ?? owner.Address.Workflow.ToString());
    }

    private sealed record TurnCommand(TurnIntent Intent, Task<TurnStart> Task);

    private abstract record RunOwnership
    {
        private RunOwnership() { }
        internal sealed record Standalone(StandaloneLease Lease) : RunOwnership;
        internal sealed record Workflow(TurnOwner Owner) : RunOwnership;
    }

    private abstract record TurnState
    {
        private TurnState() { }
        public sealed record Claimed(bool Requested) : TurnState;
        public sealed record Running(ActiveRun Active) : TurnState;
        public sealed record Observing : TurnState;
        public sealed record Finalizing(RootObservation Root) : TurnState;
        public sealed record Settled(TurnSettlement Outcome, LogCheckpoint? Checkpoint, RootObservation? Root) : TurnState;
        public sealed record Retrying(Task<TurnSettlement> Retry, LogCheckpoint? Checkpoint, RootObservation? Root) : TurnState;
        public sealed record Faulted(Exception Error) : TurnState;
        public sealed record Released(TurnDisposition Receipt, TurnSettlement Outcome) : TurnState;
        public sealed record Adopted : TurnState;
    }

    private sealed class TurnOwner(ProjectRuns project, RunStore store, Materializer materializer, RunLease lease,
        ExecutionAddress address, OperationId operation, PreparedExecution preparation, AttemptLog? log, TaskDefinition definition,
        bool adopted = false)
    {
        private readonly Lock _gate = new();
        private TurnState _state = adopted ? new TurnState.Adopted() : new TurnState.Claimed(address.Launch.Turn == 1);
        private RunningTurn? _running;
        public readonly RunStore Store = store;
        public readonly Materializer Materializer = materializer;
        public readonly RunLease Lease = lease;
        public readonly ExecutionAddress Address = address;
        public readonly PreparedExecution Preparation = preparation;
        public readonly OperationId SettleOperation = OperationIds.Derive(operation, "settle");
        private readonly OperationId _rootOperation = OperationIds.Derive(operation, "root");
        public readonly TaskCompletionSource RootExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RootObservation> _root = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<TurnSettlement> Settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskDefinition Definition = definition;
        public readonly AttemptLog? Log = log;

        public ActiveRun? Launch(Func<ActiveRun> create, Func<ActiveRun, RunningTurn> handle)
        {
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Claimed when !project.Closing:
                        var active = create();
                        _running = handle(active);
                        _state = new TurnState.Running(active);
                        return active;
                    case TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or
                        TurnState.Settled or TurnState.Retrying or TurnState.Faulted or TurnState.Released or TurnState.Adopted:
                        return null;
                    default: throw new UnreachableException();
                }
            }
        }

        public void MarkRequested()
        {
            lock (_gate)
            {
                _state = _state switch
                {
                    TurnState.Claimed { Requested: false } => new TurnState.Claimed(true),
                    TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or TurnState.Settled or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Released or TurnState.Adopted => _state,
                    _ => throw new UnreachableException(),
                };
            }
        }

        public (RunningTurn? Running, TurnSettlement? Outcome) Snapshot()
        {
            lock (_gate)
            {
                return (_running, _state switch
                {
                    TurnState.Settled settled => settled.Outcome,
                    TurnState.Released released => released.Outcome,
                    TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Adopted => null,
                    _ => throw new UnreachableException(),
                });
            }
        }

        public Task<SendResult> SendAsync(ActiveRun active, string text, bool stopTurn)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.EmptyMessage()));
            return (NodeWorks.For(Definition.Blueprint.Work) as IConverses)?.Receive(text) switch
            {
                MessageUse.Guidance guidance => active.GuideAsync(Definition, guidance.Text, default, null),
                MessageUse.Turn turn => active.SendAsync(Definition, turn.Prompt, stopTurn, default, null),
                _ => Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.NoConversation()))),
            };
        }

        public void Shutdown()
        {
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Running running:
                        _ = running.Active.StopAsync(new AttemptEvent.InterruptRequested(project.TimeProvider.GetUtcNow(), LeaveReason));
                        break;
                    case TurnState.Claimed or TurnState.Observing or TurnState.Finalizing or TurnState.Settled or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Released or TurnState.Adopted:
                        break;
                    default: throw new UnreachableException();
                }
            }
        }

        public async Task Leave()
        {
            Task pending;
            lock (_gate)
            {
                pending = _state switch
                {
                    TurnState.Retrying retrying => retrying.Retry,
                    TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or
                        TurnState.Settled or TurnState.Faulted or TurnState.Released or TurnState.Adopted => Settlement.Task,
                    _ => throw new UnreachableException(),
                };
            }
            await Task.WhenAny(pending, Task.Delay(project.LeaveTimeout, project.TimeProvider));
            ActiveRun? active = null;
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Running running:
                        active = running.Active;
                        break;
                    case TurnState.Claimed or TurnState.Observing or TurnState.Finalizing or TurnState.Settled or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Released or TurnState.Adopted:
                        break;
                    default: throw new UnreachableException();
                }
            }
            active?.Abandon(new(project.TimeProvider.GetUtcNow(), LeaveReason));
        }

        public bool RootAbandoned
        {
            get
            {
                lock (_gate)
                {
                    return _state switch
                    {
                        TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Adopted => false,
                        TurnState.Finalizing or TurnState.Settled or TurnState.Retrying or
                            TurnState.Faulted or TurnState.Released => !_root.Task.IsCompleted,
                        _ => throw new UnreachableException(),
                    };
                }
            }
        }

        public bool ObserveExit(RootExit exit)
        {
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Running:
                        _state = new TurnState.Observing();
                        break;
                    case TurnState.Claimed or TurnState.Observing or TurnState.Finalizing or TurnState.Settled or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Released or TurnState.Adopted:
                        return false;
                    default: throw new UnreachableException();
                }
            }
            Observe(exit);
            return true;
        }

        private RootObservation Observe(RootExit exit)
        {
            RootObservation root;
            try { root = Materializer.ObserveRootExit(Lease, _rootOperation, Address.Launch, exit, project.ShutdownTime); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { root = new RootObservation.Rejected(new(RunProblem.StorageUnavailable)); }
            catch (Exception error)
            {
                Fault(error);
                throw;
            }
            lock (_gate)
            {
                _state = _state switch
                {
                    TurnState.Observing => new TurnState.Finalizing(root),
                    TurnState.Claimed or TurnState.Running or TurnState.Finalizing or TurnState.Settled or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Released or TurnState.Adopted => throw new UnreachableException(),
                    _ => throw new UnreachableException(),
                };
            }
            _root.TrySetResult(root);
            switch (root)
            {
                case RootObservation.Rejected rejected:
                    RootExited.TrySetException(new InvalidOperationException(rejected.Reason.Problem.ToString()));
                    break;
                case RootObservation.Observed or RootObservation.Fenced:
                    RootExited.TrySetResult();
                    break;
                default: throw new UnreachableException();
            }
            return root;
        }

        public async Task<RootObservation?> WaitForRoot()
        {
            if (await Task.WhenAny(_root.Task, Task.Delay(project.ShutdownTime, project.TimeProvider)) == _root.Task)
                return await _root.Task;
            TurnSettlement? outcome = null;
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Running:
                        outcome = Unresolved(UnresolvedReason.Uncertain);
                        _state = new TurnState.Settled(outcome, null, null);
                        break;
                    case TurnState.Observing or TurnState.Finalizing or TurnState.Faulted:
                        break;
                    case TurnState.Settled or TurnState.Retrying or TurnState.Released:
                        if (!_root.Task.IsCompleted) return null;
                        break;
                    case TurnState.Claimed or TurnState.Adopted:
                        throw new UnreachableException();
                    default: throw new UnreachableException();
                }
            }
            if (outcome is null) return await _root.Task;
            RootExited.TrySetException(new InvalidOperationException("The client did not exit after it was stopped."));
            Settlement.TrySetResult(outcome);
            return null;
        }

        public TurnSettlement Unresolved(UnresolvedReason reason, RunRejection? rejection = null, ProcessMatch? root = null) =>
            new TurnSettlement.Unresolved(new(Release, Address, reason, rejection, root, Lease));

        public TurnSettlement Complete(TurnSettlement outcome, LogCheckpoint? checkpoint, RootObservation? root)
        {
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Released released: return released.Outcome;
                    case TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or
                        TurnState.Settled or TurnState.Retrying or TurnState.Faulted or TurnState.Adopted:
                        _state = new TurnState.Settled(outcome, checkpoint, root);
                        break;
                    default: throw new UnreachableException();
                }
            }
            switch (outcome)
            {
                case TurnSettlement.Settled:
                    RootExited.TrySetResult();
                    break;
                case TurnSettlement.Unresolved unresolved:
                    RootExited.TrySetException(new InvalidOperationException(unresolved.Turn.Rejection?.Problem.ToString() ?? unresolved.Turn.Reason.ToString()));
                    break;
                default: throw new UnreachableException();
            }
            Settlement.TrySetResult(outcome);
            return outcome;
        }

        public void Fault(Exception error)
        {
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Released: return;
                    case TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or
                        TurnState.Settled or TurnState.Retrying or TurnState.Faulted or TurnState.Adopted:
                        _state = new TurnState.Faulted(error);
                        break;
                    default: throw new UnreachableException();
                }
            }
            _root.TrySetException(error);
            RootExited.TrySetException(error);
            Settlement.TrySetException(error);
        }

        public async Task<TurnSettlement> NotStarted(string detail)
        {
            bool requested;
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Claimed claimed:
                        requested = claimed.Requested;
                        _state = new TurnState.Observing();
                        break;
                    case TurnState.Faulted faulted: throw faulted.Error;
                    case TurnState.Running or TurnState.Observing or TurnState.Finalizing or TurnState.Settled or
                        TurnState.Retrying or TurnState.Released or TurnState.Adopted:
                        throw new UnreachableException();
                    default: throw new UnreachableException();
                }
            }
            RootObservation? root = null;
            LogCheckpoint? checkpoint = null;
            try
            {
                root = Observe(new RootExit.NotStarted(detail));
                if (requested && Log is not null) Log.Append(new AttemptEvent.LaunchFailed(project.TimeProvider.GetUtcNow(), detail));
                Log?.Dispose();
                checkpoint = AttemptEvidence.Read(Folder).Checkpoint;
                return await Settle(checkpoint, root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log?.Dispose();
                RootExited.TrySetException(new InvalidOperationException("The turn's root observation could not be recorded."));
                return Complete(Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.StorageUnavailable)), checkpoint, root);
            }
            catch (Exception error)
            {
                Log?.Dispose();
                Fault(error);
                throw;
            }
        }

        private string Folder => Store.AttemptFolder(Address.Workflow, Address.Run, Address.Task, Address.Launch.Attempt);

        public async Task<TurnSettlement> Settle(LogCheckpoint? checkpoint, RootObservation root) =>
            Complete(await SettlementFor(checkpoint, root), checkpoint, root);

        private async Task<TurnSettlement> SettlementFor(LogCheckpoint? checkpoint, RootObservation? root)
        {
            if (!Lease.Held) return Unresolved(UnresolvedReason.OwnershipConflict, new(RunProblem.TaskBusy));
            switch (root)
            {
                case RootObservation.Fenced: return Unresolved(UnresolvedReason.OwnershipConflict);
                case RootObservation.Rejected rejected: return Unresolved(UnresolvedReason.OwnershipConflict, rejected.Reason);
                case RootObservation.Observed or null: break;
                default: throw new UnreachableException();
            }
            if (checkpoint is null) return Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.EvidenceMismatch));
            var settlement = await Materializer.Settle(Lease, SettleOperation, Address.Launch, checkpoint);
            return settlement switch
            {
                Execution.Settlement.Closed => BuildSettled(),
                Execution.Settlement.Rejected rejected => Unresolved(UnresolvedReason.IncompleteEvidence, rejected.Reason),
                _ => throw new UnreachableException(),
            };
        }

        private TurnSettlement BuildSettled()
        {
            if (Store.Read(Address.Workflow, Address.Run) is not RunRead.Loaded loaded)
                return Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.StorageUnavailable));
            var record = loaded.Record;
            var checkpoint = record.TurnClosures[Address.Launch];
            var evidence = AttemptEvidence.ReadPrefix(Folder, checkpoint);
            if (evidence.Record is null || evidence.Rejection is not null)
                return Unresolved(UnresolvedReason.IncompleteEvidence, evidence.Rejection);
            var closure = record.Receipts.Values.Select(receipt => receipt.Event).OfType<RunEvent.TurnClosed>().Single(value => value.Key == Address.Launch);
            return new TurnSettlement.Settled(new(Release, Address, record.Preparations[Address.Launch], checkpoint,
                record.RootExits[Address.Launch], closure, record.Dispositions[record.Settlements[Address.Launch]],
                evidence.Record, evidence.Events.OfType<AttemptEvent.CleanedUp>().LastOrDefault(), Lease));
        }

        public Task<TurnSettlement>? Reconcile()
        {
            TaskCompletionSource<Task<TurnSettlement>> start;
            Task<TurnSettlement> retry;
            LogCheckpoint? checkpoint;
            RootObservation? root;
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Settled { Outcome: TurnSettlement.Settled } settled: return Task.FromResult(settled.Outcome);
                    case TurnState.Settled settled:
                        checkpoint = settled.Checkpoint;
                        root = settled.Root;
                        break;
                    case TurnState.Faulted or TurnState.Adopted:
                        checkpoint = null;
                        root = null;
                        break;
                    case TurnState.Retrying retrying: return retrying.Retry;
                    case TurnState.Released: return null;
                    case TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing: return Settlement.Task;
                    default: throw new UnreachableException();
                }
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                retry = start.Task.Unwrap();
                _state = new TurnState.Retrying(retry, checkpoint, root);
            }
            start.SetResult(Task.Run(async () =>
            {
                TurnSettlement outcome;
                try
                {
                    outcome = checkpoint is not null && root is RootObservation.Observed
                        ? await SettlementFor(checkpoint, root) : await Recover();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { outcome = Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.StorageUnavailable)); }
                catch (Exception error)
                {
                    Fault(error);
                    throw;
                }
                return Complete(outcome, checkpoint, root);
            }));
            return retry;
        }

        private async Task<TurnSettlement> Recover()
        {
            if (Store.Read(Address.Workflow, Address.Run) is not RunRead.Loaded loaded)
                return Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.StorageUnavailable));
            var record = loaded.Record;
            if (!record.Claims.ContainsKey(Address.Launch))
                return Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.InvalidClaim));
            if (!Lease.Held) return Unresolved(UnresolvedReason.OwnershipConflict);
            if (record.TurnClosures.ContainsKey(Address.Launch)) return BuildSettled();
            if (!record.RootExits.ContainsKey(Address.Launch))
            {
                var events = AttemptEvidence.Read(Folder).Events;
                var index = 1;
                ProcessIdentity? identity = null;
                foreach (var e in events)
                {
                    if (e is AttemptEvent.TurnRequested) index++;
                    if (index == Address.Launch.Turn && e is AttemptEvent.Launched launched)
                        identity = new(launched.ProcessId, launched.ProcessStarted);
                }
                return Unresolved(UnresolvedReason.Uncertain, root: identity is null ? null : ProcessCheck.Check(identity.Value));
            }
            var result = await Materializer.RecoverSettlement(Lease, SettleOperation, Address.Launch);
            return result switch
            {
                Execution.Settlement.Closed => BuildSettled(),
                Execution.Settlement.Rejected rejected => Unresolved(UnresolvedReason.IncompleteEvidence, rejected.Reason),
                _ => throw new UnreachableException(),
            };
        }

        public Release Release()
        {
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Released released: return new Release.Released(released.Receipt);
                    case TurnState.Settled: break;
                    case TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Adopted:
                        return new Release.Held(new(RunProblem.NotSettled));
                    default: throw new UnreachableException();
                }
            }
            var receipt = TurnReceipts.Receipt(Store, Address);
            if (receipt is null) return new Release.Held(new(RunProblem.NotSettled));
            lock (_gate)
            {
                switch (_state)
                {
                    case TurnState.Settled settled:
                        _state = new TurnState.Released(receipt, settled.Outcome);
                        break;
                    case TurnState.Released released: return new Release.Released(released.Receipt);
                    case TurnState.Claimed or TurnState.Running or TurnState.Observing or TurnState.Finalizing or
                        TurnState.Retrying or TurnState.Faulted or TurnState.Adopted:
                        return new Release.Held(new(RunProblem.NotSettled));
                    default: throw new UnreachableException();
                }
            }
            Lease.Dispose();
            project.Forget(this);
            return new Release.Released(receipt);
        }
    }
}
