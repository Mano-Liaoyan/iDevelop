using System.Collections.Immutable;
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
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Action<string>? _probe;

    private Materializer(string project, RunStore store, IJoinComposer joins, TimeProvider clock,
        IReadOnlyDictionary<string, string> environment, Action<string>? probe)
    {
        _project = Path.GetFullPath(project);
        _store = store;
        _refs = new(store, probe);
        _joins = joins;
        _clock = clock;
        _environment = environment;
        _probe = probe;
    }

    /// <summary>
    /// How long a step waits for the repository's mutation lock while another step holds it. A run's own scheduled work
    /// waits longer, because its tasks prepare, claim, and publish at the same time (ADR 0018); a person's command keeps
    /// the default, so a busy repository is reported to the person at once.
    /// </summary>
    internal TimeSpan MutationPatience { get; set; } = GitRepository.MutationPatience;

    /// <summary>
    /// Whether the steps are a run's own scheduled work, which never takes the repository's mutation lock while a person's
    /// command waits for it. A person's command, the default, goes ahead of that work (#105).
    /// </summary>
    internal bool Scheduled { get; set; }

    /// <summary>Whether the step's owner stopped, such as a halted coordinator or a closing project, so a wait for the lock ends.</summary>
    internal Func<bool>? Halted { get; set; }

    public static Materializer Open(string projectFolder, RunStore store, IJoinComposer? joins = null) =>
        Open(projectFolder, store, joins, TimeProvider.System, null, null);

    internal static Materializer Open(string projectFolder, RunStore store, IJoinComposer? joins,
        TimeProvider clock, IReadOnlyDictionary<string, string>? environment, Action<string>? probe = null) =>
        new(projectFolder, store, joins ?? new UnavailableJoins(), clock, environment ?? new Dictionary<string, string>(), probe);

    /// <param name="agents">What the clients offer now, which a planner's start records for its prompt to list (#99).</param>
    public async ValueTask<Preparation> Prepare(RunLease lease, OperationId operation, AttemptCause cause,
        string? basePrompt = null, CancellationToken cancellation = default, ImmutableArray<AgentOffer> agents = default)
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
            using var held = MutationLock(repository);
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
                        original.Id, basePrompt, RunPlanning.Context(record, original), RunReducer.FixArtifacts(record, original.Id), Note(original),
                        RunReducer.Editable(record, original)) != completed.Execution.Prompt)
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
                VerifyWorktree(repository, completed.Execution.Location, record);
                ResolveMaintenanceBlocks(permit, operation, "Prepared.");
                return new Preparation.Ready(completed.Execution, WorkingFolder(repository, completed.Execution.Location.Owner));
            }
            step = "layout";
            record = AllocateLayout(permit, operation, task, repository, record);
            step = "plan";
            var planOperation = OperationIds.Derive(operation, "plan");
            var revision = record.Receipts.GetValueOrDefault(planOperation)?.Event is
                RunEvent.Planned { Plan: MaterializationPlan.Preparation originalPlan }
                ? originalPlan.Revision
                : RunReducer.Slot(record, task, cause)?.Revision ?? record.Revision.Id;
            var planned = Journal("plan", () => _store.Plan(lease, planOperation, revision, cause, agents));
            record = DecisionRecord(planned);
            var planEvent = DecisionEvent(planned);
            var plan = (MaterializationPlan.Preparation)((RunEvent.Planned)planEvent).Plan;
            var planId = record.Plans.Single(pair => RunReducer.Same(pair.Value, plan)).Key;
            inputs = plan.Inputs;
            step = "artifacts";
            var storage = new RunStorage(_project, workflow, run);
            VerifyArtifacts(record, plan, storage);
            VerifyForwarding(record, plan.Task, plan.Revision, plan.Bindings);
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
            var definition = record.Revisions[plan.Revision].Snapshot.Tasks[task];
            var edits = RunReducer.Editable(record, record.Attempts[plan.Attempt]);
            var owner = Owner(record, task, edits);
            var input = record.Inputs[plan.Inputs];
            var start = RunReducer.AttemptBase(record, record.Attempts[plan.Attempt], input) ??
                throw new Refusal(new(RunProblem.InputConflict));
            var location = new ExecutionLocation(owner, start);
            step = "worktree";
            // A Retry or a Continue of a Merge node's attempt keeps the files the failed attempt left, as every other Retry does.
            var keeps = cause is AttemptCause.Retry or AttemptCause.Continue && RunReducer.MergeOf(record, cause) is not null;
            EnsureCheckout(permit, operation, planId, repository, location, record, keeps, cause is AttemptCause.Retry && RunReducer.MergeOf(record, cause) is null);
            // Git checks out no folder that holds no tracked file.
            Directory.CreateDirectory(WorkingFolder(repository, owner));
            step = "delivery";
            Deliver(record, input, repository, owner, storage);
            var outbox = edits ? RunLayout.Outbox(plan.Attempt) : "";
            if (outbox.Length != 0) Directory.CreateDirectory(RunStorage.SafePath(WorkingFolder(repository, owner), outbox));
            VerifyOutbox(repository, WorkingFolder(repository, owner), outbox);
            step = "prompt";
            var prompt = Prompt(definition, input, plan.Attempt, basePrompt, RunPlanning.Context(record, record.Attempts[plan.Attempt]),
                RunReducer.FixArtifacts(record, plan.Attempt), Note(record.Attempts[plan.Attempt]), edits);
            var execution = new PreparedExecution(new(plan.Attempt, 1), input.Id, location, prompt, Revision.Hash(prompt), outbox);
            step = "prepared";
            // A sibling result may supersede an input while Git and file delivery run.
            if (RunReducer.InputProblem(Read(workflow, run), input, true) is { } stale) return new Preparation.Rejected(stale);
            Journal("prepared", () => _store.Record(permit, OperationIds.Derive(operation, "prepared"), new RunEvent.Prepared(execution)));
            ResolveMaintenanceBlocks(permit, operation, "Prepared.");
            return new Preparation.Ready(execution, WorkingFolder(repository, owner));
        }
        catch (Refusal refused) { return new Preparation.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return Block(permit, operation, step, new(operation, task, attempt, failed.Problem, inputs, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Block(permit, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message) { Scope = new BlockScope.Operation() }); }
    }

    /// <summary>
    /// The task's worktree in this run. Each attempt of a task, a Retry's included, works in the worktree its first attempt
    /// got. An editing task's worktree is on a branch of its own, and a read-only task's worktree has none (ADR 0032).
    /// </summary>
    private static WorktreeOwner Owner(RunRecord record, TaskId task, bool edits) =>
        record.Preparations.Values.FirstOrDefault(prepared => prepared.Location.Owner.Task == task)?.Location.Owner ??
        new(task, RunLayout.TaskCheckout(record.RunKey!, record.TaskKeys[task]), edits ? RunLayout.TaskBranch(record.RunKey!, record.TaskKeys[task]) : null);

    private GitRepository OpenRepository() => GitRepository.Open(_project, _environment) switch
    {
        RepositoryOpen.Opened opened => opened.Repository,
        RepositoryOpen.Refused refused => throw Fault(refused.Fault.Problem(), refused.Detail, new BlockScope.Repository()),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// The repository's mutation lock, waited for as this materializer's steps wait, or null once that wait ends. Each try
    /// the lock refuses is probed as <c>mutation.waiting</c>.
    /// </summary>
    private FileStream? MutationLock(GitRepository repository) => repository.TakeMutationLock(MutationPatience, () =>
    {
        _probe?.Invoke("mutation.waiting");
        return Halted?.Invoke() == true;
    }, Scheduled);

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
        try
        {
            var record = Read(workflow, run);
            foreach (var id in RunReducer.BlockIds(operation, step))
            {
                if (!record.Receipts.TryGetValue(id, out var receipt))
                {
                    Journal(step + "-blocked", () => _store.Record(permit, id, new RunEvent.Blocked(block)));
                    return new Preparation.Blocked(block);
                }
                if (receipt.Event is RunEvent.Blocked prior && !record.Blocks[id].Resolved && RunReducer.Same(prior.Block, block))
                    return new Preparation.Blocked(prior.Block);
            }
            throw new InvalidOperationException();
        }
        catch (Refusal refused) { return new Preparation.Rejected(refused.Reason); }
    }

    private void ResolveMaintenanceBlocks(CoordinatorPermit permit, OperationId operation, string reason, Func<BlockScope, bool>? scope = null)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        foreach (var pair in Read(workflow, run).Blocks.Where(pair => !pair.Value.Resolved && pair.Value.Block.Operation == operation &&
            (scope is null || scope(pair.Value.Block.Scope)))
            .OrderBy(pair => pair.Key.Value))
            Journal("maintenance-resolve-" + pair.Key.Value.ToString("D"), () => _store.Record(permit,
                OperationIds.Derive(operation, "maintenance-resolve-" + pair.Key.Value.ToString("D")), new RunEvent.BlockResolved(pair.Key, reason)));
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
        GitRead<T>.Failed failed => throw Fault(failed.Fault.Problem(), failed.Detail, failed switch
        {
            { Refs.IsEmpty: false } => new BlockScope.Refs(failed.Refs),
            // Only a task checkout's index that hides entries fails as DirtyWorktree.
            { Fault: GitFault.HiddenChanges } => new BlockScope.Repository(),
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

    /// <summary>The project's folder in the task's worktree, where its agent works and its inputs and outbox are.</summary>
    private static string WorkingFolder(GitRepository repository, WorktreeOwner owner) => repository.ProjectIn(Checkout(repository, owner));

    /// <param name="planning">What a planner's first prompt lists, from <see cref="RunPlanning.Context"/>.</param>
    /// <param name="note">The person's note to the agent with a Retry, which the prompt ends with (#126).</param>
    private static string Prompt(TaskDefinition definition, InputRecord input, AttemptId attempt, string? basePrompt,
        PlanningContext? planning, ImmutableArray<ArtifactRecord> kept, string? note, bool edits)
    {
        var ending = note is null ? "" : "\n\n" + RetryNote(note);
        // A Merge node's agent reads the conflict it resolves, and nothing else hands it input.
        if (input.Conflict is { } conflict) return MergeWork.Prompt(new NodeContext(definition, "") { Conflict = conflict.Description }) + ending;
        if (basePrompt is null && definition.Blueprint.Work is not WorkSpec.Agent)
            throw new Refusal(new(RunProblem.InvalidData));
        var template = (definition.Blueprint.Work as WorkSpec.Agent)?.Template;
        var prompt = basePrompt ?? AgentWork.Prompt(new NodeContext(definition, input.Text) { Planning = planning });
        if ((basePrompt is not null || template is not null && !template.Text.Contains("{{inputs}}", StringComparison.Ordinal)) && input.Text.Length != 0)
            prompt += "\n\n" + input.Text;
        if (edits)
            prompt += "\n\n" + ArtifactInstructions.For(attempt, kept);
        return prompt + ending;
    }

    /// <summary>The note a Retry's first prompt ends with.</summary>
    internal static string RetryNote(string note) => "## A note from the person with this retry\n\n" + note;

    /// <summary>The note of <paramref name="attempt"/>'s Retry, or null.</summary>
    private static string? Note(RunAttempt attempt) => (attempt.Cause as AttemptCause.Retry)?.Note;

    private static MaterializationFailure Fault(MaterializationProblem problem, string detail, BlockScope scope) => new(problem, detail, scope);
    private sealed class MaterializationFailure(MaterializationProblem problem, string detail, BlockScope scope) : Exception(detail)
    { public MaterializationProblem Problem { get; } = problem; public BlockScope Scope { get; } = scope; }
    private sealed class Refusal(RunRejection reason) : Exception
    { public RunRejection Reason { get; } = reason; }
}
