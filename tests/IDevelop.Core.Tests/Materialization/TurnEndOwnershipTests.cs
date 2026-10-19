using IDevelop.Core.Tests.Git;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class TurnEndOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_foreign_commit_before_capture_or_after_closure_blocks_the_writer_but_not_its_sibling(bool beforeCapture)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = await PrepareAndClaim(f, U);
        f.Git.Write("u.txt", "U\n", sibling.Checkout);
        await f.Close(sibling);
        var writer = await PrepareAndClaim(f, T);
        var writerTip = CommitFile(f, writer, "result.txt", "writer\n");
        var log = f.ObserveAndLog(writer);
        if (beforeCapture) CommitFile(f, writer, "foreign.txt", "foreign\n");
        var operation = f.Op();
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, writer.Execution.Launch, log));
        if (beforeCapture)
            Assert.Equal("UncertainOwnership", Assert.IsType<CaptureDisposition.Diverged>(settled.Disposition).Problem.ToString());
        else Assert.IsType<CaptureDisposition.Matched>(settled.Disposition);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, operation, writer.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, log));
        var foreign = beforeCapture ? GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch!))!.Value
            : CommitFile(f, writer, "foreign.txt", "foreign\n");

        Assert.Equal("UncertainOwnership", Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
            f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem.ToString());
        Assert.Empty(f.Read().Results);
        Assert.Single(f.Read().Captures[settled.Capture]);
        Assert.Equal("writer\n", f.Git.Git("show", f.Read().Captures[settled.Capture][0].Candidate!.Value.Hex + ":result.txt"));
        Assert.Equal(foreign, GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch!)));
        Assert.Equal("foreign\n", f.Git.Git("show", foreign.Hex + ":foreign.txt"));

        // The sibling's result is its own branch's commit, whatever the writer's branch holds.
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(U), f.Op(), sibling.Execution.Launch.Attempt));
        Assert.Equal("U\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":u.txt"));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task A_different_descendant_after_closure_cannot_replace_the_captured_writer_commit()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = await PrepareAndClaim(f, T);
        var writerTip = CommitFile(f, writer, "result.txt", "writer\n");
        await f.Close(writer);
        var capture = f.Read().Settlements[writer.Execution.Launch];
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "--hard", writer.Execution.Location.AttemptBase.Hex).ExitCode);
        var foreign = CommitFile(f, writer, "foreign.txt", "foreign\n");

        Assert.Equal("UncertainOwnership", Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(
            f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem.ToString());
        Assert.IsType<GitAncestry.No>(f.Git.Open().IsAncestor(writerTip, foreign));
        Assert.IsType<GitAncestry.Yes>(f.Git.Open().IsAncestor(writer.Execution.Location.AttemptBase, foreign));
        Assert.Equal(foreign, GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch!)));
        Assert.Equal("foreign\n", File.ReadAllText(Path.Combine(writer.Checkout, "foreign.txt")));
        Assert.Equal("writer\n", f.Git.Git("show", f.Read().Captures[capture][0].Candidate!.Value.Hex + ":result.txt"));
        Assert.Empty(f.Read().Results);

        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "--hard", writerTip.Hex).ExitCode);
        Recheck(f, writer.Execution.Launch.Attempt);
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("writer\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Salvage_does_not_adopt_an_unpublished_rewind_or_authorize_a_retry()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = await PrepareAndClaim(f, U);
        f.Git.Write("u.txt", "U\n", sibling.Checkout);
        await f.Close(sibling);
        var writer = await PrepareAndClaim(f, T);
        var writerTip = CommitFile(f, writer, "result.txt", "writer\n");
        await f.Close(writer);
        var repository = f.Git.Open();
        var branch = writer.Execution.Location.Owner.Branch!;
        var basis = writer.Execution.Location.AttemptBase;
        Assert.True(RefOwnership.Accepts(f.Read(), repository, branch, writerTip));
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "--hard", basis.Hex).ExitCode);
        var retained = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), writer.Execution.Launch.Attempt));
        Assert.False(RefOwnership.Accepts(f.Read(), repository, branch, basis));
        Assert.True(RefOwnership.Accepts(f.Read(), repository, branch, writerTip));
        Assert.Equal("A\n", f.Git.Git("show", retained.Commit.Hex + ":a.txt"));
        Assert.Equal("UncertainOwnership", Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(
            f.Lease(T), f.Op(), retained.Receipt.Plan, f.Op())).Block.Problem.ToString());
        Assert.Equal(0, f.Read().GitIntents.Values.Count(intent => intent.Mutation is GitMutation.ResetCheckout));
        Assert.Equal(basis, GitFixture.Read(repository.ReadRef(branch)));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(writer.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(writer.Checkout, "result.txt")));
        Assert.Equal("writer\n", f.Git.Git("show", f.Read().Captures[f.Read().Settlements[writer.Execution.Launch]][0].Candidate!.Value.Hex + ":result.txt"));

        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "--hard", writerTip.Hex).ExitCode);
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(U), f.Op(), sibling.Execution.Launch.Attempt));
        Assert.Equal("U\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":u.txt"));
        Assert.Equal(1, f.Read().Results.Count(result => result.Task == U));
        var control = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal(basis, Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(), control.Receipt.Plan, f.Op())).Target);
        Assert.Equal(1, f.Read().GitIntents.Values.Count(intent => intent.Mutation is GitMutation.ResetCheckout));
        Assert.Equal("writer\n", f.Git.Git("show", control.Commit.Hex + ":result.txt"));
    }

    [Fact]
    public async Task A_legitimate_claimed_writer_commit_is_accepted_and_delivered_to_its_dependent()
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var writer = await PrepareAndClaim(f, T);
        CommitFile(f, writer, "result.txt", "writer\n");
        await f.Close(writer);
        Assert.Equal("Explained", f.Read().RootExits[writer.Execution.Launch].Ownership.ToString());
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("writer\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Single(f.Read().Results);
        var dependent = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        Assert.Equal("writer\n", File.ReadAllText(Path.Combine(dependent.Checkout, "result.txt")));
        Assert.Single(f.Read().Claims);
    }

    [Fact]
    public async Task A_process_crash_after_the_first_capture_is_recovered_by_one_matching_capture()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var operation = f.Op();
        f.ReleaseControl();
        using (var racer = new Racer("settle-crash", f.Git.Folder, W.Value.ToString("D"), f.RunId.Value.ToString("D"),
            T.Value.ToString("D"), writer.Execution.Launch.Attempt.Value.ToString("D"), operation.Value.ToString("D")))
        {
            Assert.Equal("Owned:", await racer.Line());
            Assert.Equal("journal.capture-1.after", await racer.Line());
            await racer.Exit();
            Assert.Equal(73, racer.ExitCode);
        }
        var owned = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(W, f.RunId));
        using (owned.Permit)
        {
            using var lease = Assert.IsType<LeaseTake.Taken>(owned.Permit.TakeTask(T)).Lease;
            Assert.Equal(new[] { writer.Execution.Launch }, owned.Fenced);
            Assert.Equal(new[] { writer.Execution.Launch }, f.Read().Fenced);
            var observation = Assert.Single(Assert.Single(f.Read().Captures).Value);
            Assert.Equal("done\n", f.Git.Git("show", observation.Candidate!.Value.Hex + ":result.txt"));
            foreach (var retry in new[] { operation, f.Op() })
                Assert.Equal("UnresolvedOwnership", Assert.IsType<Settlement.Rejected>(await f.Materializer().Settle(
                    lease, retry, writer.Execution.Launch, observation.Log)).Reason.Problem.ToString());
            var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().RecoverSettlement(lease, f.Op(), writer.Execution.Launch));
            Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
            Assert.Equal(RunJournal.Canonical(observation), RunJournal.Canonical(Assert.Single(f.Read().Captures[closed.Capture])));
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(owned.Permit, f.Op(), writer.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, observation.Log));
            var published = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(lease, f.Op(), writer.Execution.Launch.Attempt));
            Assert.Equal("done\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex + ":result.txt"));
            Assert.Equal((1, 1), (f.Read().Results.Count, f.Read().Claims.Count));
        }

        var control = await PrepareAndClaim(f, U);
        f.Git.Write("u.txt", "U\n", control.Checkout);
        await f.Close(control);
        var accepted = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(U), f.Op(), control.Execution.Launch.Attempt));
        Assert.Equal("U\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":u.txt"));
        Assert.Equal(2, f.Read().Results.Count);
    }

    private static void Recheck(PreparationFixture f, AttemptId attempt)
    {
        var record = f.Read();
        var block = Assert.Single(record.Blocks, pair => pair.Value.Block.Attempt == attempt && !pair.Value.Resolved).Key;
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(block, "Rechecked by the person.")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_of_a_closed_attempt_rejects_an_active_Continue_on_its_checkout(bool reset)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var first = await PrepareAndClaim(f, T);
        f.Git.Write("result.txt", "a\n", first.Checkout);
        await f.Close(first);
        var retained = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), first.Execution.Launch.Attempt));
        var next = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.Continue(first.Execution.Launch.Attempt, f.Op())));
        Assert.Equal(first.Execution.Location.Owner, next.Execution.Location.Owner);
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), next.Execution.Launch,
            f.Read().Inputs[next.Execution.Inputs], next.Execution.PromptHash));
        f.Git.Write("result.txt", "b\n", next.Checkout);
        var sequence = f.Read().Sequence;
        if (reset)
            Assert.Equal("UnresolvedOwnership", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(
                f.Lease(T), f.Op(), retained.Receipt.Plan, f.Op())).Reason.Problem.ToString());
        else
            Assert.Equal("UnresolvedOwnership", Assert.IsType<Salvage.Rejected>(await f.Materializer().Salvage(
                f.Lease(T), f.Op(), first.Execution.Launch.Attempt)).Reason.Problem.ToString());
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal("b\n", File.ReadAllText(Path.Combine(next.Checkout, "result.txt")));
        Assert.Single(GitFixture.Read(f.Git.Open().RefSnapshot($"refs/idp/{f.Read().RunKey}/salvage/", $"refs/idp/{f.Read().RunKey}/resalvage/")));

        var log = f.ObserveAndLog(next);
        var control = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), first.Execution.Launch.Attempt));
        Assert.Equal("b\n", f.Git.Git("show", control.Commit.Hex + ":result.txt"));
        Assert.Equal(2, f.Read().Salvages.Count);
        Assert.Equal(2, GitFixture.Read(f.Git.Open().RefSnapshot($"refs/idp/{f.Read().RunKey}/salvage/",
            $"refs/idp/{f.Read().RunKey}/resalvage/")).Count);
        Assert.Equal("UnresolvedOwnership", Assert.IsType<RetryReset.Rejected>(f.Materializer().ResetForRetry(
            f.Lease(T), f.Op(), control.Receipt.Plan, f.Op())).Reason.Problem.ToString());
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), next.Execution.Launch, log));
        Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
        var restored = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(), control.Receipt.Plan, f.Op()));
        Assert.Equal(first.Execution.Location.AttemptBase, restored.Target);
        Assert.Equal(1, f.Read().GitIntents.Values.Count(intent => intent.Mutation is GitMutation.ResetCheckout));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(next.Checkout, "a.txt")));
    }

    private static async Task<Preparation.Ready> PrepareAndClaim(PreparationFixture f, TaskId task)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(task));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(task), f.Op(), ready.Execution.Launch,
            f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));
        return ready;
    }

    private static CommitId CommitFile(PreparationFixture f, Preparation.Ready ready, string path, string bytes)
    {
        f.Git.Write(path, bytes, ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "--", path).ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", path).ExitCode);
        return GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch!))!.Value;
    }
}
