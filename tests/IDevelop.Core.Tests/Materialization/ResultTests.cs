using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

/// <summary>
/// An editing node's result is its report and artifacts, and the commit its last turn ended with is its code once that
/// commit is its branch's tip (ADR 0032).
/// </summary>
public sealed class ResultTests
{
    [Fact]
    public async Task An_editing_attempts_turn_commit_becomes_its_branch_tip_and_its_report_its_result()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await f.ChangedWriter(artifact: true);
        var attempt = ready.Execution.Launch.Attempt;
        var operation = f.Op();

        var result = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), operation, attempt)).Result;

        var record = f.Read();
        var capture = Assert.Single(record.Captures[record.Settlements[ready.Execution.Launch]]);
        var code = Assert.IsType<CodeOutput.Produced>(result.Code).Code;
        Assert.Equal(capture.Candidate, code.Commit);
        Assert.Equal(ready.Execution.Location.AttemptBase, code.AttemptBase);
        Assert.Equal(ready.Execution.Location.Owner.Branch, code.ResultRef);
        Assert.Equal("B ready.\n", result.Report);
        Assert.Equal(code.Commit.Hex, f.Git.Git("rev-parse", ready.Execution.Location.Owner.Branch!).Trim());
        Assert.Equal("A captured\n", f.Git.Git("show", code.Commit.Hex + ":a.txt"));
        Assert.Equal("new\n", f.Git.Git("show", code.Commit.Hex + ":new.txt"));
        // The worktree's index follows its branch, so the worktree is clean at its result.
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("payload", artifact.Name);
        Assert.Equal(RunStorage.ArtifactPath(result.Id, "payload"), artifact.StoredPath);
        Assert.Equal([67, 0, 127], new RunStorage(f.Git.Folder, W, f.RunId).ReadArtifact(result.Id, artifact));

        Assert.DoesNotContain(record.Plans.Values, plan => plan is MaterializationPlan.Publication);
        Assert.Equal("", f.Git.Git("for-each-ref", $"refs/idp/{record.RunKey}/result/").Trim());
        Assert.Equal(result.Id, Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), operation, attempt)).Result.Id);
        Assert.Equal(result.Id, Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), f.Op(), attempt)).Result.Id);
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task A_capture_that_diverged_from_its_turn_blocks_the_result_and_leaves_the_branch()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("b.txt", "B\n", ready.Checkout);
        var checkpoint = f.ObserveAndLog(ready);
        // A commit after the client exited moves the branch the root exit observed.
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "late").ExitCode);
        var late = f.Git.Git("rev-parse", ready.Execution.Location.Owner.Branch!).Trim();
        var operation = f.Op();
        var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), operation, ready.Execution.Launch, checkpoint));
        Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, operation, ready.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, checkpoint));

        var blocked = Assert.IsType<Acceptance.Blocked>(f.Materializer().Accept(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));

        Assert.Equal(MaterializationProblem.UncertainOwnership, blocked.Block.Problem);
        Assert.Equal(late, f.Git.Git("rev-parse", ready.Execution.Location.Owner.Branch!).Trim());
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task A_failed_branch_move_blocks_the_result_and_a_repeat_accepts_it_once_git_works()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await f.ChangedWriter();
        var attempt = ready.Execution.Launch.Attempt;
        var operation = f.Op();

        var blocked = Assert.IsType<Acceptance.Blocked>(f.FailingGit("update-ref").Accept(f.Lease(T), operation, attempt));
        Assert.Equal(MaterializationProblem.GitFailed, blocked.Block.Problem);
        Assert.Empty(f.Read().Results);

        var result = Assert.IsType<Acceptance.Accepted>(f.Materializer().Accept(f.Lease(T), operation, attempt)).Result;
        Assert.Equal(Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex, f.Git.Git("rev-parse", ready.Execution.Location.Owner.Branch!).Trim());
        Assert.All(f.Read().Blocks.Values, block => Assert.True(block.Resolved));
    }
}
