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
        Task<TurnStart> command;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_leaving is not null, this);
            var operation = Operation(intent);
            var key = (permit.Workflow, permit.Run, operation);
            if (_commands.TryGetValue(key, out var prior))
            {
                if (prior.Intent != intent) return Task.FromResult<TurnStart>(new TurnStart.Refused(new(RunProblem.OperationConflict)));
                if (!prior.Task.IsCompleted) return ExistingWhenComplete(prior.Task, permit, wait);
                if (prior.Task.IsCompletedSuccessfully && LaunchOf(prior.Task.Result) is { } launch)
                    return Task.FromResult<TurnStart>(Existing(permit, launch)).WaitAsync(wait);
                _commands.Remove(key);
            }
            command = Task.Run(() => StartTurnCore(permit, intent));
            _commands.Add(key, new(intent, command));
        }
        return command.WaitAsync(wait);
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
        lock (owner.Gate) return new(launch, owner.Running, owner.Outcome);
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
            bool closing;
            lock (_gate)
            {
                _owned.Add((permit.Workflow, permit.Run, prepared.Launch), owner);
                closing = _leaving is not null;
            }
            if (closing)
            {
                owner.Leave();
                _ = owner.LeaveAsync();
            }
            if (claimFailure is not null) return new TurnStart.Settled(await owner.NotStarted(claimFailure));
            Probe?.Invoke("runner.claim.after");
            if (intent is TurnIntent.Next)
            {
                Probe?.Invoke("runner.turn-requested.before");
                owner.Log!.Append(new AttemptEvent.TurnRequested(TimeProvider.GetUtcNow(), prepared.Prompt, plan.Command.Path, plan.Launch.Arguments)
                {
                    Conversation = definition.Conversation, Consumed = [.. evidence!.Record!.Queued.Select(message => message.Id)], Tree = tree,
                });
                owner.Requested = true;
                Probe?.Invoke("runner.turn-requested.after");
            }
            else owner.Requested = true;
            Probe?.Invoke("runner.launch.before");
            var launchEvidence = AttemptEvidence.Read(folder);
            if (launchEvidence.Record is null || launchEvidence.Rejection is not null)
                return new TurnStart.Settled(await owner.NotStarted("The turn's request could not be read."));
            string? notStarted = null;
            lock (owner.Gate)
            {
                if (owner.Leaving) notStarted = "The project was closed before the client started.";
                else
                {
                    var process = ChildProcess.Start(plan.Command, plan.Launch.Arguments, ready.Checkout, ProcessLifetime.Workflow);
                    owner.Active = new ActiveRun(this, 0, plan, process, owner.Log!, new RunOwnership.Workflow(owner), launchEvidence.Record);
                    owner.Running = new(owner.Address, owner.RootExited.Task, owner.Settlement.Task,
                        () => owner.Active.StopAsync(new AttemptEvent.CancelRequested(TimeProvider.GetUtcNow())),
                        owner.SendAsync);
                }
            }
            if (notStarted is not null) return new TurnStart.Settled(await owner.NotStarted(notStarted));
            Probe?.Invoke("runner.launch.after");
            owner.Active!.Launched();
            owner.Active.Start();
            Probe?.Invoke("runner.running");
            return new TurnStart.Started(owner.Running!);
        }
        catch (Exception error) when (owner is not null)
        {
            if (owner.Active is { } active)
            {
                active.BreakLog();
                active.Start();
                return new TurnStart.Started(owner.Running!);
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

    internal Task<TurnSettlement> Reconcile(CoordinatorPermit permit, OperationId operation, LaunchKey launch, CancellationToken wait = default)
    {
        Task<TurnSettlement> reconciliation;
        lock (_gate)
        {
            if (_owned.TryGetValue((permit.Workflow, permit.Run, launch), out var owner))
                reconciliation = owner.Reconcile();
            else reconciliation = Task.Run(() => ReconcileCore(permit, operation, launch));
        }
        return reconciliation.WaitAsync(wait);
    }

    private async Task<TurnSettlement> ReconcileCore(CoordinatorPermit permit, OperationId operation, LaunchKey launch)
    {
        var store = TurnStore;
        if (store.Read(permit.Workflow, permit.Run) is not RunRead.Loaded loaded) return new TurnSettlement.Refused(new(RunProblem.StorageUnavailable));
        var record = loaded.Record;
        if (!record.Attempts.TryGetValue(launch.Attempt, out var attempt)) return new TurnSettlement.Refused(new(RunProblem.InvalidClaim));
        var task = attempt.Task;
        if (permit.TakeTask(task) is not LeaseTake.Taken taken) return new TurnSettlement.Refused(new(RunProblem.TaskBusy));
        if (!record.Claims.ContainsKey(launch))
        {
            taken.Lease.Dispose();
            return new TurnSettlement.Refused(new(RunProblem.InvalidClaim));
        }
        var owner = new TurnOwner(this, store, TurnMaterializer(store), taken.Lease,
            new(record.Repository!, permit.Workflow, permit.Run, task, launch), operation, record.Preparations[launch], null,
            record.Revisions[record.Attempts[launch.Attempt].Revision].Snapshot.Tasks[task]);
        bool closing;
        lock (_gate)
        {
            _owned.Add((permit.Workflow, permit.Run, launch), owner);
            closing = _leaving is not null;
        }
        if (closing)
        {
            owner.Leave();
            _ = owner.LeaveAsync();
        }
        return await owner.Recover();
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

    private sealed class TurnOwner(ProjectRuns project, RunStore store, Materializer materializer, RunLease lease,
        ExecutionAddress address, OperationId operation, PreparedExecution preparation, AttemptLog? log, TaskDefinition definition)
    {
        public readonly Lock Gate = new();
        public readonly RunStore Store = store;
        public readonly Materializer Materializer = materializer;
        public readonly RunLease Lease = lease;
        public readonly ExecutionAddress Address = address;
        public readonly PreparedExecution Preparation = preparation;
        public readonly OperationId SettleOperation = OperationIds.Derive(operation, "settle");
        public readonly TaskCompletionSource RootExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<RootObservation> RootStep = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<TurnSettlement> Settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskDefinition Definition = definition;
        public readonly AttemptLog? Log = log;
        public bool Requested = address.Launch.Turn == 1;
        public bool Leaving;
        public bool Detached;
        public ActiveRun? Active;
        public RunningTurn? Running;
        public TurnSettlement? Outcome;
        public LogCheckpoint? Checkpoint;
        private Task<TurnSettlement>? _retry;

        public Task<SendResult> SendAsync(string text, bool stopTurn)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.EmptyMessage()));
            return (NodeWorks.For(Definition.Blueprint.Work) as IConverses)?.Receive(text) switch
            {
                MessageUse.Guidance guidance => Active!.GuideAsync(Definition, guidance.Text, default, null),
                MessageUse.Turn turn => Active!.SendAsync(Definition, turn.Prompt, stopTurn, default, null),
                _ => Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.NoConversation()))),
            };
        }

        public void Leave()
        {
            lock (Gate)
            {
                Leaving = true;
                if (Active is { } active) _ = active.StopAsync(new AttemptEvent.InterruptRequested(project.TimeProvider.GetUtcNow(), LeaveReason));
            }
        }

        public async Task LeaveAsync()
        {
            Task settlement;
            lock (Gate) settlement = _retry ?? Settlement.Task;
            try { await settlement.WaitAsync(project.LeaveTimeout, project.TimeProvider); }
            catch (TimeoutException)
            {
                lock (Gate) Active?.Abandon(new(project.TimeProvider.GetUtcNow(), LeaveReason));
            }
            finally
            {
                if (settlement.IsCompleted) Lease.Dispose();
                else _ = ReleaseAfterSettlement(settlement);
            }
        }

        private async Task ReleaseAfterSettlement(Task settlement)
        {
            await settlement;
            Lease.Dispose();
        }

        public bool Observe(RootExit exit)
        {
            lock (Gate)
            {
                if (Detached) return false;
                RootObservation observation;
                try { observation = Materializer.ObserveRootExit(Lease, OperationIds.Derive(operation, "root"), Address.Launch, exit, project.ShutdownTime); }
                catch { observation = new RootObservation.Rejected(new(RunProblem.StorageUnavailable)); }
                RootStep.TrySetResult(observation);
                if (observation is RootObservation.Rejected rejected)
                    RootExited.TrySetException(new InvalidOperationException(rejected.Reason.Problem.ToString()));
                else RootExited.TrySetResult();
                return true;
            }
        }

        public TurnSettlement Unresolved(UnresolvedReason reason, RunRejection? rejection = null, ProcessMatch? root = null) =>
            new TurnSettlement.Unresolved(new(Store, Address, reason, rejection, root, Lease));

        public TurnSettlement Complete(TurnSettlement outcome)
        {
            if (outcome is TurnSettlement.Settled) RootExited.TrySetResult();
            else if (!RootExited.Task.IsCompleted)
                RootExited.TrySetException(new InvalidOperationException(outcome is TurnSettlement.Unresolved unresolved
                    ? unresolved.Turn.Rejection?.Problem.ToString() ?? unresolved.Turn.Reason.ToString()
                    : ((TurnSettlement.Refused)outcome).Reason.Problem.ToString()));
            lock (Gate) Outcome = outcome;
            Settlement.TrySetResult(outcome);
            return outcome;
        }

        public async Task<TurnSettlement> NotStarted(string detail)
        {
            try
            {
                Observe(new RootExit.NotStarted(detail));
                if (Requested && Log is not null) Log.Append(new AttemptEvent.LaunchFailed(project.TimeProvider.GetUtcNow(), detail));
                Log?.Dispose();
                Checkpoint = AttemptEvidence.Read(Folder).Checkpoint;
                return Complete(await Settle());
            }
            catch
            {
                Log?.Dispose();
                RootExited.TrySetException(new InvalidOperationException("The turn's root observation could not be recorded."));
                return Complete(Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.StorageUnavailable)));
            }
        }

        private string Folder => Store.AttemptFolder(Address.Workflow, Address.Run, Address.Task, Address.Launch.Attempt);

        public async Task<TurnSettlement> Settle()
        {
            if (!Lease.Held) return Unresolved(UnresolvedReason.OwnershipConflict, new(RunProblem.TaskBusy));
            if (RootStep.Task.IsCompletedSuccessfully)
            {
                switch (RootStep.Task.Result)
                {
                    case RootObservation.Fenced: return Unresolved(UnresolvedReason.OwnershipConflict);
                    case RootObservation.Rejected rejected:
                        return Unresolved(UnresolvedReason.OwnershipConflict, rejected.Reason);
                }
            }
            if (Checkpoint is null) return Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.EvidenceMismatch));
            var settlement = await Materializer.Settle(Lease, SettleOperation, Address.Launch, Checkpoint);
            return settlement switch
            {
                Execution.Settlement.Closed => BuildSettled(),
                Execution.Settlement.Rejected rejected => Unresolved(UnresolvedReason.IncompleteEvidence, rejected.Reason),
                _ => throw new InvalidOperationException(),
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
            return new TurnSettlement.Settled(new(Store, Address, record.Preparations[Address.Launch], checkpoint,
                record.RootExits[Address.Launch], closure, record.Dispositions[record.Settlements[Address.Launch]],
                evidence.Record, evidence.Events.OfType<AttemptEvent.CleanedUp>().LastOrDefault(), Lease));
        }

        public Task<TurnSettlement> Reconcile()
        {
            lock (Gate)
            {
                if (Outcome is null) return Settlement.Task;
                if (Outcome is TurnSettlement.Settled) return Task.FromResult(Outcome);
                if (_retry is { IsCompleted: false }) return _retry;
                return _retry = Task.Run(async () =>
                {
                    TurnSettlement outcome;
                    try
                    {
                        outcome = Checkpoint is not null && RootStep.Task.IsCompletedSuccessfully && RootStep.Task.Result is RootObservation.Observed
                            ? await Settle() : await Recover();
                    }
                    catch { outcome = Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.StorageUnavailable)); }
                    return Complete(outcome);
                });
            }
        }

        public async Task<TurnSettlement> Recover()
        {
            if (Store.Read(Address.Workflow, Address.Run) is not RunRead.Loaded loaded)
                return Complete(Unresolved(UnresolvedReason.IncompleteEvidence, new(RunProblem.StorageUnavailable)));
            var record = loaded.Record;
            if (!record.Claims.ContainsKey(Address.Launch))
            {
                Lease.Dispose();
                return Complete(new TurnSettlement.Refused(new(RunProblem.InvalidClaim)));
            }
            if (!Lease.Held) return Complete(Unresolved(UnresolvedReason.OwnershipConflict));
            if (record.TurnClosures.ContainsKey(Address.Launch)) return Complete(BuildSettled());
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
                return Complete(Unresolved(UnresolvedReason.Uncertain, root: identity is null ? null : ProcessCheck.Check(identity.Value)));
            }
            RootStep.TrySetResult(new RootObservation.Observed(record.RootExits[Address.Launch]));
            RootExited.TrySetResult();
            var result = await Materializer.RecoverSettlement(Lease, SettleOperation, Address.Launch);
            return Complete(result is Execution.Settlement.Closed ? BuildSettled() :
                Unresolved(UnresolvedReason.IncompleteEvidence, ((Execution.Settlement.Rejected)result).Reason));
        }
    }
}
