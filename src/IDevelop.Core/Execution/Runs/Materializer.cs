using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private readonly string _project;
    private readonly RunStore _store;
    private readonly IJoinComposer _joins;
    private readonly IExecutionBoundary _boundary;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Action<string>? _probe;

    private Materializer(string project, RunStore store, IJoinComposer joins, IExecutionBoundary boundary, TimeProvider clock,
        IReadOnlyDictionary<string, string> environment, Action<string>? probe)
    {
        _project = Path.GetFullPath(project);
        _store = store;
        _joins = joins;
        _boundary = boundary;
        _clock = clock;
        _environment = environment;
        _probe = probe;
    }

    public static Materializer Open(string projectFolder, RunStore store, IJoinComposer? joins = null) =>
        Open(projectFolder, store, joins, new UnprovenBoundary(), TimeProvider.System, null, null);

    internal static Materializer Open(string projectFolder, RunStore store, IJoinComposer? joins, IExecutionBoundary boundary,
        TimeProvider clock, IReadOnlyDictionary<string, string>? environment, Action<string>? probe = null) =>
        new(projectFolder, store, joins ?? new UnavailableJoins(), boundary, clock, environment ?? new Dictionary<string, string>(), probe);

    public async ValueTask<Preparation> Prepare(WorkflowId workflow, RunId run, OperationId operation, TaskId task, AttemptCause cause,
        string? basePrompt = null, CancellationToken cancellation = default)
    {
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
            if (!tracked.IsEmpty) throw Fault(MaterializationProblem.InputUnavailable, "Tracked execution data: " + string.Join(", ", tracked));
            if (preparedReceipt?.Event is RunEvent.Prepared completed)
            {
                var original = record.Attempts[completed.Execution.Launch.Attempt];
                if (original.Task != task || !RunReducer.Same(original.Cause, cause) ||
                    Prompt(record.Revisions[original.Revision].Snapshot.Tasks[task], record.Inputs[completed.Execution.Inputs],
                        original.Id, basePrompt) != completed.Execution.Prompt)
                    return new Preparation.Rejected(new(RunProblem.OperationConflict));
                attempt = original.Id;
                inputs = completed.Execution.Inputs;
                VerifyRepository(record, repository);
                if (Value(repository.ReadRef(RunLayout.ApprovedBase(record.RunKey!))) != record.Base.Commit)
                    throw Fault(MaterializationProblem.UncertainOwnership, "The approved base retention ref has changed.");
                if (record.Inputs[completed.Execution.Inputs].Code is CodeSelection.Joined joined)
                    VerifyJoin(record, repository, record.Plans.Values.OfType<MaterializationPlan.Preparation>()
                        .Single(plan => plan.Attempt == original.Id), joined.Join);
                using var taskLock = TakeTaskLock(task);
                VerifyCheckout(repository, completed.Execution.Location, cause is AttemptCause.Continue, record);
                VerifyDelivery(record, completed.Execution, repository);
                RunStorage.Read(new RunStorage(_project, workflow, run).Folder, completed.SharedRefs.RelativePath,
                    completed.SharedRefs.Content, completed.SharedRefs.ByteLength);
                return new Preparation.Ready(completed.Execution, Checkout(repository, completed.Execution.Location.Owner));
            }
            step = "layout";
            record = AllocateLayout(workflow, run, operation, task, repository, record);
            step = "plan";
            var planned = Journal("plan", () => _store.Plan(workflow, run, OperationIds.Derive(operation, "plan"), task,
                record.Revision.Id, cause));
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
                var outcome = await _joins.Compose(new(OperationIds.Derive(operation, "join"), run, task, plan.Inputs,
                    plan.Sources, Value(repository.ReadRef(joinRef))), cancellation);
                if (outcome is JoinOutcome.Blocked blocked)
                    return Block(workflow, run, operation, step, blocked.Block with { Operation = operation });
                join = ((JoinOutcome.Ready)outcome).Join;
                VerifyJoin(Read(workflow, run), repository, plan, join);
            }
            cancellation.ThrowIfCancellationRequested();
            step = "reserve";
            record = DecisionRecord(Journal("reserve", () => _store.Reserve(workflow, run,
                OperationIds.Derive(operation, "reserve"), planId, join)));
            attempt = plan.Attempt;
            var owner = new WorktreeOwner(task, RunLayout.TaskCheckout(record.RunKey!, record.TaskKeys[task]),
                RunLayout.TaskBranch(record.RunKey!, record.TaskKeys[task]));
            var input = record.Inputs[plan.Inputs];
            var start = RunReducer.AttemptBase(record, record.Attempts[plan.Attempt], input) ??
                throw new Refusal(new(RunProblem.InputConflict));
            var location = new ExecutionLocation(owner, start);
            step = "task-lock";
            using var locked = TakeTaskLock(task);
            step = "worktree";
            EnsureCheckout(workflow, run, operation, planId, repository, location, record);
            step = "checkout";
            VerifyCheckout(repository, location, cause is AttemptCause.Continue, Read(workflow, run));
            step = "delivery";
            Deliver(record, input, repository, owner, storage);
            var definition = record.Revisions[plan.Revision].Snapshot.Tasks[task];
            var outbox = definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit } ? RunLayout.Outbox(plan.Attempt) : "";
            if (outbox.Length != 0) Directory.CreateDirectory(RunStorage.SafePath(Checkout(repository, owner), outbox));
            VerifyOutbox(repository, Checkout(repository, owner), outbox);
            step = "prompt";
            var prompt = Prompt(definition, input, plan.Attempt, basePrompt);
            var execution = new PreparedExecution(new(plan.Attempt, 1), input.Id, location, prompt, Revision.Hash(prompt), outbox);
            step = "shared-refs";
            var refs = Snapshot(storage, operation, repository, record);
            step = "prepared";
            // A sibling result may supersede an input while Git and file delivery run.
            if (RunReducer.InputProblem(Read(workflow, run), input, true) is { } stale) return new Preparation.Rejected(stale);
            Journal("prepared", () => _store.Record(workflow, run, OperationIds.Derive(operation, "prepared"), new RunEvent.Prepared(execution, refs)));
            return new Preparation.Ready(execution, Checkout(repository, owner));
        }
        catch (Refusal refused) { return new Preparation.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return Block(workflow, run, operation, step, new(operation, task, attempt, failed.Problem, inputs, [], failed.Message)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Block(workflow, run, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, [], error.Message)); }
    }

    private GitRepository OpenRepository() => GitRepository.Open(_project, _environment) switch
    {
        RepositoryOpen.Opened opened => opened.Repository,
        RepositoryOpen.Refused refused => throw Fault(refused.Problem, refused.Detail),
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

    private Preparation Block(WorkflowId workflow, RunId run, OperationId operation, string step, MaterializationBlock block)
    {
        var id = OperationIds.Derive(operation, step + "-blocked");
        try
        {
            var record = Read(workflow, run);
            var number = 1;
            while (record.Receipts.TryGetValue(id, out var receipt))
            {
                if (receipt.Event is RunEvent.Blocked prior && RunReducer.Same(prior.Block, block))
                    return new Preparation.Blocked(prior.Block);
                id = OperationIds.Derive(operation, step + "-blocked-" + number++);
            }
            Journal(step + "-blocked", () => _store.Record(workflow, run, id, new RunEvent.Blocked(block)));
            return new Preparation.Blocked(block);
        }
        catch (Refusal refused) { return new Preparation.Rejected(refused.Reason); }
    }

    private RunLock TakeTaskLock(TaskId task) => RunLock.TryTake(DataFolder.Attempts(_project), task) ??
        throw Fault(MaterializationProblem.LiveWriter, "The task checkout is owned by a live writer.");

    private static T Value<T>(GitRead<T> read) => read switch
    {
        GitRead<T>.Read success => success.Value,
        GitRead<T>.Failed failed => throw Fault(failed.Problem, failed.Detail),
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

    private static string Prompt(TaskDefinition definition, InputRecord input, AttemptId attempt, string? basePrompt)
    {
        if (basePrompt is null && definition.Blueprint.Work is not WorkSpec.Agent)
            throw new Refusal(new(RunProblem.InvalidData));
        var template = (definition.Blueprint.Work as WorkSpec.Agent)?.Template;
        var prompt = basePrompt ?? AgentWork.Prompt(new NodeContext(definition, input.Text));
        if ((basePrompt is not null || template is not null && !template.Text.Contains("{{inputs}}", StringComparison.Ordinal)) && input.Text.Length != 0)
            prompt += "\n\n" + input.Text;
        if (definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit })
            prompt += "\n\nDeclare artifacts in " + RunLayout.Outbox(attempt) +
                "/manifest.json using {\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}. " +
                "Artifact paths are relative to that folder.";
        return prompt;
    }

    private static MaterializationFailure Fault(MaterializationProblem problem, string detail) => new(problem, detail);
    private sealed class MaterializationFailure(MaterializationProblem problem, string detail) : Exception(detail)
    { public MaterializationProblem Problem { get; } = problem; }
    private sealed class Refusal(RunRejection reason) : Exception
    { public RunRejection Reason { get; } = reason; }
}
