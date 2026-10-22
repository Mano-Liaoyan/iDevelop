using System.Collections.Immutable;
using System.Text.Json;

namespace IDevelop.Execution;

/// <summary>What making an editing attempt's last commit its branch's tip came to.</summary>
internal abstract record ResultCommit
{
    private ResultCommit() { }
    internal sealed record Committed(CommitId Commit) : ResultCommit;
    internal sealed record Blocked(MaterializationBlock Block) : ResultCommit;
    internal sealed record Rejected(RunRejection Reason) : ResultCommit;
}

/// <summary>What accepting an agent attempt's report as its result came to.</summary>
internal abstract record Acceptance
{
    private Acceptance() { }
    internal sealed record Accepted(ResultRecord Result) : Acceptance;

    /// <summary>
    /// A Merge node's attempt has no result of its own. <paramref name="Commit"/> is the merge it made, or null when its
    /// agent left conflict markers, and then the conflict stays open.
    /// </summary>
    internal sealed record Merged(CommitId? Commit) : Acceptance;
    internal sealed record Blocked(MaterializationBlock Block) : Acceptance;
    internal sealed record Rejected(RunRejection Reason) : Acceptance;
}

internal sealed partial class Materializer
{
    /// <summary>
    /// Accepts a successfully closed agent attempt's report as its result. An editing attempt's last commit first becomes
    /// its branch's tip, through <see cref="CommitResult"/>, and its result carries that commit as its code (ADR 0032).
    /// </summary>
    public Acceptance Accept(RunLease lease, OperationId operation, AttemptId attempt)
    {
        // The task's authority lasts until the result is accepted, so a release while the branch moves waits for it.
        using var authority = lease.Use();
        if (authority is null) return new Acceptance.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Acceptance.Rejected(new(problem));
        var permit = lease.Permit;
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Attempts.TryGetValue(attempt, out var owner)) return new Acceptance.Rejected(new(RunProblem.UnknownAttempt));
            // A Merge node's attempt resolves a conflict: the merge its capture made is all it leaves.
            if (RunReducer.Merges(record, attempt))
                return record.Closures.GetValueOrDefault(attempt) is AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded }
                    ? new Acceptance.Merged(RunReducer.MergeResult(record, attempt)) : new Acceptance.Rejected(new(RunProblem.OutcomeMismatch));
            if (RunReducer.Editable(record, owner))
            {
                switch (CommitResult(lease, OperationIds.Derive(operation, "commit"), attempt))
                {
                    case ResultCommit.Rejected rejected: return new Acceptance.Rejected(rejected.Reason);
                    case ResultCommit.Blocked blocked: return new Acceptance.Blocked(blocked.Block);
                }
                record = Read(permit.Workflow, permit.Run);
            }
            if (record.Results.FirstOrDefault(result => result.Origin is ResultOrigin.Executed executed && executed.Attempt == attempt) is { } existing)
                return new Acceptance.Accepted(existing);
            if (record.Closures.GetValueOrDefault(attempt) is not AttemptEnd.Logged closure) return new Acceptance.Rejected(new(RunProblem.OutcomeMismatch));
            var evidence = AttemptEvidence.Read(_store.AttemptFolder(permit.Workflow, permit.Run, owner.Task, attempt), closure.Evidence);
            if (evidence.Record is not { Result: { } report } logged)
                return new Acceptance.Rejected(evidence.Rejection ?? new(RunProblem.OutcomeMismatch));
            if (!record.Claims.TryGetValue(new(attempt, logged.Turns.Count), out var claim)) return new Acceptance.Rejected(new(RunProblem.InvalidClaim));
            return _store.AcceptReport(permit, operation, attempt, claim.Inputs.Id, report,
                record.CurrentResults.GetValueOrDefault(owner.Task)?.Id) switch
            {
                RunDecision.Rejected refused => new Acceptance.Rejected(refused.Reason),
                var decision => new Acceptance.Accepted(((RunEvent.ResultAccepted)DecisionEvent(decision)).Result),
            };
        }
        catch (Refusal refused) { return new Acceptance.Rejected(refused.Reason); }
    }

    /// <summary>
    /// Makes the commit a successful editing attempt's last turn ended with its task branch's tip, and aligns the worktree's
    /// index with it, so the attempt's report can be accepted as its result with that commit as its code (ADR 0032). A
    /// capture that did not match its turn blocks the result. A repeat converges.
    /// </summary>
    public ResultCommit CommitResult(RunLease lease, OperationId operation, AttemptId attempt)
    {
        using var authority = lease.Use();
        if (authority is null) return new ResultCommit.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new ResultCommit.Rejected(new(problem));
        var permit = lease.Permit;
        var task = lease.Task;
        InputId? inputs = null;
        var step = "preconditions";
        ImmutableArray<EvidenceFile> evidence = [];
        try
        {
            var record = Read(permit.Workflow, permit.Run);
            if (!record.Attempts.TryGetValue(attempt, out var writer)) return new ResultCommit.Rejected(new(RunProblem.UnknownAttempt));
            if (LeaseProblem(lease, writer.Task) is { } mismatch) return new ResultCommit.Rejected(new(mismatch));
            if (!RunReducer.Editable(record, writer))
                return new ResultCommit.Rejected(new(RunProblem.UnsupportedResult));
            if (record.Closures.GetValueOrDefault(attempt) is not AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded })
                return new ResultCommit.Rejected(new(RunProblem.OutcomeMismatch));
            var launch = record.Claims.Keys.Where(key => key.Attempt == attempt).OrderBy(key => key.Turn).LastOrDefault();
            if (launch.Attempt != attempt || !record.TurnClosures.ContainsKey(launch) || !RunReducer.CaptureSettled(record, launch) ||
                RunReducer.AttemptPlan(record, attempt) is not { } plan)
                return new ResultCommit.Rejected(new(RunProblem.OutcomeMismatch));
            inputs = record.Preparations[launch].Inputs;
            VerifyCapture(record, launch, ref evidence);
            var frozen = RunReducer.ResultCapture(record, launch) ?? throw new Refusal(new(RunProblem.OutcomeMismatch));
            var change = RunReducer.ResultMove(record, launch, frozen);
            if (record.Results.Any(result => result.Origin is ResultOrigin.Executed executed && executed.Attempt == attempt))
                return new ResultCommit.Committed(change.Target);
            if (record.Phase is not (RunPhase.Approved or RunPhase.StopRequested)) return new ResultCommit.Rejected(new(RunProblem.RunStopped));
            step = "repository";
            var repository = OpenRepository();
            _probe?.Invoke("commit.lock.before");
            using var mutation = MutationLock(repository);
            if (mutation is null) return new ResultCommit.Rejected(new(RunProblem.JournalBusy));
            record = Read(permit.Workflow, permit.Run);
            VerifyRepository(record, repository);
            var location = record.Preparations[launch].Location;
            VerifyWorktree(repository, location, record);
            step = "branch";
            RequirePublication(_refs.Publish(permit, operation, plan, "result-branch", repository, change), new BlockScope.Checkout([], Branch: true));
            step = "index";
            // The worktree's files are the commit's tree, so its index follows the branch. A person's staged change stays.
            var aligned = Mutate("align-index", () => repository.AlignIndex(Checkout(repository, location.Owner), frozen.Index?.Content, frozen.Recipe.Tree));
            if (aligned is IndexAlignment.Failed failed) throw Fault(MaterializationProblem.GitFailed, failed.Detail, BlockScope.Checkout.Whole);
            ResolveMaintenanceBlocks(permit, operation, "Committed.");
            return new ResultCommit.Committed(change.Target);
        }
        catch (Refusal refused) { return new ResultCommit.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return CommitBlock(permit, operation, step, new(operation, task, attempt, failed.Problem, inputs, evidence, failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return CommitBlock(permit, operation, step, new(operation, task, attempt, MaterializationProblem.InputUnavailable, inputs, evidence, error.Message) { Scope = new BlockScope.Operation() }); }
    }

    /// <summary>A capture that could not be taken, or that diverged from its turn, blocks the attempt's result with what it found.</summary>
    private static void VerifyCapture(RunRecord record, LaunchKey launch, ref ImmutableArray<EvidenceFile> evidence)
    {
        if (RunReducer.CaptureFailure(record, launch) is { } failed)
        {
            evidence = failed.Evidence;
            throw Fault(failed.Problem, failed.Detail, new BlockScope.Operation());
        }
        var capture = record.Captures[launch];
        if (RunReducer.Divergence(record, capture) is not { } diverged) return;
        evidence = [capture.SharedRefs];
        var branch = record.Preparations[launch].Location.Owner.Branch;
        BlockScope scope = diverged.Refs.All(name => name == branch || name == "HEAD")
            ? new BlockScope.Checkout([], branch is not null && diverged.Refs.Contains(branch), diverged.Refs.Contains("HEAD"))
            : new BlockScope.Refs(diverged.Refs);
        throw Fault(MaterializationProblem.UncertainOwnership, diverged.Detail + " Paths or refs: " + string.Join(", ", diverged.Refs), scope);
    }

    private static void RequirePublication(RefPublication outcome, BlockScope scope)
    {
        if (outcome is RefPublication.Rejected rejected) throw new Refusal(rejected.Reason);
        if (outcome is RefPublication.Blocked blocked) throw Fault(blocked.Problem, blocked.Detail, scope);
    }

    private static bool PublicationObserved(RunRecord record, OperationId plan, GitMutation mutation) =>
        record.GitIntents.Any(pair => pair.Value.Plan == plan && RunReducer.Same(pair.Value.Mutation, mutation) &&
            record.GitObservations.ContainsKey(pair.Key));

    private ResultCommit CommitBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, block) switch
        {
            Preparation.Blocked blocked => new ResultCommit.Blocked(blocked.Block),
            Preparation.Rejected rejected => new ResultCommit.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
