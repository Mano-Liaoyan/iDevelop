using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal abstract record GatePreparation
{
    private GatePreparation() { }

    internal sealed record Requested(GateRequest Request) : GatePreparation;

    internal sealed record Blocked(MaterializationBlock Block) : GatePreparation;

    internal sealed record Rejected(RunRejection Reason) : GatePreparation;

    /// <summary>The inputs changed after the request read them, so nothing was recorded and the next request reads them again.</summary>
    internal sealed record Moved(RunRejection Reason) : GatePreparation;
}

internal sealed partial class Materializer
{
    /// <summary>
    /// Records an Approval node's request on its current inputs, composed as for an attempt: the dependency results' reports
    /// and artifacts, and their code, joined under the node's named join ref when two or more commits come in. The artifacts
    /// the approval would forward are stored under its result first. The request's operation, and with it every id, derives
    /// from <paramref name="root"/> and the bindings, so a repeat after a crash converges on one request, and inputs that
    /// changed get a new one.
    /// </summary>
    public async ValueTask<GatePreparation> RequestGate(CoordinatorPermit permit, OperationId root, TaskId task, CancellationToken cancellation = default)
    {
        var workflow = permit.Workflow;
        var run = permit.Run;
        var operation = root;
        var step = "gate";
        (RevisionId Revision, ImmutableArray<InputBinding> Bindings)? read = null;
        GitRepository? repository = null;
        IDisposable? held = null;
        try
        {
            var record = Read(workflow, run);
            var revision = record.Revision.Id;
            var capture = RunStore.Inputs(record, task, revision, default);
            if (capture.Rejection is { } rejection) return new GatePreparation.Rejected(rejection);
            var bindings = capture.Inputs!.Bindings;
            read = (revision, bindings);
            operation = OperationIds.Derive(root, Revision.Hash(RunJournal.Canonical(new { revision, bindings })).Sha256);
            var id = new InputId(OperationIds.Derive(operation, "inputs").Value);
            var gate = new GateId(OperationIds.Derive(operation, "gate").Value);
            var result = new ResultId(OperationIds.Derive(operation, "result").Value);
            var sources = InputMaterial.Sources(record, bindings);
            JoinRecord? join = null;
            if (sources.Select(source => source.Commit).Distinct().Count() >= 2)
            {
                step = "join";
                repository = OpenRepository();
                held = repository.TakeMutationLock();
                if (held is null) return new GatePreparation.Rejected(new(RunProblem.JournalBusy));
                record = AllocateLayout(permit, operation, task, repository, record);
                var joinRef = RunLayout.JoinBranch(record.RunKey!, record.TaskKeys[task]);
                var outcome = await _joins.Compose(new(permit, OperationIds.Derive(operation, "join"), task, id, sources,
                    Value(repository.ReadRef(joinRef))), cancellation);
                if (outcome is JoinOutcome.Blocked blocked) return GateBlock(permit, operation, step, blocked.Block with { Operation = operation });
                join = ((JoinOutcome.Ready)outcome).Join;
                VerifyJoin(Read(workflow, run), repository, task, id, sources, join);
            }
            cancellation.ThrowIfCancellationRequested();
            step = "artifacts";
            record = Read(workflow, run);
            var material = InputMaterial.Build(record, id, task, revision, bindings, sources,
                InputMaterial.Review(record.Revision.Snapshot, task, bindings), join);
            var forwarded = GateForwarding.Artifacts(record, material, result);
            if (forwarded.Collision is { } name)
                throw Fault(MaterializationProblem.ArtifactCollision, $"Two inputs hand on different artifacts named {name}.", new BlockScope.Operation());
            Forward(record, material, result, new RunStorage(_project, workflow, run));
            step = "request";
            var decision = Journal("gate-request", () => _store.RequestGate(permit, OperationIds.Derive(operation, "request"), gate, material, result));
            return new GatePreparation.Requested(((RunEvent.GateRequested)DecisionEvent(decision)).Request);
        }
        catch (Refusal refused)
        {
            // A result that lands after the read, such as new context, refuses the request. Only then is it worth another read.
            return InputsMoved(workflow, run, task, read) ? new GatePreparation.Moved(refused.Reason) : new GatePreparation.Rejected(refused.Reason);
        }
        catch (MaterializationFailure failed)
        { return GateBlock(permit, operation, step, new(operation, task, null, failed.Problem, null, [], failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return GateBlock(permit, operation, step, new(operation, task, null, MaterializationProblem.InputUnavailable, null, [], error.Message) { Scope = new BlockScope.Operation() }); }
        finally { held?.Dispose(); }
    }

    /// <summary>Whether the node's revision or input bindings now differ from those a refused request read.</summary>
    private bool InputsMoved(WorkflowId workflow, RunId run, TaskId task, (RevisionId Revision, ImmutableArray<InputBinding> Bindings)? read)
    {
        if (read is not { } before || _store.Read(workflow, run) is not RunRead.Loaded loaded) return false;
        var record = loaded.Record;
        if (record.Revision.Id != before.Revision) return true;
        return RunStore.Inputs(record, task, record.Revision.Id, default).Inputs is not { } now || !RunReducer.Same(now.Bindings, before.Bindings);
    }

    /// <summary>Copies each forwarded artifact's verified bytes from its producing result to the approval's.</summary>
    private static void Forward(RunRecord record, InputRecord inputs, ResultId result, RunStorage storage)
    {
        foreach (var binding in inputs.Bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency))
        {
            var source = record.Results.Single(candidate => candidate.Id == binding.Result);
            foreach (var artifact in source.Artifacts)
            {
                byte[] bytes;
                try { bytes = storage.ReadArtifact(source.Id, artifact); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw Fault(MaterializationProblem.InputUnavailable, $"Result {source.Id.Value:D}, stored path {artifact.StoredPath}: {error.Message}", new BlockScope.Operation()); }
                RunStorage.Publish(storage.Folder, RunStorage.ArtifactPath(result, artifact.Name), bytes, artifact.Content, artifact.ByteLength);
            }
        }
    }

    /// <summary>Records the block. It names no inputs, because a blocked request records none.</summary>
    private GatePreparation GateBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, block with { Inputs = null }) switch
        {
            Preparation.Blocked blocked => new GatePreparation.Blocked(blocked.Block),
            Preparation.Rejected rejected => new GatePreparation.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
