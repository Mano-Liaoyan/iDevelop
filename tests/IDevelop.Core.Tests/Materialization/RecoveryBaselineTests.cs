using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class RecoveryBaselineTests
{
    [Fact]
    public async Task Interrupted_fix_continue_keeps_the_commit_made_before_the_crash()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        StartLog(f, ready);
        var k = CommitKeep(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var confirmation = f.Op();
        RecoverStopped(f, ready, confirmation);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
        var block = Assert.Single(f.Read().Blocks);
        Assert.Equal("UncertainOwnership", block.Value.Block.Problem.ToString());
        Assert.Equal(new[] { ready.Execution.Location.Owner.Branch }, block.Value.Block.Scope!.Refs);
        Assert.False(block.Value.Resolved);
        var cause = new AttemptCause.Continue(previous, confirmation);
        Assert.Equal("OutcomeMismatch", Assert.IsType<Preparation.Rejected>(await f.Prepare(T, cause: cause)).Reason.Problem.ToString());
        var unrelated = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, unrelated, new RunEvent.Blocked(
            new(unrelated, T, previous, MaterializationProblem.DirtyWorktree, ready.Execution.Inputs, [], "Unrelated drift.")
            { Scope = new(["other.txt"], [], false) })));
        var operation = f.Op();
        var first = Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), operation, previous, confirmation, preservation));
        var sequence = f.Read().Sequence;
        Assert.Equal(first, f.Materializer().RecordRecoveryBaseline(f.Lease(T), operation, previous, confirmation, preservation));
        Assert.Equal(first, f.Materializer().RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, f.Op()));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal("session-1", first.Receipt.Baseline.Session);
        Assert.Single(f.Read().Baselines);
        Assert.Equal(1, f.Read().Receipts.Values.Count(e => e.Event is RunEvent.RecoveryBaselined));
        Assert.True(f.Read().Blocks[block.Key].Resolved);
        Assert.False(f.Read().Blocks[unrelated].Resolved);
        Assert.Equal("Adopted as the recovery baseline after confirmation " + confirmation.Value.ToString("D"),
            Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
        var continuedOperation = f.Op();
        var continued = Assert.IsType<Preparation.Ready>(await f.Prepare(T, continuedOperation, cause));
        Assert.Equal(continued, await f.Prepare(T, continuedOperation, cause));
        Assert.Equal(1, f.Read().Attempts.Values.Count(a => a.Cause is AttemptCause.Continue));
        Assert.Equal(k, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(continued.Checkout, "keep.txt")));
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-ref", ready.Execution.Location.Owner.Branch, f.A.Hex, k.Hex).ExitCode);
        var replay = Assert.IsType<Preparation.Blocked>(await f.Prepare(T, continuedOperation, cause));
        Assert.Equal("UncertainOwnership", replay.Block.Problem.ToString());
        Assert.Equal(1, f.Read().Preparations.Values.Count(p => p.Launch.Attempt == continued.Execution.Launch.Attempt));
    }

    [Fact]
    public async Task Interrupted_fix_with_a_changing_baseline_cannot_continue()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        StartLog(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var confirmation = f.Op();
        RecoverStopped(f, ready, confirmation);
        f.Git.Write("keep.txt", "first\n", ready.Checkout);
        var preservation = f.Op();
        var diverged = Assert.IsType<Preservation.Blocked>(await f.Materializer(probe: point =>
        {
            if (point == "git.preserve-observe-1.after") f.Git.Write("keep.txt", "second\n", ready.Checkout);
        }).Preserve(f.Lease(T), preservation, previous));
        Assert.Equal("DirtyWorktree", diverged.Block.Problem.ToString());
        Assert.Equal("RecoveryEvidenceInsufficient", Assert.IsType<RecoveryBaselining.Rejected>(f.Materializer()
            .RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, preservation)).Reason.Problem.ToString());
        Assert.Equal("OutcomeMismatch", Assert.IsType<Preparation.Rejected>(await f.Prepare(T,
            cause: new AttemptCause.Continue(previous, confirmation))).Reason.Problem.ToString());
        Assert.Equal(0, f.Read().Attempts.Values.Count(a => a.Cause is AttemptCause.Continue));
        var fresh = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), fresh, previous));
        Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, fresh));
        Assert.IsType<Preparation.Ready>(await f.Prepare(T, cause: new AttemptCause.Continue(previous, confirmation)));
        Assert.Equal(1, f.Read().Attempts.Values.Count(a => a.Cause is AttemptCause.Continue));
    }

    [Fact]
    public async Task Continue_rules()
    {
        using (var f = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            StartLog(f, ready);
            var previous = ready.Execution.Launch.Attempt;
            Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), previous, RecoveryOutcome.NotStarted, f.Op(), "Not started."));
            var preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
            Assert.Equal("OutcomeMismatch", Assert.IsType<RecoveryBaselining.Rejected>(f.Materializer()
                .RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, f.Op(), preservation)).Reason.Problem.ToString());
            Assert.Empty(f.Read().Baselines);
            Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id, new AttemptCause.Retry(previous, f.Op())));
            Assert.Equal(1, f.Read().Plans.Values.OfType<MaterializationPlan.Preparation>().Count(p => p.Cause is AttemptCause.Retry));
        }
        using (var f = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            StartLog(f, ready, session: null);
            var previous = ready.Execution.Launch.Attempt;
            var confirmation = f.Op();
            RecoverStopped(f, ready, confirmation);
            var preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
            Assert.Equal("SessionUnavailable", Assert.IsType<RecoveryBaselining.Rejected>(f.Materializer()
                .RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, preservation)).Reason.Problem.ToString());
            Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id, new AttemptCause.Retry(previous, f.Op())));
            Assert.Equal(1, f.Read().Plans.Values.OfType<MaterializationPlan.Preparation>().Count(p => p.Cause is AttemptCause.Retry));
        }
        using (var f = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            StartLog(f, ready, otherClient: true);
            var previous = ready.Execution.Launch.Attempt;
            var confirmation = f.Op();
            RecoverStopped(f, ready, confirmation);
            var preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
            Assert.Equal("SessionUnavailable", Assert.IsType<RecoveryBaselining.Rejected>(f.Materializer()
                .RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, preservation)).Reason.Problem.ToString());
            Assert.Empty(f.Read().Baselines);
        }
        using (var f = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            await f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
            Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id,
                new AttemptCause.Continue(ready.Execution.Launch.Attempt, f.Op())));
            Assert.Equal(1, f.Read().Plans.Values.OfType<MaterializationPlan.Preparation>().Count(p => p.Cause is AttemptCause.Continue));
        }
        using (var f = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            await CloseInterrupted(f, ready);
            var previous = ready.Execution.Launch.Attempt;
            var confirmation = f.Op();
            Assert.Equal(17L, f.Read().Sequence);
            Assert.Equal(1, f.Read().Receipts.Values.Count(e => e.Event is RunEvent.Planned));
            Assert.Equal("RecoveryEvidenceInsufficient", Assert.IsType<RunDecision.Rejected>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id,
                new AttemptCause.Continue(previous, confirmation))).Reason.Problem.ToString());
            Assert.Equal(17L, f.Read().Sequence);
            Assert.Equal(1, f.Read().Receipts.Values.Count(e => e.Event is RunEvent.Planned));
            var preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
            Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, preservation));
            Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id, new AttemptCause.Continue(previous, confirmation)));
            Assert.Equal(3, f.Read().Receipts.Values.Count(e => e.Event is RunEvent.Planned));
        }
    }

    [Fact]
    public async Task Prepare_rechecks_the_recovery_baseline()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        StartLog(f, ready);
        CommitKeep(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var confirmation = f.Op();
        RecoverStopped(f, ready, confirmation);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
        Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, preservation));
        f.Git.Write("keep.txt", "changed\n", ready.Checkout);
        var operation = f.Op();
        var cause = new AttemptCause.Continue(previous, confirmation);
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(T, operation, cause));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        var continued = Assert.Single(f.Read().Attempts.Values, a => a.Cause is AttemptCause.Continue);
        Assert.Equal(0, f.Read().Preparations.Values.Count(p => p.Launch.Attempt == continued.Id));
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(ready.Checkout, "keep.txt")));
        f.Git.Write("keep.txt", "keep\n", ready.Checkout);
        Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation, cause));
        Assert.Equal(1, f.Read().Preparations.Values.Count(p => p.Launch.Attempt == continued.Id));
        f.Git.Write("keep.txt", "late\n", ready.Checkout);
        Assert.Equal("DirtyWorktree", Assert.IsType<Preparation.Blocked>(await f.Prepare(T, operation, cause)).Block.Problem.ToString());
    }

    [Theory]
    [InlineData("files")]
    [InlineData("branch")]
    [InlineData("lock")]
    public async Task Recording_rechecks_the_preserved_checkout(string component)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        StartLog(f, ready);
        var k = CommitKeep(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var confirmation = f.Op();
        RecoverStopped(f, ready, confirmation);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
        var branch = ready.Execution.Location.Owner.Branch;
        var lockPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)) + ".lock";
        if (component == "files") f.Git.Write("keep.txt", "late\n", ready.Checkout);
        if (component == "branch") Assert.Equal(0, f.Git.Run(ready.Checkout, "update-ref", branch, f.A.Hex, k.Hex).ExitCode);
        if (component == "lock") File.WriteAllText(lockPath, "lock\n");
        var operation = f.Op();
        var blocked = Assert.IsType<RecoveryBaselining.Blocked>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), operation, previous, confirmation, preservation));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The checkout changed after it was preserved. Preserve it again.", blocked.Block.Detail);
        Assert.Empty(f.Read().Baselines);
        if (component == "files") f.Git.Write("keep.txt", "keep\n", ready.Checkout);
        if (component == "branch") Assert.Equal(0, f.Git.Run(ready.Checkout, "update-ref", branch, k.Hex, f.A.Hex).ExitCode);
        if (component == "lock") File.Delete(lockPath);
        Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), operation, previous, confirmation, preservation));
        Assert.Single(f.Read().Baselines);
    }

    [Fact]
    public async Task Recovery_baseline_authorizes_only_its_checkout_tip()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        StartLog(f, ready);
        var k = CommitKeep(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var confirmation = f.Op();
        RecoverStopped(f, ready, confirmation);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
        var repository = f.Git.Open();
        var branch = ready.Execution.Location.Owner.Branch;
        var other = sibling.Execution.Location.Owner.Branch;
        Assert.False(RefOwnership.Accepts(f.Read(), repository, branch, k));
        Assert.False(RefOwnership.Accepts(f.Read(), repository, other, k));
        Assert.True(RefOwnership.Accepts(f.Read(), repository, other, f.A));
        var basis = RefOwnership.Basis(f.Read());
        Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, preservation));
        Assert.Equal(basis + 1, RefOwnership.Basis(f.Read()));
        Assert.True(RefOwnership.Accepts(f.Read(), repository, branch, k));
        Assert.False(RefOwnership.Accepts(f.Read(), repository, branch, f.A));
        Assert.False(RefOwnership.Accepts(f.Read(), repository, other, k));
        Assert.True(RefOwnership.Accepts(f.Read(), repository, other, f.A));
        var folded = CheckoutBaseline.Fold(f.Read(), ready.Execution.Location.Owner, commit => GitFixture.Read(repository.ReadCommit(commit)).Tree);
        Assert.Equal(4, folded.Components.Count);
        Assert.All(folded.Components.Values, component =>
        {
            var source = Assert.IsType<RestoreTarget.Recovery>(Assert.IsType<ComponentBaseline.Fixed>(component).Source);
            Assert.Equal(previous, source.Previous);
            Assert.Equal(confirmation, source.Confirmation);
        });
        Assert.Equal(k.Hex, Assert.IsType<ComponentBaseline.Fixed>(folded.Components["branch"]).Value);
        f.Git.Write("keep.txt", "next\n", ready.Checkout);
        var descendant = CommitFile(f, ready, "keep.txt", "next");
        Assert.False(RefOwnership.Accepts(f.Read(), repository, branch, descendant));
    }

    [Theory]
    [InlineData("journal.baseline.before")]
    [InlineData("journal.baseline.after")]
    public async Task Baseline_recording_converges_at_B1_and_B2(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await CloseInterrupted(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var preservation = f.Op();
        var confirmation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
        var operation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .RecordRecoveryBaseline(f.Lease(T), operation, previous, confirmation, preservation));
        var recorded = Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer()
            .RecordRecoveryBaseline(f.Lease(T), operation, previous, confirmation, preservation));
        Assert.Equal("session-1", recorded.Receipt.Baseline.Session);
        Assert.Equal(1, f.Read().Receipts.Values.Count(e => e.Event is RunEvent.RecoveryBaselined));
        var continuedOperation = f.Op();
        var cause = new AttemptCause.Continue(previous, confirmation);
        var continued = Assert.IsType<Preparation.Ready>(await f.Prepare(T, continuedOperation, cause));
        Assert.Equal(continued, await f.Prepare(T, continuedOperation, cause));
        Assert.Equal(1, f.Read().Attempts.Values.Count(a => a.Cause is AttemptCause.Continue));
    }

    [Fact]
    public async Task A_preserved_lock_requires_restore_before_a_baseline()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await CloseInterrupted(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var confirmation = f.Op();
        var lockPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)) + ".lock";
        File.WriteAllText(lockPath, "lock\n");
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, previous));
        var blocked = Assert.IsType<RecoveryBaselining.Blocked>(f.Materializer()
            .RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, preservation));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("Remove index.lock with Restore first.", blocked.Block.Detail);
        Assert.Equal("lock\n", File.ReadAllText(lockPath));
        Assert.Empty(f.Read().Baselines);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            var preview = Assert.IsType<RestorePreviewRead.Previewed>(f.Materializer().PreviewRestore(f.Lease(T), previous, preservation)).Preview;
            Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), previous, preservation, f.Op(), preview.Identity));
        }
        else File.Delete(lockPath);
        var fresh = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), fresh, previous));
        Assert.IsType<RecoveryBaselining.Recorded>(f.Materializer().RecordRecoveryBaseline(f.Lease(T), f.Op(), previous, confirmation, fresh));
        Assert.Single(f.Read().Baselines);
    }

    private sealed class Crash : Exception;

    private static void StartLog(PreparationFixture f, Preparation.Ready ready, string? session = "session-1", bool otherClient = false)
    {
        var execution = ready.Execution;
        var attempt = f.Read().Attempts[execution.Launch.Attempt];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(attempt.Task), f.Op(), execution.Launch,
            f.Read().Inputs[execution.Inputs], execution.PromptHash));
        var definition = f.Read().Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task];
        var folder = f.Store.AttemptFolder(W, f.RunId, attempt.Task, attempt.Id);
        using var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
            new AttemptEvent.Requested(At, attempt.Id, attempt.Task, definition.Title, otherClient
                ? new IDevelop.Workflows.ExecutionSettings(definition.Execution!.Client == IDevelop.Workflows.ClientId.Pi ? IDevelop.Workflows.ClientId.Codex : IDevelop.Workflows.ClientId.Pi)
                : definition.Execution!, execution.Prompt, "codex", [])
            { RunBinding = new(W, f.RunId, attempt.Revision, execution.Inputs), Conversation = definition.Conversation, Fix = f.Read().ReviewOf(attempt.Id) });
        if (session is not null) log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted(session)));
    }

    internal static async Task CloseInterrupted(PreparationFixture f, Preparation.Ready ready)
    {
        StartLog(f, ready);
        var previous = ready.Execution.Launch.Attempt;
        var folder = f.Store.AttemptFolder(W, f.RunId, ready.Execution.Location.Owner.Task, previous);
        using (var log = AttemptLog.Open(folder))
        {
            log.Append(new AttemptEvent.InterruptRequested(At, "Stopped."));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Done.\n")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }
        Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(), ready.Execution.Launch, new RootExit.Exited(0)));
        var checkpoint = Checkpoint(folder);
        Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, checkpoint));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), previous, TerminalAttemptOutcome.Interrupted, checkpoint));
        Assert.Equal("Interrupted", Assert.IsType<AttemptEnd.Logged>(f.Read().Closures[previous]).Outcome.ToString());
    }

    private static CommitId CommitKeep(PreparationFixture f, Preparation.Ready ready)
    {
        f.Git.Write("keep.txt", "keep\n", ready.Checkout);
        return CommitFile(f, ready, "keep.txt", "keep");
    }

    private static CommitId CommitFile(PreparationFixture f, Preparation.Ready ready, string path, string message)
    {
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", path).ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", message).ExitCode);
        return GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))!.Value;
    }

    private static void RecoverStopped(PreparationFixture f, Preparation.Ready ready, OperationId confirmation)
    {
        f.ReleaseControl();
        _ = f.Permit;
        Assert.Contains(ready.Execution.Launch, f.Read().Fenced);
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt, RecoveryOutcome.Stopped, confirmation, "Stopped."));
        Assert.Equal("Stopped", Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[ready.Execution.Launch.Attempt]).Outcome.ToString());
    }
}
