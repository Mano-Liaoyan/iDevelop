using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class PublicationOwnershipTests
{
    [Fact]
    public async Task Observed_base_retention_is_rechecked_before_preparing_another_task()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Git("update-ref", "refs/idp/93f23689/base", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(U));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Approved base ref has an unexpected value.", blocked.Block.Detail);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal(0, f.Read().Preparations.Values.Count(prepared => prepared.Location.Owner.Task == U));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("review")]
    [InlineData("failed")]
    public async Task A_closed_sibling_created_after_preparation_does_not_block_publication(string mode)
    {
        var siblingDefinition = mode == "read-only" ? Task(U) : mode == "failed" ? Writer(U) :
            new TaskDefinition(U, new Blueprint(new("example.review", 1), "Review",
                new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")), [],
                new(Task().Execution, ConversationMode.Autonomous))) { Title = "Review" };
        var workflow = FixtureWorkflow(Writer(T), siblingDefinition, Writer(C));
        if (mode == "review") workflow = Connect(workflow, C, U);
        using var f = new PreparationFixture(workflow);
        if (mode == "review") await f.Publish(C, f.A);
        var writer = await PublicationTests.ChangedWriter(f);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: mode == "review" ? "Review." : null));
        f.Close(sibling, outcome: mode == "failed" ? TerminalAttemptOutcome.Failed : TerminalAttemptOutcome.Succeeded);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal(mode == "review" ? "bae683e6ffa6b64a4c788e890a69ff3207ce81f5" : "81cae59086bf9597301026b65f1bb57380b74686",
            Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex);
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(writer.Checkout, "a.txt")));
    }

    [Fact]
    public async Task An_observed_sibling_creation_without_a_prepared_receipt_is_explained()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Task(U)));
        var writer = await PublicationTests.ChangedWriter(f);
        await Assert.ThrowsAsync<PublicationTests.Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.worktree-observed.after")
                throw new PublicationTests.Crash();
        }).Prepare(W, f.RunId, f.Op(), U, new AttemptCause.Initial()));
        Assert.Equal(0, f.Read().Preparations.Values.Count(prepared => prepared.Location.Owner.Task == U));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/task/c67f2fc3"))?.Hex);
    }

    [Fact]
    public async Task Both_writers_can_commit_and_close_before_either_publishes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = await PublicationTests.ChangedWriter(f);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", sibling.Execution.Location.AttemptBase.Hex);
        f.Git.Write("c.txt", "C\n", sibling.Checkout);
        Assert.Equal(0, f.Git.Run(sibling.Checkout, "add", "c.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(sibling.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "c").ExitCode);
        Assert.Equal("7025720b8121cfd45a182b2ead881a0c0461beb0\n", f.Git.Run(sibling.Checkout, "rev-parse", "HEAD").Text);
        f.Close(sibling);
        var first = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(first.Result.Code).Code.Commit.Hex);
        var second = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), sibling.Execution.Launch.Attempt));
        Assert.Equal("0a439e0dba85e8be99a65aded6d87d1213697ef0", Assert.IsType<CodeOutput.Produced>(second.Result.Code).Code.Commit.Hex);
        Assert.Equal(2, f.Read().Results.Count);
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(writer.Checkout, "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(sibling.Checkout, "c.txt")));
    }

    [Fact]
    public async Task A_sibling_rewound_below_its_known_commit_blocks_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = await PublicationTests.ChangedWriter(f);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        f.Git.Git("update-ref", sibling.Execution.Location.Owner.Branch, "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000102: unexplained shared ref refs/heads/idp/93f23689/task/c67f2fc3, absent to 7c64b20d5be53b5c1a291863ef191aa28f6f4d51.", blocked.Block.Detail);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch))?.Hex);
        Assert.Empty(f.Read().Results);
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("stash")]
    public async Task Resuming_a_plan_revalidates_ownership_before_moving_refs_or_the_index(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = await PublicationTests.ChangedWriter(f);
        var operation = f.Op();
        Assert.Throws<PublicationTests.Crash>(() => f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new PublicationTests.Crash();
        }).Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        if (mode == "detached") Assert.Equal(0, f.Git.Run(writer.Checkout, "checkout", "--detach", "HEAD").ExitCode);
        else f.Git.Git("update-ref", "refs/stash", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        var index = GitFixture.Read(f.Git.Open().IndexPath(writer.Checkout));
        var before = File.ReadAllBytes(index);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal(before, File.ReadAllBytes(index));
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(RunLayout.ResultRef(f.Read().RunKey!, f.Read().TaskKeys[T], writer.Execution.Launch.Attempt))));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(writer.Checkout, "new.txt")));
        Assert.Empty(f.Read().Results);
    }

    [Theory]
    [InlineData("git.branch.after")]
    [InlineData("git.result.after")]
    public async Task Resuming_adopts_the_plans_unobserved_ref_move(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = await PublicationTests.ChangedWriter(f);
        var operation = new OperationId(Id(2000));
        Assert.Throws<PublicationTests.Crash>(() => f.Materializer(probe: step =>
        {
            if (step == point) throw new PublicationTests.Crash();
        }).Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", published.Result.Id.Value.ToString("D"));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex);
        var intent = OperationIds.Derive(operation, point == "git.branch.after" ? "branch-intent" : "result-intent");
        Assert.True(f.Read().GitObservations[intent].Adopted);
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", f.Read().GitObservations[intent].Value);
        Assert.Equal("", f.Git.Run(writer.Checkout, "status", "--porcelain").Text);
        Assert.Equal(published, f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        Assert.Single(f.Read().Results);
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("rewritten")]
    [InlineData("lock")]
    public async Task Checkout_ownership_blocks_detachment_history_rewrites_and_foreign_registration_locks(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f);
        var operation = f.Op();
        if (mode == "detached") Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "--detach", "HEAD").ExitCode);
        else if (mode == "rewritten") f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        else
        {
            f.Git.Git("worktree", "unlock", ready.Checkout);
            f.Git.Git("worktree", "lock", "--reason", "foreign owner", ready.Checkout);
        }
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Empty(f.Read().Results);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Theory]
    [InlineData("stash")]
    [InlineData("foreign-ref")]
    [InlineData("delete")]
    [InlineData("final-stash")]
    public async Task Unexplained_shared_changes_preserve_before_and_after_evidence(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        f.Git.Git("update-ref", "refs/stash", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        var ready = await PublicationTests.ChangedWriter(f);
        if (mode == "stash") f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        else if (mode == "foreign-ref") f.Git.Git("update-ref", "refs/idp/93f23689/foreign", f.A.Hex);
        else if (mode == "delete") f.Git.Git("update-ref", "-d", "refs/idp/93f23689/base");
        var operation = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (mode == "final-stash" && step == "git.result.after") f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        }).Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal(2, blocked.Block.Evidence.Length);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var before = System.Text.Encoding.UTF8.GetString(RunStorage.Read(storage.Folder, blocked.Block.Evidence[0].RelativePath,
            blocked.Block.Evidence[0].Content, blocked.Block.Evidence[0].ByteLength));
        Assert.Contains("refs/stash", before);
        Assert.Contains("7c64b20d5be53b5c1a291863ef191aa28f6f4d51", before);
        Assert.Empty(f.Read().Results);
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Overlapping_sibling_fast_forward_and_observed_publication_are_explained_by_the_run_journal()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(C)));
        var writer = await PublicationTests.ChangedWriter(f);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        f.Git.Write("c.txt", "C\n", sibling.Checkout);
        Assert.Equal(0, f.Git.Run(sibling.Checkout, "add", "c.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(sibling.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "c").ExitCode);
        var first = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(first.Result.Code).Code.Commit.Hex);
        f.Close(sibling, "C ready.\n");
        var second = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), sibling.Execution.Launch.Attempt));
        Assert.Equal("e954b83b974db2a85981aa86b3a2eabee42d7803", Assert.IsType<CodeOutput.Produced>(second.Result.Code).Code.Commit.Hex);
        Assert.Equal(2, f.Read().Results.Count);
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(writer.Checkout, "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(sibling.Checkout, "c.txt")));
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("head")]
    [InlineData("result")]
    public async Task Late_ref_and_head_changes_block_as_uncertain_ownership_even_when_status_also_changes(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f);
        var reference = RunLayout.ResultRef(f.Read().RunKey!, f.Read().TaskKeys[T], ready.Execution.Launch.Attempt);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step != "git.result.after") return;
            if (mode == "head") Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "--detach", "HEAD").ExitCode);
            else f.Git.Git("update-ref", mode == "branch" ? ready.Execution.Location.Owner.Branch : reference, f.A.Hex);
        }).Publish(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task Unmerged_stages_are_preserved_and_block_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Git("checkout", "-q", "-b", "other");
        f.Git.Write("a.txt", "right\n");
        f.Git.Git("add", "a.txt");
        f.Git.Git("-c", "commit.gpgSign=false", "commit", "-q", "-m", "right");
        f.Git.Write("a.txt", "left\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "a.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "left").ExitCode);
        Assert.Equal(1, f.Git.Run(ready.Checkout, "merge", "--no-edit", "other").ExitCode);
        f.Close(ready);
        Assert.Equal("DirtyWorktree", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt))
            .Block.Problem.ToString());
        Assert.Equal("UU a.txt\0", System.Text.Encoding.UTF8.GetString(GitFixture.Read(f.Git.Open().Status(ready.Checkout))));
        Assert.Empty(f.Read().Results);
    }
}
