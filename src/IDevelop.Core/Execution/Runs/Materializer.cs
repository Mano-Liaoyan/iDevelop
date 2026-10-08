using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private readonly string _project;
    private readonly RunStore _store;
    private readonly IJoinComposer _joins;
    private readonly RefPublisher _refs;
    private readonly TimeProvider _clock;
    private readonly Func<string, ulong?> _volumes;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Action<string>? _probe;

    private Materializer(string project, RunStore store, IJoinComposer joins, TimeProvider clock,
        IReadOnlyDictionary<string, string> environment, Action<string>? probe, Func<string, ulong?> volumes)
    {
        _project = Path.GetFullPath(project);
        _store = store;
        _refs = new(store, probe);
        _joins = joins;
        _clock = clock;
        _volumes = volumes;
        _environment = environment;
        _probe = probe;
    }

    public static Materializer Open(string projectFolder, RunStore store, IJoinComposer? joins = null) =>
        Open(projectFolder, store, joins, TimeProvider.System, null, null);

    internal static Materializer Open(string projectFolder, RunStore store, IJoinComposer? joins,
        TimeProvider clock, IReadOnlyDictionary<string, string>? environment, Action<string>? probe = null, Func<string, ulong?>? volumes = null) =>
        new(projectFolder, store, joins ?? new UnavailableJoins(), clock, environment ?? new Dictionary<string, string>(), probe, volumes ?? FileIdentities.VolumeOf);

    public async ValueTask<Preparation> Prepare(RunLease lease, OperationId operation, AttemptCause cause,
        string? basePrompt = null, CancellationToken cancellation = default)
    {
        using var authority = lease.Use();
        if (authority is null) return new Preparation.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Preparation.Rejected(new(problem));
        var permit = lease.Permit;
        var workflow = permit.Workflow;
        var run = permit.Run;
        var task = lease.Task;
        var step = "open";
        AttemptId? attempt = null;
        InputId? inputs = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var repository = OpenRepository();
            using var held = repository.TakeMutationLock();
            if (held is null) return new Preparation.Rejected(new(RunProblem.JournalBusy));
            var record = Read(workflow, run);
            if (record.Phase != RunPhase.Approved) return new Preparation.Rejected(new(RunProblem.RunStopped));
            step = "exclusions";
            var preparedReceipt = record.Receipts.GetValueOrDefault(OperationIds.Derive(operation, "prepared"));
            if (preparedReceipt?.Event is not RunEvent.Prepared) Mutate("exclusions", repository.EnsureExcluded);
            var tracked = Value(repository.TrackedFiles(repository.ProjectFolder, ".idp/inputs", ".idp/outbox"));
            if (!tracked.IsEmpty) throw Fault(MaterializationProblem.InputUnavailable, "Tracked execution data: " + string.Join(", ", tracked), new BlockScope.Operation());
            if (preparedReceipt?.Event is RunEvent.Prepared completed)
            {
                var original = record.Attempts[completed.Execution.Launch.Attempt];
                if (LeaseProblem(lease, original.Task) is { } mismatch) return new Preparation.Rejected(new(mismatch));
                if (!RunReducer.Same(original.Cause, cause) ||
                    Prompt(record.Revisions[original.Revision].Snapshot.Tasks[task], record.Inputs[completed.Execution.Inputs],
                        original.Id, basePrompt, RunPlanning.Context(record, original)) != completed.Execution.Prompt)
                    return new Preparation.Rejected(new(RunProblem.OperationConflict));
                attempt = original.Id;
                inputs = completed.Execution.Inputs;
                VerifyRepository(record, repository);
                if (Value(repository.ReadRef(RunLayout.ApprovedBase(record.RunKey!))) != record.Base.Commit)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The approved base retention ref has changed.",
                        new BlockScope.Refs([RunLayout.ApprovedBase(record.RunKey!)]));
                if (record.Inputs[completed.Execution.Inputs].Code is CodeSelection.Joined joined)
                    VerifyJoin(record, repository, task, completed.Execution.Inputs, record.Plans.Values.OfType<MaterializationPlan.Preparation>()
                        .Single(plan => plan.Attempt == original.Id).Sources, joined.Join);
                VerifyRecoveryBaseline(repository, record, completed.Execution.Location, cause);
                VerifyCheckout(repository, completed.Execution.Location, cause is AttemptCause.Continue, record);
                VerifyDelivery(record, completed.Execution, repository);
                RunStorage.Read(new RunStorage(_project, workflow, run).Folder, completed.SharedRefs.RelativePath,
                    completed.SharedRefs.Content, completed.SharedRefs.ByteLength);
                ResolveMaintenanceBlocks(permit, operation, "Prepared.");
                return new Preparation.Ready(completed.Execution, Checkout(repository, completed.Execution.Location.Owner));
            }
            step = "layout";
            record = AllocateLayout(permit, operation, task, repository, record);
            step = "plan";
            var planOperation = OperationIds.Derive(operation, "plan");
            var revision = record.Receipts.GetValueOrDefault(planOperation)?.Event is
                RunEvent.Planned { Plan: MaterializationPlan.Preparation originalPlan }
                ? originalPlan.Revision
                : RunReducer.Slot(record, task, cause)?.Revision ?? record.Revision.Id;
            var planned = Journal("plan", () => _store.Plan(lease, planOperation, revision, cause));
            record = DecisionRecord(planned);
            var planEvent = DecisionEvent(planned);
            var plan = (MaterializationPlan.Preparation)((RunEvent.Planned)planEvent).Plan;
            var planId = record.Plans.Single(pair => RunReducer.Same(pair.Value, plan)).Key;
            inputs = plan.Inputs;
            step = "artifacts";
            var storage = new RunStorage(_project, workflow, run);
            VerifyArtifacts(record, plan, storage);
            JoinRecord? join = null;
            if (plan.Sources.Select(source => source.Commit).Distinct().Count() >= 2)
            {
                step = "join";
                var joinRef = RunLayout.JoinBranch(record.RunKey!, record.TaskKeys[task]);
                var outcome = await _joins.Compose(new(permit, OperationIds.Derive(operation, "join"), task, plan.Inputs,
                    plan.Sources, Value(repository.ReadRef(joinRef))), cancellation);
                if (outcome is JoinOutcome.Blocked blocked)
                    return Block(permit, operation, step, blocked.Block with { Operation = operation });
                join = ((JoinOutcome.Ready)outcome).Join;
                VerifyJoin(Read(workflow, run), repository, task, plan.Inputs, plan.Sources, join);
            }
            cancellation.ThrowIfCancellationRequested();
            step = "reserve";
            record = DecisionRecord(Journal("reserve", () => _store.Reserve(lease,
                OperationIds.Derive(operation, "reserve"), planId, join)));
            attempt = plan.Attempt;
            var owner = new WorktreeOwner(task, RunLayout.TaskCheckout(record.RunKey!, record.TaskKeys[task]),
                RunLayout.TaskBranch(record.RunKey!, record.TaskKeys[task]));
            var input = record.Inputs[plan.Inputs];
            var start = RunReducer.AttemptBase(record, record.Attempts[plan.Attempt], input) ??
                throw new Refusal(new(RunProblem.InputConflict));
            var location = new ExecutionLocation(owner, start);
            step = "worktree";
            EnsureCheckout(permit, operation, planId, repository, location, record);
            step = "checkout";
            VerifyRecoveryBaseline(repository, Read(workflow, run), location, cause);
            VerifyCheckout(repository, location, cause is AttemptCause.Continue, Read(workflow, run));
            step = "delivery";
            Deliver(record, input, repository, owner, storage);
            var definition = record.Revisions[plan.Revision].Snapshot.Tasks[task];
            var outbox = definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit } ? RunLayout.Outbox(plan.Attempt) : "";
            if (outbox.Length != 0) Directory.CreateDirectory(RunStorage.SafePath(Checkout(repository, owner), outbox));
            VerifyOutbox(repository, Checkout(repository, owner), outbox);
            step = "prompt";
            var prompt = Prompt(definition, input, plan.Attempt, basePrompt, RunPlanning.Context(record, record.Attempts[plan.Attempt]));
            var execution = new PreparedExecution(new(plan.Attempt, 1), input.Id, location, prompt, Revision.Hash(prompt), outbox);
            step = "shared-refs";
            var refs = Snapshot(storage, operation, repository, record);
            step = "prepared";
            // A sibling result may supersede an input while Git and file delivery run.
            if (RunReducer.InputProblem(Read(workflow, run), input, true) is { } stale) return new Preparation.Rejected(stale);
            Journal("prepared", () => _store.Record(permit, OperationIds.Derive(operation, "prepared"), new RunEvent.Prepared(execution, refs)));
            ResolveMaintenanceBlocks(permit, operation, "Prepared.");
            return new Preparation.Ready(execution, Checkout(repository, owner));
        }
        catch (Refusal refused) { return new Preparation.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return Block(permit, operation, step, new(operation, task, attempt, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Block(permit, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }
    }

    private GitRepository OpenRepository() => GitRepository.Open(_project, _environment) switch
    {
        RepositoryOpen.Opened opened => opened.Repository,
        RepositoryOpen.Refused refused => throw Fault(refused.Problem, refused.Detail, new BlockScope.Repository()),
        _ => throw new InvalidOperationException(),
    };

    private RunRecord Read(WorkflowId workflow, RunId run) => _store.Read(workflow, run) switch
    {
        RunRead.Loaded loaded => loaded.Record,
        RunRead.Rejected rejected => throw new Refusal(rejected.Reason),
        _ => throw new InvalidOperationException(),
    };

    private RunDecision Journal(string step, Func<RunDecision> action)
    {
        _probe?.Invoke("journal." + step + ".before");
        var result = action();
        _probe?.Invoke("journal." + step + ".after");
        if (result is RunDecision.Rejected rejected) throw new Refusal(rejected.Reason);
        return result;
    }

    private T Mutate<T>(string step, Func<T> action)
    {
        _probe?.Invoke("git." + step + ".before");
        var result = action();
        _probe?.Invoke("git." + step + ".after");
        return result;
    }

    private void Mutate(string step, Action action) => Mutate(step, () => { action(); return true; });

    private Preparation Block(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        var id = OperationIds.Derive(operation, step + "-blocked");
        try
        {
            var record = Read(workflow, run);
            var number = 1;
            while (record.Receipts.TryGetValue(id, out var receipt))
            {
                if (receipt.Event is RunEvent.Blocked prior && !record.Blocks[id].Resolved && RunReducer.Same(prior.Block, block))
                    return new Preparation.Blocked(prior.Block);
                id = OperationIds.Derive(operation, step + "-blocked-" + number++);
            }
            Journal(step + "-blocked", () => _store.Record(permit, id, new RunEvent.Blocked(block)));
            return new Preparation.Blocked(block);
        }
        catch (Refusal refused) { return new Preparation.Rejected(refused.Reason); }
    }

    private RunProblem? LeaseProblem(RunLease lease, TaskId? task = null)
    {
        if (!lease.Held) return RunProblem.TaskBusy;
        if (!SamePath(lease.Project, _project) || task is { } owner && lease.Task != owner) return RunProblem.IdentityMismatch;
        return null;
    }

    private static T Value<T>(GitRead<T> read) => read switch
    {
        GitRead<T>.Read success => success.Value,
        GitRead<T>.Failed failed => throw Fault(failed.Problem, failed.Detail, failed switch
        {
            { Refs.IsEmpty: false } => new BlockScope.Refs(failed.Refs),
            // Only a task checkout's index that hides entries fails as DirtyWorktree, and Restore and baselines refuse one.
            { Problem: MaterializationProblem.DirtyWorktree } => new BlockScope.Repository(),
            _ => new BlockScope.Operation(),
        }),
        _ => throw new InvalidOperationException(),
    };

    private static RunRecord DecisionRecord(RunDecision decision) => decision switch
    {
        RunDecision.Recorded recorded => recorded.Record,
        RunDecision.Created created => created.Record,
        RunDecision.Existing existing => existing.Record,
        _ => throw new InvalidOperationException(),
    };

    private static RunEvent DecisionEvent(RunDecision decision) => decision switch
    {
        RunDecision.Recorded recorded => recorded.Event,
        RunDecision.Created created => created.Event,
        RunDecision.Existing existing => existing.Event,
        _ => throw new InvalidOperationException(),
    };

    private static string Checkout(GitRepository repository, WorktreeOwner owner) =>
        Path.GetFullPath(Path.Combine(repository.ProjectFolder, owner.RelativePath));

    /// <param name="planning">What a planner's first prompt lists, from <see cref="RunPlanning.Context"/>.</param>
    private static string Prompt(TaskDefinition definition, InputRecord input, AttemptId attempt, string? basePrompt,
        PlanningContext? planning = null)
    {
        if (basePrompt is null && definition.Blueprint.Work is not WorkSpec.Agent)
            throw new Refusal(new(RunProblem.InvalidData));
        var template = (definition.Blueprint.Work as WorkSpec.Agent)?.Template;
        var prompt = basePrompt ?? AgentWork.Prompt(new NodeContext(definition, input.Text) { Planning = planning });
        if ((basePrompt is not null || template is not null && !template.Text.Contains("{{inputs}}", StringComparison.Ordinal)) && input.Text.Length != 0)
            prompt += "\n\n" + input.Text;
        if (definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit })
            prompt += "\n\nDeclare artifacts in " + RunLayout.Outbox(attempt) +
                "/manifest.json using {\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}. " +
                "Artifact paths are relative to that folder.";
        return prompt;
    }

    private static MaterializationFailure Fault(MaterializationProblem problem, string detail, BlockScope scope) => new(problem, detail, scope);
    private sealed class MaterializationFailure(MaterializationProblem problem, string detail, BlockScope scope) : Exception(detail)
    { public MaterializationProblem Problem { get; } = problem; public BlockScope Scope { get; } = scope; }
    private sealed class Refusal(RunRejection reason) : Exception
    { public RunRejection Reason { get; } = reason; }
}
