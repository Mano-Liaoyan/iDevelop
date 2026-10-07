using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class PublicationOwnershipTests
{
    [Theory]
    [InlineData("replace")]
    [InlineData("grafts")]
    public async Task Grafted_writer_history_without_the_attempt_base_blocks_publication(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal(0, f.Git.Run(writer.Checkout, "checkout", "-q", "--detach", "81ddb7c330112c7f16700ed002803a04b0bce693").ExitCode);
        CommitFile(f, writer.Checkout, "b.txt", "B\n");
        const string tip = "40abc7bebc8957e22d11b4c6b9603180652d95f1";
        Assert.Equal(tip, Head(f, writer.Checkout));
        if (mode == "replace") Assert.Equal(0, f.Git.Run(writer.Checkout, "replace", "--graft", tip, A).ExitCode);
        else File.WriteAllText(Path.Combine(f.Git.Open().CommonDirectory, "info", "grafts"), tip + " " + A + "\n");
        Assert.Equal(0, f.Git.Run(writer.Checkout, "symbolic-ref", "HEAD", writer.Execution.Location.Owner.Branch).ExitCode);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "-q", "--hard", tip).ExitCode);
        f.Close(writer);
        var operation = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000102: unexplained shared ref refs/heads/idp/93f23689/task/90d5b0a2, adfe40b30c176fb407933286f51d15ea9b54cdc3 to 40abc7bebc8957e22d11b4c6b9603180652d95f1.", blocked.Block.Detail);
        Assert.Equal("40abc7bebc8957e22d11b4c6b9603180652d95f1", Ref(f, writer.Execution.Location.Owner.Branch));
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(writer.Checkout, "b.txt")));
        Assert.Empty(f.Read().Results);
        var again = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        Assert.Equal(blocked.Block.Detail, again.Block.Detail);
        Assert.Equal("40abc7bebc8957e22d11b4c6b9603180652d95f1", Ref(f, writer.Execution.Location.Owner.Branch));
    }

    [Fact]
    public async Task Persisted_publication_plan_without_the_attempt_base_blocks_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        const string below = "81ddb7c330112c7f16700ed002803a04b0bce693";
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "-q", "--hard", below).ExitCode);
        f.Git.Write("b.txt", "B\n", writer.Checkout);
        Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        f.Close(writer);
        var repository = f.Git.Open();
        var capture = GitFixture.Read(repository.Capture(writer.Checkout));
        var recipe = new CommitRecipe(capture.Tree, [new CommitId(below)], "B\n", "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", At);
        var commit = GitFixture.Read(repository.CreateCommit(recipe));
        var plan = new MaterializationPlan.Publication(writer.Execution.Launch.Attempt, new ResultId(Guid.Parse("00000000-0000-0000-0000-00000000abcd")),
            null, new CommitId(below), capture.IndexBefore, recipe, commit, "B ready.\n", []);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(W, f.RunId, f.Op(), new RunEvent.Planned(plan)));
        var operation = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The writer branch tip 81ddb7c330112c7f16700ed002803a04b0bce693 does not contain the attempt base adfe40b30c176fb407933286f51d15ea9b54cdc3.", blocked.Block.Detail);
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", Ref(f, writer.Execution.Location.Owner.Branch));
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task Salvage_adopted_rewind_blocks_publication_without_losing_retained_work_and_retry_converges()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("b.txt", "B\n", writer.Checkout);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "b").ExitCode);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", Head(f, writer.Checkout));
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "--hard", "81ddb7c330112c7f16700ed002803a04b0bce693").ExitCode);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "checkout", "--detach", "-q", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67").ExitCode);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal(0, f.Git.Run(writer.Checkout, "symbolic-ref", "HEAD", writer.Execution.Location.Owner.Branch).ExitCode);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "--hard", "HEAD").ExitCode);
        f.Close(writer);
        var operation = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The writer branch tip 81ddb7c330112c7f16700ed002803a04b0bce693 does not contain the attempt base adfe40b30c176fb407933286f51d15ea9b54cdc3.", blocked.Block.Detail);
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", Ref(f, writer.Execution.Location.Owner.Branch));
        Assert.Equal(retained.Commit.Hex + "\n", f.Git.Git("for-each-ref", "--contains", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", "--format=%(objectname)", retained.Receipt.Ref));
        Assert.Equal("B\n", f.Git.Git("show", retained.Commit.Hex + ":b.txt"));
        Assert.Null(Ref(f, RunLayout.ResultRef(f.Read().RunKey!, f.Read().TaskKeys[T], writer.Execution.Launch.Attempt)));
        Assert.Equal(blocked, f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        var salvageOperation = f.Op();
        var recovered = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, salvageOperation, writer.Execution.Launch.Attempt));
        Assert.Equal(recovered, f.Materializer().Salvage(W, f.RunId, salvageOperation, writer.Execution.Launch.Attempt));
        var resetOperation = f.Op();
        var confirmation = f.Op();
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(W, f.RunId, resetOperation, recovered.Receipt.Plan, confirmation));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", reset.Target.Hex);
        Assert.Equal(reset, f.Materializer().ResetForRetry(W, f.RunId, resetOperation, recovered.Receipt.Plan, confirmation));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(writer.Checkout, "a.txt")));
        Assert.Equal("B\n", f.Git.Git("show", retained.Commit.Hex + ":b.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Salvaged_descendant_publishes_with_the_literal_writer_parent_including_plan_resume(bool resume)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("b.txt", "B\n", writer.Checkout);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "b").ExitCode);
        Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        f.Close(writer);
        var operation = f.Op();
        if (resume) Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new Crash();
        }).Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
        var code = Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code;
        Assert.Equal(new[] { "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" }, GitFixture.Read(f.Git.Open().ReadCommit(code.Commit)).Parents.Select(parent => parent.Hex));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", code.AttemptBase.Hex);
        Assert.Equal("B\n", f.Git.Git("show", code.Commit.Hex + ":b.txt"));
        Assert.Equal(accepted, f.Materializer().Publish(W, f.RunId, operation, writer.Execution.Launch.Attempt));
    }

    [Fact]
    public async Task Salvage_cannot_explain_a_published_branch_rewind_for_a_sibling_or_retry()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(C)));
        var first = await PublicationTests.ChangedWriter(f);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), first.Execution.Launch.Attempt));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        CommitFile(f, sibling.Checkout, "c.txt", "C\n");
        f.Git.Write("a.txt", "A from C\n", sibling.Checkout);
        f.Close(sibling, "C ready.\n");
        Assert.Equal(0, f.Git.Run(first.Checkout, "reset", "-q", "--hard", A).ExitCode);
        var operation = f.Op();
        var before = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, sibling.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", before.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000104: unexplained shared ref refs/heads/idp/93f23689/task/90d5b0a2, 81cae59086bf9597301026b65f1bb57380b74686 to adfe40b30c176fb407933286f51d15ea9b54cdc3.", before.Block.Detail);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(W, f.RunId, f.Op(), first.Execution.Launch.Attempt));
        var after = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, sibling.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", after.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000104: unexplained shared ref refs/heads/idp/93f23689/task/90d5b0a2, 81cae59086bf9597301026b65f1bb57380b74686 to adfe40b30c176fb407933286f51d15ea9b54cdc3.", after.Block.Detail);
        Assert.Equal(RunJournal.Canonical(after.Block), RunJournal.Canonical(Assert.IsType<Publication.Blocked>(
            f.Materializer().Publish(W, f.RunId, operation, sibling.Execution.Launch.Attempt)).Block));
        var retry = Assert.IsType<RetryReset.Blocked>(f.Materializer().ResetForRetry(W, f.RunId, f.Op(), retained.Receipt.Plan, f.Op()));
        Assert.Equal("UncertainOwnership", retry.Block.Problem.ToString());
        Assert.Equal("The retry branch moved outside the recorded reset.", retry.Block.Detail);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Ref(f, first.Execution.Location.Owner.Branch));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Ref(f, Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.ResultRef));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(first.Checkout, "a.txt")));
        Assert.Equal("A captured\n", f.Git.Git("show", "81cae59086bf9597301026b65f1bb57380b74686:a.txt"));
    }

    [Fact]
    public async Task A_symbolic_shared_run_ref_blocks_a_sibling_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(C)));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        var writer = await PublicationTests.ChangedWriter(f);
        f.Git.Git("branch", "foreign", A);
        f.Git.Git("symbolic-ref", sibling.Execution.Location.Owner.Branch, "refs/heads/foreign");
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Ref refs/heads/idp/93f23689/task/ca55ceea is symbolic to refs/heads/foreign.", blocked.Block.Detail);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Ref(f, "refs/heads/foreign"));
        Assert.Equal("refs/heads/foreign\n", f.Git.Git("symbolic-ref", sibling.Execution.Location.Owner.Branch));
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(writer.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Observed_base_retention_is_rechecked_before_preparing_another_task()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Git("update-ref", "refs/idp/93f23689/base", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(U));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Publication ref refs/idp/93f23689/base has unexpected value 7c64b20d5be53b5c1a291863ef191aa28f6f4d51.", blocked.Block.Detail);
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

    private const string A = "adfe40b30c176fb407933286f51d15ea9b54cdc3";
    private static string? Ref(PreparationFixture f, string name) => GitFixture.Read(f.Git.Open().ReadRef(name))?.Hex;
    private static string Head(PreparationFixture f, string checkout) => f.Git.Run(checkout, "rev-parse", "HEAD").Text.Trim();
    private static bool Reachable(PreparationFixture f, string commit) => f.Git.Git("for-each-ref", "--contains", commit, "--format=%(refname)").Trim().Length != 0;
    private static void CommitFile(PreparationFixture f, string checkout, string file, string text)
    {
        f.Git.Write(file, text, checkout);
        Assert.Equal(0, f.Git.Run(checkout, "add", file).ExitCode);
        Assert.Equal(0, f.Git.Run(checkout, "-c", "commit.gpgSign=false", "commit", "-qm", file).ExitCode);
    }
    private static AttemptId AttemptOf(ResultRecord result) => Assert.IsType<ResultOrigin.Executed>(result.Origin).Attempt;

    [Fact]
    public async Task A_published_sibling_rewound_to_its_creation_start_blocks_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        CommitFile(f, sibling.Checkout, "u.txt", "U\n");
        f.Close(sibling);
        var published = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), sibling.Execution.Launch.Attempt));
        var writer = await PublicationTests.ChangedWriter(f);
        f.Git.Git("update-ref", sibling.Execution.Location.Owner.Branch, A);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000104: unexplained shared ref refs/heads/idp/93f23689/task/c67f2fc3, 70d86755135c4bf457f1b7d4d32f5ffc137996d8 to adfe40b30c176fb407933286f51d15ea9b54cdc3.", blocked.Block.Detail);
        Assert.Equal(A, Ref(f, sibling.Execution.Location.Owner.Branch));
        Assert.Single(f.Read().Results);
        Assert.True(Reachable(f, Assert.IsType<CodeOutput.Produced>(published.Result.Code).Code.Commit.Hex));
    }

    [Fact]
    public async Task A_closed_readers_branch_moved_forward_blocks_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Task(U)));
        var reader = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        f.Close(reader, "Read.\n");
        var writer = await PublicationTests.ChangedWriter(f);
        CommitFile(f, reader.Checkout, "c.txt", "C\n");
        var moved = Head(f, reader.Checkout);
        Assert.Equal("ce4934392b45ba0126622eb19131acce29bd8705", moved);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000104: unexplained shared ref refs/heads/idp/93f23689/task/c67f2fc3, adfe40b30c176fb407933286f51d15ea9b54cdc3 to ce4934392b45ba0126622eb19131acce29bd8705.", blocked.Block.Detail);
        Assert.Equal("ce4934392b45ba0126622eb19131acce29bd8705", Ref(f, reader.Execution.Location.Owner.Branch));
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task A_published_writers_branch_moved_forward_blocks_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        CommitFile(f, sibling.Checkout, "u.txt", "U\n");
        f.Close(sibling);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), sibling.Execution.Launch.Attempt));
        var writer = await PublicationTests.ChangedWriter(f);
        CommitFile(f, sibling.Checkout, "late.txt", "late\n");
        var moved = Head(f, sibling.Checkout);
        Assert.Equal("1731d2d20abe6875ad433e4a336ab668a66bdce7", moved);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000104: unexplained shared ref refs/heads/idp/93f23689/task/c67f2fc3, 70d86755135c4bf457f1b7d4d32f5ffc137996d8 to 1731d2d20abe6875ad433e4a336ab668a66bdce7.", blocked.Block.Detail);
        Assert.Equal("1731d2d20abe6875ad433e4a336ab668a66bdce7", Ref(f, sibling.Execution.Location.Owner.Branch));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task A_sibling_rewound_to_its_earlier_published_result_blocks_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var first = await f.Publish(U, f.A);
        var again = Assert.IsType<Preparation.Ready>(await f.Prepare(U, cause: new AttemptCause.Retry(AttemptOf(first), f.Op())));
        CommitFile(f, again.Checkout, "u.txt", "U\n");
        f.Close(again);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), again.Execution.Launch.Attempt));
        var writer = await PublicationTests.ChangedWriter(f);
        var firstCommit = Assert.IsType<CodeOutput.Produced>(first.Code).Code.Commit.Hex;
        Assert.Equal("366602c243fddab716b8f02fc03fdacaf56e6058", firstCommit);
        f.Git.Git("update-ref", again.Execution.Location.Owner.Branch, firstCommit);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000106: unexplained shared ref refs/heads/idp/93f23689/task/c67f2fc3, 5f04054075cc6998be6254c2ee7c4d2f8cbba682 to 366602c243fddab716b8f02fc03fdacaf56e6058.", blocked.Block.Detail);
        Assert.Equal(2, f.Read().Results.Count);
    }

    [Fact]
    public async Task A_sibling_that_published_and_was_rewound_to_the_publishers_snapshot_value_blocks_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var writer = await PublicationTests.ChangedWriter(f);
        CommitFile(f, sibling.Checkout, "u.txt", "U\n");
        f.Close(sibling);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), sibling.Execution.Launch.Attempt));
        f.Git.Git("update-ref", sibling.Execution.Location.Owner.Branch, A);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000104: unexplained shared ref refs/heads/idp/93f23689/task/c67f2fc3, adfe40b30c176fb407933286f51d15ea9b54cdc3 to adfe40b30c176fb407933286f51d15ea9b54cdc3.", blocked.Block.Detail);
        Assert.Equal(A, Ref(f, sibling.Execution.Location.Owner.Branch));
        Assert.Single(f.Read().Results);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("pending-then-moved")]
    public async Task A_sibling_creation_interrupted_after_git_is_judged_by_its_pending_intent(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Task(U)));
        var writer = await PublicationTests.ChangedWriter(f);
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == "git.create-worktree.after") throw new Crash(); })
            .Prepare(W, f.RunId, f.Op(), U, new AttemptCause.Initial()));
        var branch = "refs/heads/idp/93f23689/task/c67f2fc3";
        Assert.Equal(A, Ref(f, branch));
        if (mode == "pending-then-moved")
            CommitFile(f, Path.Combine(f.Git.Folder, ".worktrees", "93f23689", "c67f2fc3"), "c.txt", "C\n");
        var outcome = f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt);
        if (mode == "pending")
        {
            var accepted = Assert.IsType<Publication.Accepted>(outcome);
            Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex);
            Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Ref(f, branch));
        }
        else
        {
            var blocked = Assert.IsType<Publication.Blocked>(outcome);
            Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
            Assert.Equal("Attempt 00000000-0000-0000-0000-000000000102: unexplained shared ref refs/heads/idp/93f23689/task/c67f2fc3, absent to ce4934392b45ba0126622eb19131acce29bd8705.", blocked.Block.Detail);
            Assert.Equal("ce4934392b45ba0126622eb19131acce29bd8705", Ref(f, branch));
        }
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(writer.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Historical_creation_cannot_authorize_recreating_a_rewound_branch()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        await f.Publish(T, f.A);
        var owner = f.Read().Preparations[new(A1, 1)].Location.Owner;
        var checkout = Path.Combine(f.Git.Folder, owner.RelativePath);
        f.Git.Git("worktree", "unlock", checkout);
        f.Git.Git("worktree", "remove", "--force", checkout);
        f.Git.Git("update-ref", owner.Branch, "adfe40b30c176fb407933286f51d15ea9b54cdc3");
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(T, cause: new AttemptCause.Retry(A1, f.Op())));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The branch or occupied checkout is not a matching creation intent.", blocked.Block.Detail);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Ref(f, owner.Branch));
        Assert.Equal("d4d26ecdf72779dbc9c5c983025fb51546c8f9ea", Ref(f, "refs/idp/93f23689/result/90d5b0a2/00000000-0000-0000-0000-000000000102"));
        Assert.False(Directory.Exists(checkout));
    }

    [Fact]
    public async Task A_ref_deleted_before_the_snapshot_is_still_checked_against_the_journal()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        await f.Publish(U, f.A);
        const string reference = "refs/idp/93f23689/result/c67f2fc3/00000000-0000-0000-0000-000000000102";
        f.Git.Git("update-ref", "-d", reference);
        var writer = await PublicationTests.ChangedWriter(f);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Attempt 00000000-0000-0000-0000-000000000104: unexplained shared ref refs/idp/93f23689/result/c67f2fc3/00000000-0000-0000-0000-000000000102, absent to absent.", blocked.Block.Detail);
        Assert.Equal(2, blocked.Block.Evidence.Length);
        Assert.Equal("366602c243fddab716b8f02fc03fdacaf56e6058", Ref(f, "refs/heads/idp/93f23689/task/c67f2fc3"));
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(writer.Checkout, "a.txt")));
        Assert.Single(f.Read().Results);
    }
}
