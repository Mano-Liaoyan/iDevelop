using System.Text;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Projects;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

public sealed class PreparationBoundaryTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("digest")]
    [InlineData("length")]
    public async Task Unavailable_artifacts_block_before_reservation_with_source_and_stored_path(string mode)
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var result = await f.Publish(T, f.A, artifact: true);
        var path = Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, result.Artifacts[0].StoredPath);
        if (mode == "missing") File.Delete(path);
        else File.WriteAllBytes(path, mode == "digest" ? [68, 0, 127] : [67, 0]);
        var operation = f.Op();
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(U, operation));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Contains("4afb8e68-c5ea-8261-a205-38609bc0c482", blocked.Block.Detail);
        Assert.Contains("results/4afb8e68-c5ea-8261-a205-38609bc0c482/artifacts/payload", blocked.Block.Detail);
        Assert.Equal(operation, Assert.Single(f.Read().Blocks).Value.Block.Operation);
        Assert.Single(f.Read().Attempts);
        Assert.Equal(0, f.Read().Preparations.Values.Count(p => f.Read().Attempts[p.Launch.Attempt].Task == U));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(f.Git.Folder, "a.txt")));
    }

    [Fact]
    public async Task Evidence_is_immutable_and_different_delivered_bytes_are_preserved()
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var operation = f.Op();
        var first = storage.WriteEvidence(operation, "failure.txt", Encoding.UTF8.GetBytes("conflict\n"));
        Assert.Equal(9, first.ByteLength);
        Assert.Equal("36035986117d4e7d84f9ed7f7fbfbb07ece8b7a73d24ed77ebbd7c49b63a8c78", first.Content.Sha256);
        Assert.Equal(first, storage.WriteEvidence(operation, "failure.txt", Encoding.UTF8.GetBytes("conflict\n")));
        Assert.Throws<IOException>(() => storage.WriteEvidence(operation, "failure.txt", Encoding.UTF8.GetBytes("changed\n")));
        Assert.Equal("conflict\n", File.ReadAllText(Path.Combine(storage.Folder, first.RelativePath)));
        await f.Publish(T, f.A);
        var steps = new List<string>();
        var prepare = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: name =>
        {
            steps.Add(name);
            if (name == "journal.prepared.before") throw new Crash();
        }).Prepare(f.Lease(U), prepare, new AttemptCause.Initial()));
        var input = f.Read().Inputs.Values.Single(input => input.Task == U);
        var checkout = Path.Combine(f.Git.Folder, ".worktrees", f.Read().RunKey!, f.Read().TaskKeys[U]);
        f.Git.Write(input.Files[0].RelativePath, "mine\n", checkout);
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(U, prepare));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(checkout, input.Files[0].RelativePath)));
        Assert.Single(f.Read().Preparations);
    }

    [Fact]
    public async Task Repository_and_task_locks_refuse_busy_owners_then_the_same_operation_prepares()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var operation = f.Op();
        using (var locked = f.Git.Open().TakeMutationLock())
        {
            Assert.NotNull(locked);
            var rejected = Assert.IsType<Preparation.Rejected>(await f.Prepare(T, operation));
            Assert.Equal("JournalBusy", rejected.Reason.Problem.ToString());
            Assert.Null(f.Read().RunKey);
        }
        f.Release(T);
        using (var locked = StandaloneLease.TryTake(f.Git.Folder, T))
        {
            Assert.NotNull(locked);
            Assert.IsType<LeaseTake.Busy>(f.Permit.TakeTask(T));
            Assert.Equal(1, f.Read().Sequence);
            Assert.Empty(f.Read().Blocks);
        }
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Later_turn_keeps_changes_inputs_location_and_exact_given_prompt()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T) with { Conversation = ConversationMode.Chat }));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var first = ready.Execution;
        var record = f.Read();
        var input = record.Inputs[first.Inputs];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(first.Location.Owner.Task), f.Op(), first.Launch, input, first.PromptHash));
        var definition = record.Revision.Snapshot.Tasks[T];
        var folder = f.Store.AttemptFolder(W, f.RunId, T, first.Launch.Attempt);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(
            At, first.Launch.Attempt, T, definition.Title, definition.Execution!, first.Prompt, "codex", [])
        { RunBinding = new(W, f.RunId, record.Revision.Id, input.Id), Conversation = ConversationMode.Chat }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("fixture")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("More?")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }
        var next = new LaunchKey(first.Launch.Attempt, 2);
        Assert.Equal("InvalidClaim", Assert.IsType<Preparation.Rejected>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[next.Attempt].Task), f.Op(),
            next, " Continue.\n")).Reason.Problem.ToString());
        Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(), first.Launch, new RootExit.Exited(0)));
        Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), first.Launch, Checkpoint(folder)));
        f.Git.Write("left.txt", "ongoing\n", ready.Checkout);
        f.Git.Write("a.txt", "edited\n", ready.Checkout);
        var operation = f.Op();
        var second = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[next.Attempt].Task), operation, next, " Continue.\n"));
        Assert.Equal(" Continue.\n", second.Execution.Prompt);
        Assert.Equal(new InputId(Guid.Parse("00000000-0000-0000-0000-000000000101")), second.Execution.Inputs);
        Assert.Equal(first.Location, second.Execution.Location);
        Assert.Equal(ready.Checkout, second.Checkout);
        Assert.Equal("ongoing\n", File.ReadAllText(Path.Combine(second.Checkout, "left.txt")));
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(second.Checkout, "a.txt")));
        Assert.Equal(second, Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[next.Attempt].Task), operation, next, " Continue.\n")));
        f.Git.Run(second.Checkout, "checkout", "--detach", "HEAD");
        Assert.Equal("UncertainOwnership", Assert.IsType<Preparation.Blocked>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[next.Attempt].Task), operation,
            next, " Continue.\n")).Block.Problem.ToString());
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(second.Checkout, "a.txt")));
    }

    [Theory]
    [InlineData("journal.worktree-blocked.before")]
    [InlineData("journal.worktree-blocked.after")]
    public async Task Interrupted_block_recording_preserves_the_directory_and_allows_creation_reconciliation(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var operation = f.Op();
        f.Git.Write(".worktrees/93f23689/90d5b0a2/keep", "mine\n");
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
        Assert.Equal("mine\n", File.ReadAllText(f.Git.PathOf(".worktrees/93f23689/90d5b0a2/keep")));
        File.Move(f.Git.PathOf(".worktrees/93f23689/90d5b0a2/keep"), f.Git.PathOf("saved"));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", ready.Execution.Location.AttemptBase.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Single(f.Read().Preparations);
    }

    [Theory]
    [InlineData("git.submodules.before")]
    [InlineData("git.submodules.after")]
    [InlineData("invalid")]
    public async Task Submodule_initialization_is_retried_and_failure_is_recorded_without_deleting_checkout_files(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var exclude = Path.Combine(f.Git.Open().CommonDirectory, "info", "exclude");
        File.AppendAllText(exclude, "\n/.gitmodules\n");
        var operation = f.Op();
        var materializer = f.Materializer(probe: step =>
        {
            if (step == "journal.worktree-observed.after")
                f.Git.Write(".worktrees/93f23689/90d5b0a2/.gitmodules", point == "invalid" ? "[broken\n" : "");
            if (step == point) throw new Crash();
        });
        if (point == "invalid")
        {
            var blocked = Assert.IsType<Preparation.Blocked>(await materializer.Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
            Assert.Equal("SubmoduleUnavailable", blocked.Block.Problem.ToString());
            Assert.Equal("[broken\n", File.ReadAllText(f.Git.PathOf(".worktrees/93f23689/90d5b0a2/.gitmodules")));
            f.Git.Write(".worktrees/93f23689/90d5b0a2/.gitmodules", "");
        }
        else await Assert.ThrowsAsync<Crash>(async () => await materializer.Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", ready.Execution.Location.AttemptBase.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Single(f.Read().Preparations);
    }

    private sealed class Crash : Exception;
}
