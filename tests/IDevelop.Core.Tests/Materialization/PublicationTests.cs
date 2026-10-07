using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class PublicationTests
{
    private static readonly OperationId Operation = new(Id(2000));

    [Fact]
    public async Task Own_branch_commit_edits_and_additions_publish_exact_code_and_align_the_real_index()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", published.Result.Id.Value.ToString("D"));
        var code = Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code;
        Assert.Equal(T, code.Owner);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", code.Commit.Hex);
        Assert.Equal("435dc9f6defa1902ff86e44aca393b16fdf82718", code.Tree.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", code.AttemptBase.Hex);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", GitFixture.Read(f.Git.Open().ReadRef(code.ResultRef))?.Hex);
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        Assert.Equal("435dc9f6defa1902ff86e44aca393b16fdf82718", GitFixture.Read(f.Git.Open().ReadIndexTree(ready.Checkout)).Hex);
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("B ready.\n", published.Result.Report);
        Assert.Equal(new[] { "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" }, GitFixture.Read(f.Git.Open().ReadCommit(code.Commit)).Parents.Select(p => p.Hex));
        File.WriteAllText(Path.Combine(ready.Checkout, "new.txt"), "later\n");
        Assert.Equal(published, Assert.IsType<Publication.Accepted>(f.Materializer(boundary: new UnprovenBoundary())
            .Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt)));
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted);
    }

    [Fact]
    public async Task A_late_file_is_preserved_and_blocks_acceptance_then_the_same_plan_can_be_verified()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var late = Path.Combine(ready.Checkout, "late.txt");
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "git.branch.after") File.WriteAllText(late, "late\n");
        }).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(OperationIds.Derive(Operation, "plan"), blocked.Block.Operation);
        Assert.Empty(f.Read().Results);
        Assert.Equal("late\n", File.ReadAllText(late));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        File.Delete(late);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        Assert.True(Assert.Single(f.Read().Blocks).Value.Resolved);
        Assert.Equal("Publication verified.", Assert.Single(f.Read().Receipts.Values.Select(e => e.Event).OfType<RunEvent.BlockResolved>()).Reason);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-attempt")]
    [InlineData("task-lock")]
    [InlineData("final")]
    public async Task Publication_requires_explicit_matching_quiescence_and_the_task_lock(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var boundary = new Boundary(mode);
        using var held = mode == "task-lock" ? RunLock.TryTake(DataFolder.Attempts(f.Git.Folder), T) : null;
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(boundary: boundary).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("LiveWriter", blocked.Block.Problem.ToString());
        Assert.Empty(f.Read().Results);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal(mode == "final" ? OperationIds.Derive(Operation, "plan") : Operation, blocked.Block.Operation);
    }

    [Fact]
    public async Task Preconditions_reject_read_only_and_unclosed_or_failed_attempts()
    {
        using var reader = new PreparationFixture(FixtureWorkflow(Task()));
        var read = Assert.IsType<Preparation.Ready>(await reader.Prepare(T));
        Assert.Equal("UnsupportedResult", Assert.IsType<Publication.Rejected>(reader.Materializer().Publish(W, reader.RunId, Operation,
            read.Execution.Launch.Attempt)).Reason.Problem.ToString());
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(W, f.RunId, Operation,
            ready.Execution.Launch.Attempt)).Reason.Problem.ToString());
        f.Close(ready, outcome: TerminalAttemptOutcome.Failed);
        Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(W, f.RunId, Operation,
            ready.Execution.Launch.Attempt)).Reason.Problem.ToString());
        Assert.Equal("UnknownAttempt", Assert.IsType<Publication.Rejected>(f.Materializer().Publish(W, f.RunId, Operation, new(Id(999)))).Reason.Problem.ToString());
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Every_emitted_publication_probe_converges_on_the_same_literal_result_and_commit()
    {
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(FixtureWorkflow(Writer(T))))
        {
            var ready = await ChangedWriter(baseline, artifact: true);
            Assert.IsType<Publication.Accepted>(baseline.Materializer(probe: steps.Add).Publish(W, baseline.RunId, Operation, ready.Execution.Launch.Attempt));
        }
        Assert.Contains("journal.accepted.after", steps);
        Assert.Contains("git.align-index.after", steps);
        Assert.Contains("artifact.payload.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = await ChangedWriter(f, artifact: true);
            Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
            var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
            Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", published.Result.Id.Value.ToString("D"));
            Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
            Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted);
            Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
            Assert.Equal(new byte[] { 67, 0, 127 }, new RunStorage(f.Git.Folder, W, f.RunId).ReadArtifact(published.Result.Id, published.Result.Artifacts.Single()));
        }
    }

    [Fact]
    public async Task Unexpected_index_after_the_plan_is_preserved_and_never_overwritten()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        byte[]? changed = null;
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step != "journal.plan.after") return;
            Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", "--force-remove", "a.txt").ExitCode);
            changed = File.ReadAllBytes(index);
        }).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(changed, File.ReadAllBytes(index));
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Empty(f.Read().Results);
        Assert.Equal("DirtyWorktree", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, Operation,
            ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        Assert.Equal(changed, File.ReadAllBytes(index));
    }

    [Fact]
    public async Task An_invalid_index_after_alignment_blocks_as_dirty_and_keeps_the_unexpected_bytes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "git.align-index.after") File.WriteAllBytes(index, [68, 73, 82, 67]);
        }).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(new byte[] { 68, 73, 82, 67 }, File.ReadAllBytes(index));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Empty(f.Read().Results);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Interrupted_block_recording_retains_the_plan_and_verifies_it_after_the_late_file_is_removed(string side)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var late = Path.Combine(ready.Checkout, "late.txt");
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step == "git.branch.after") File.WriteAllText(late, "late\n");
            if (step == "journal.verify-blocked." + side) throw new Crash();
        }).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("late\n", File.ReadAllText(late));
        Assert.Empty(f.Read().Results);
        File.Delete(late);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        Assert.Single(f.Read().Results);
        Assert.All(f.Read().Blocks.Values, block => Assert.True(block.Resolved));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Lost_block_resolution_acknowledgement_keeps_one_acceptance_and_resolves_once(string side)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var late = Path.Combine(ready.Checkout, "late.txt");
        Assert.Equal("DirtyWorktree", Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "git.branch.after") File.WriteAllText(late, "late\n");
        }).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        File.Delete(late);
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step.StartsWith("journal.resolve-", StringComparison.Ordinal) && step.EndsWith("." + side, StringComparison.Ordinal)) throw new Crash();
        }).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", published.Result.Id.Value.ToString("D"));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted);
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.BlockResolved);
        Assert.True(Assert.Single(f.Read().Blocks).Value.Resolved);
    }

    [Fact]
    public async Task Publication_accepted_before_lock_acquisition_is_returned_without_recapturing()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await ChangedWriter(f);
        var boundary = new CallbackBoundary(attempt =>
        {
            Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, Operation, attempt));
            File.WriteAllText(Path.Combine(ready.Checkout, "new.txt"), "later\n");
        });
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer(boundary: boundary).Publish(W, f.RunId, Operation, ready.Execution.Launch.Attempt));
        Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", accepted.Result.Id.Value.ToString("D"));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex);
        Assert.Equal("later\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.ResultAccepted);
    }

    private sealed class CallbackBoundary(Action<AttemptId> callback) : IExecutionBoundary
    {
        public WriterState Inspect(AttemptId attempt)
        {
            callback(attempt);
            return new WriterState.Quiescent(attempt, "fixture");
        }
    }

    internal static async Task<Preparation.Ready> ChangedWriter(PreparationFixture f, bool artifact = false)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("b.txt", "B\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "b").ExitCode);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67\n", f.Git.Run(ready.Checkout, "rev-parse", "HEAD").Text);
        f.Git.Write("a.txt", "A captured\n", ready.Checkout);
        f.Git.Write("new.txt", "new\n", ready.Checkout);
        if (artifact)
        {
            var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
            File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
            File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
        }
        f.Close(ready);
        return ready;
    }

    private sealed class Boundary(string mode) : IExecutionBoundary
    {
        private int _calls;
        public WriterState Inspect(AttemptId attempt) => mode switch
        {
            "missing" => new WriterState.Unproven("No process tree owner."),
            "wrong-attempt" => new WriterState.Quiescent(new(Id(999)), "fixture"),
            "final" when ++_calls > 1 => new WriterState.Unproven("Ownership expired."),
            _ => new WriterState.Quiescent(attempt, "fixture"),
        };
    }
    internal sealed class Crash : Exception;
}
