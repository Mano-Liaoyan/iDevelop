using System.Text;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class JoinTests
{
    private static Workflow Diamond(bool third = false)
    {
        var workflow = Connect(Connect(FixtureWorkflow(third ? [Writer(T), Writer(C), Writer(D), Writer(U)] : [Writer(T), Writer(C), Writer(U)]), T, U), C, U);
        return third ? Connect(workflow, D, U) : workflow;
    }

    private static Materializer Open(PreparationFixture f, Action<string>? probe = null) =>
        MergeJoins.Open(f.Git.Folder, f.Store, new QuiescentBoundary(), new Clock(), f.Git.Environment, probe);

    private static async Task<(OwnedCode B, OwnedCode C)> Sources(PreparationFixture f, string bPath = "b.txt", string bText = "B\n",
        string cPath = "c.txt", string cText = "C\n")
    {
        var b = Assert.IsType<Preparation.Ready>(await Open(f).Prepare(W, f.RunId, f.Op(), T, new AttemptCause.Initial()));
        var c = Assert.IsType<Preparation.Ready>(await Open(f).Prepare(W, f.RunId, f.Op(), C, new AttemptCause.Initial()));
        OwnCommit(f, b, bPath, bText, "b");
        OwnCommit(f, c, cPath, cText, "c");
        f.Close(b);
        var publishedB = Assert.IsType<Publication.Accepted>(Open(f).Publish(W, f.RunId, f.Op(), b.Execution.Launch.Attempt));
        f.Close(c);
        var publishedC = Assert.IsType<Publication.Accepted>(Open(f).Publish(W, f.RunId, f.Op(), c.Execution.Launch.Attempt));
        return (Assert.IsType<CodeOutput.Produced>(publishedB.Result.Code).Code, Assert.IsType<CodeOutput.Produced>(publishedC.Result.Code).Code);
    }

    private static void OwnCommit(PreparationFixture f, Preparation.Ready ready, string path, string text, string message)
    {
        f.Git.Write(path, text, ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "--all").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", message).ExitCode);
    }

    private static ValueTask<Preparation> Prepare(PreparationFixture f, OperationId operation, Action<string>? probe = null) =>
        Open(f, probe).Prepare(W, f.RunId, operation, U, new AttemptCause.Initial());

    [Fact]
    public async Task Overlapping_diamond_preserves_isolated_results_and_materializes_the_ordered_join()
    {
        using var f = new PreparationFixture(Diamond());
        var sources = await Sources(f);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", sources.B.AttemptBase.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", sources.C.AttemptBase.Hex);
        Assert.Equal("b350f18e8c7f922d58c54e415a95fb0a4b6fa249", sources.B.Commit.Hex);
        Assert.Equal("e954b83b974db2a85981aa86b3a2eabee42d7803", sources.C.Commit.Hex);
        var repository = f.Git.Open();
        Assert.Equal(new[] { "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67" }, GitFixture.Read(repository.ReadCommit(sources.B.Commit)).Parents.Select(parent => parent.Hex));
        Assert.Equal(new[] { "7025720b8121cfd45a182b2ead881a0c0461beb0" }, GitFixture.Read(repository.ReadCommit(sources.C.Commit)).Parents.Select(parent => parent.Hex));
        Assert.Equal(new[] { "a.txt", "b.txt", "plan.txt", "root.txt" }, GitFixture.Read(repository.TreeFiles(sources.B.Commit)));
        Assert.Equal(new[] { "a.txt", "c.txt", "plan.txt", "root.txt" }, GitFixture.Read(repository.TreeFiles(sources.C.Commit)));
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        AssertDiamond(f, ready);
        Assert.Equal(1, f.Read().Preparations.Values.Count(prepared => f.Read().Attempts[prepared.Launch.Attempt].Task == U));
    }

    private static void AssertDiamond(PreparationFixture f, Preparation.Ready ready)
    {
        Assert.Equal("a9e5b83f5ce058b92625a5187acce471cf4b3fad", ready.Execution.Location.AttemptBase.Hex);
        Assert.Equal("approved\n", File.ReadAllText(Path.Combine(ready.Checkout, "plan.txt")));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
        var repository = f.Git.Open();
        Assert.Equal(new[] { "b350f18e8c7f922d58c54e415a95fb0a4b6fa249", "e954b83b974db2a85981aa86b3a2eabee42d7803" },
            GitFixture.Read(repository.ReadCommit(ready.Execution.Location.AttemptBase)).Parents.Select(parent => parent.Hex));
        Assert.Equal("a9e5b83f5ce058b92625a5187acce471cf4b3fad", GitFixture.Read(repository.ReadRef("refs/heads/idp/93f23689/join/c67f2fc3"))?.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(repository.ReadRef("refs/heads/main"))?.Hex);
    }

    [Fact]
    public async Task Separate_line_edits_merge_both_changes()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: git =>
        {
            git.Write("settings.txt", "0\n1\n2\n3\n4\n5\n6\n7\n8\n");
            return git.Commit("settings");
        });
        await Sources(f, "settings.txt", "b=1\n1\n2\n3\n4\n5\n6\n7\n8\n", "settings.txt", "0\n1\n2\n3\n4\n5\n6\n7\nc=1\n");
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        Assert.Equal("b=1\n1\n2\n3\n4\n5\n6\n7\nc=1\n", File.ReadAllText(Path.Combine(ready.Checkout, "settings.txt")));
    }

    [Fact]
    public async Task Fan_in_conflict_preserves_sources_and_raw_messages_and_deduplicates_the_block()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        var sources = await Sources(f, "settings.txt", "b=1\n", "settings.txt", "c=1\n");
        var operation = f.Op();
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, operation));
        AssertConflict(f, blocked, 2);
        Assert.Equal(new[] { "6b63dd7b21aab7adbbc8688a874c118a339d505f", "f9f8010263a20ded9d4f632293fcb68a8b5a6beb" }, blocked.Block.Conflict!.Sources.Select(source => source.Commit.Hex));
        Assert.Equal("6b63dd7b21aab7adbbc8688a874c118a339d505f", sources.B.Commit.Hex);
        Assert.Equal("f9f8010263a20ded9d4f632293fcb68a8b5a6beb", sources.C.Commit.Hex);
        var repository = f.Git.Open();
        Assert.Equal("6b63dd7b21aab7adbbc8688a874c118a339d505f", GitFixture.Read(repository.ReadRef("refs/heads/idp/93f23689/task/90d5b0a2"))?.Hex);
        Assert.Equal("f9f8010263a20ded9d4f632293fcb68a8b5a6beb", GitFixture.Read(repository.ReadRef("refs/heads/idp/93f23689/task/ca55ceea"))?.Hex);
        Assert.Equal("6b63dd7b21aab7adbbc8688a874c118a339d505f", GitFixture.Read(repository.ReadRef(sources.B.ResultRef))?.Hex);
        Assert.Equal("f9f8010263a20ded9d4f632293fcb68a8b5a6beb", GitFixture.Read(repository.ReadRef(sources.C.ResultRef))?.Hex);
        Assert.Equal(RunJournal.Canonical(blocked.Block), RunJournal.Canonical(Assert.IsType<Preparation.Blocked>(await Prepare(f, operation)).Block));
        Assert.Equal(1, f.Read().Blocks.Values.Count(block => block.Block.Task == U));
    }

    private static CommitId Settings(GitFixture git)
    {
        git.Write("settings.txt", "0\n");
        return git.Commit("settings");
    }

    [Fact]
    public async Task Late_conflict_replay_preserves_the_block_when_the_clock_advances()
    {
        using var f = new PreparationFixture(Diamond(true));
        await Sources(f);
        var third = Assert.IsType<Preparation.Ready>(await Open(f).Prepare(W, f.RunId, f.Op(), D, new AttemptCause.Initial()));
        OwnCommit(f, third, "b.txt", "different\n", "e");
        f.Close(third);
        Assert.IsType<Publication.Accepted>(Open(f).Publish(W, f.RunId, f.Op(), third.Execution.Launch.Attempt));
        var operation = f.Op();
        var first = Assert.IsType<Preparation.Blocked>(await Prepare(f, operation));
        Assert.Equal("FanInConflict", first.Block.Problem.ToString());
        Assert.Equal(2, first.Block.Conflict!.Step);
        Assert.Equal(new[] { "b.txt" }, first.Block.Conflict.Paths);
        Assert.Equal(3, first.Block.Conflict.Sources.Length);
        var later = MergeJoins.Open(f.Git.Folder, f.Store, new QuiescentBoundary(), new LaterClock(), f.Git.Environment);
        var second = Assert.IsType<Preparation.Blocked>(await later.Prepare(W, f.RunId, operation, U, new AttemptCause.Initial()));
        Assert.Equal("FanInConflict", second.Block.Problem.ToString());
        Assert.Equal(RunJournal.Canonical(first.Block), RunJournal.Canonical(second.Block));
        Assert.Equal(1, f.Read().Blocks.Values.Count(block => block.Block.Task == U));
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/join/c67f2fc3")));
    }

    private sealed class LaterClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => At.AddMinutes(1); }

    private static void AssertConflict(PreparationFixture f, Preparation.Blocked blocked, int sources)
    {
        Assert.Equal("FanInConflict", blocked.Block.Problem.ToString());
        Assert.Equal(1, blocked.Block.Conflict!.Step);
        Assert.Equal(new[] { "settings.txt" }, blocked.Block.Conflict.Paths);
        if (sources == 3)
        {
            Assert.Equal(new[] { "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000004", "00000000-0000-0000-0000-000000000005" }, blocked.Block.Conflict.Sources.Select(source => source.Task.ToString()));
        }
        else
        {
            Assert.Equal(new[] { "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000004" }, blocked.Block.Conflict.Sources.Select(source => source.Task.ToString()));
        }
        var message = Assert.Single(blocked.Block.Conflict.Messages, message => message.Type == "CONFLICT (contents)");
        Assert.Equal("CONFLICT (content): Merge conflict in settings.txt\n", message.Text);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var evidence = blocked.Block.Conflict.Stdout;
        Assert.Contains("CONFLICT (content): Merge conflict in settings.txt\n", Encoding.UTF8.GetString(RunStorage.Read(storage.Folder, evidence.RelativePath, evidence.Content, evidence.ByteLength)));
        Assert.Equal(0, f.Read().Preparations.Values.Count(prepared => f.Read().Attempts[prepared.Launch.Attempt].Task == U));
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/join/c67f2fc3")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_inputs_keep_original_parents_and_record_every_source_on_early_conflict(bool conflict)
    {
        using var f = new PreparationFixture(Diamond(true), configureBase: conflict ? Settings : null);
        if (conflict) await Sources(f, "settings.txt", "b=1\n", "settings.txt", "c=1\n");
        else await Sources(f);
        var third = Assert.IsType<Preparation.Ready>(await Open(f).Prepare(W, f.RunId, f.Op(), D, new AttemptCause.Initial()));
        OwnCommit(f, third, "e.txt", "E\n", "e");
        f.Close(third);
        var thirdResult = Assert.IsType<Publication.Accepted>(Open(f).Publish(W, f.RunId, f.Op(), third.Execution.Launch.Attempt));
        if (conflict) Assert.Equal("560f86f9c272840d2b9c584f26a76e246dabbef4", Assert.IsType<CodeOutput.Produced>(thirdResult.Result.Code).Code.Commit.Hex);
        var outcome = await Prepare(f, f.Op());
        if (conflict)
        {
            AssertConflict(f, Assert.IsType<Preparation.Blocked>(outcome), 3);
        }
        else
        {
            var ready = Assert.IsType<Preparation.Ready>(outcome);
            Assert.Equal("3cbffb776a80266eba7a89046eab81561047b47c", ready.Execution.Location.AttemptBase.Hex);
            Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
            Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
            Assert.Equal("E\n", File.ReadAllText(Path.Combine(ready.Checkout, "e.txt")));
            Assert.Equal(new[] { "b350f18e8c7f922d58c54e415a95fb0a4b6fa249", "e954b83b974db2a85981aa86b3a2eabee42d7803", "9178ad6566dbaa87bd07d44f4ff2a6115b5756e8" },
                GitFixture.Read(f.Git.Open().ReadCommit(ready.Execution.Location.AttemptBase)).Parents.Select(parent => parent.Hex));
        }
    }

    [Fact]
    public async Task Adoption_rejects_correct_parents_with_a_tree_missing_an_input()
    {
        using var f = new PreparationFixture(Diamond());
        var sources = await Sources(f);
        var operation = f.Op();
        await Assert.ThrowsAsync<PublicationTests.Crash>(async () => await Prepare(f, operation, point =>
        {
            if (point == "journal.plan.after") throw new PublicationTests.Crash();
        }));
        var preparation = (MaterializationPlan.Preparation)f.Read().Plans[OperationIds.Derive(operation, "plan")];
        var repository = f.Git.Open();
        var recipe = new CommitRecipe(sources.B.Tree, [sources.B.Commit, sources.C.Commit], "incomplete\n", "E2 <e2@example.test>", "E2 <e2@example.test>", At);
        var commit = GitFixture.Read(repository.CreateCommit(recipe));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(W, f.RunId, OperationIds.Derive(operation, "join"), new RunEvent.Planned(
            new MaterializationPlan.Join(U, preparation.Inputs, preparation.Sources, recipe, commit, null, "refs/heads/idp/93f23689/join/c67f2fc3"))));
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, operation));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Equal("The recorded join is not the clean merge of its sources.", blocked.Block.Detail);
        Assert.Null(GitFixture.Read(repository.ReadRef("refs/heads/idp/93f23689/join/c67f2fc3")));
    }

    [Fact]
    public async Task Foreign_join_ref_blocks_before_a_join_plan_is_journaled()
    {
        using var f = new PreparationFixture(Diamond());
        await Sources(f);
        f.Git.Git("update-ref", "refs/heads/idp/93f23689/join/c67f2fc3", "b350f18e8c7f922d58c54e415a95fb0a4b6fa249");
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, f.Op()));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("b350f18e8c7f922d58c54e415a95fb0a4b6fa249", GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/join/c67f2fc3"))?.Hex);
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Join>());
    }

    [Fact]
    public async Task Join_probes_separated_only_by_reads_share_durable_state_and_converge_to_one_publication_and_an_equal_preparation()
    {
        var points = new List<string>();
        using (var baseline = new PreparationFixture(Diamond()))
        {
            await Sources(baseline);
            var ready = Assert.IsType<Preparation.Ready>(await Prepare(baseline, baseline.Op(), points.Add));
            AssertDiamond(baseline, ready);
        }
        Assert.Equal(new[] { "git.join-merge-1.before", "git.join-merge-1.after", "git.join-commit.before", "git.join-commit.after",
            "journal.join-plan.before", "journal.join-plan.after", "journal.join-intent.before", "journal.join-intent.after", "git.join.before", "git.join.after",
            "journal.join-observed.before", "journal.join-observed.after" }, points.Where(point => point.Contains("join", StringComparison.Ordinal)));
        foreach (var point in points.Where(point => point.Contains("join", StringComparison.Ordinal))
            .Where(point => point == "git.join-merge-1.before" || point.EndsWith(".after", StringComparison.Ordinal)))
        {
            using var f = new PreparationFixture(Diamond());
            await Sources(f);
            var operation = f.Op();
            await Assert.ThrowsAsync<PublicationTests.Crash>(async () => await Prepare(f, operation, step =>
            {
                if (step == point) throw new PublicationTests.Crash();
            }));
            var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, operation));
            AssertDiamond(f, ready);
            var record = f.Read();
            var intent = Assert.Single(record.GitIntents, pair => pair.Value.Plan == OperationIds.Derive(operation, "join"));
            Assert.Equal("a9e5b83f5ce058b92625a5187acce471cf4b3fad", record.GitObservations[intent.Key].Value);
            Assert.Equal(1, record.Receipts.Values.Count(entry => entry.Event is RunEvent.GitObserved observed && observed.Mutation == intent.Key));
            Assert.Single(record.Plans.Values.OfType<MaterializationPlan.Join>());
            Assert.Equal(ready, await Prepare(f, operation));
        }
    }

    [Fact]
    public async Task Supersession_after_composition_rejects_stale_inputs_and_retains_the_old_result()
    {
        using var f = new PreparationFixture(Diamond());
        var sources = await Sources(f);
        var old = f.Read().CurrentResults[T];
        var second = Assert.IsType<Preparation.Ready>(await Open(f).Prepare(W, f.RunId, f.Op(), T, new AttemptCause.Continue(old.Origin is ResultOrigin.Executed executed ? executed.Attempt : throw new InvalidOperationException(), f.Op())));
        f.Git.Write("b.txt", "B again\n", second.Checkout);
        f.Close(second, "B again.\n");
        var publish = f.Op();
        Assert.Throws<PublicationTests.Crash>(() => Open(f, point =>
        {
            if (point == "journal.accepted.before") throw new PublicationTests.Crash();
        }).Publish(W, f.RunId, publish, second.Execution.Launch.Attempt));
        var composer = new SupersedingComposer(new MergeJoins(f.Git.Folder, f.Store, f.Git.Environment), f, publish);
        var outcome = await f.Materializer(composer).Prepare(W, f.RunId, f.Op(), U, new AttemptCause.Initial());
        Assert.Equal("StaleInput", Assert.IsType<Preparation.Rejected>(outcome).Reason.Problem.ToString());
        Assert.Equal("b350f18e8c7f922d58c54e415a95fb0a4b6fa249", GitFixture.Read(f.Git.Open().ReadRef(sources.B.ResultRef))?.Hex);
        Assert.Equal("B\n", f.Git.Git("show", "b350f18e8c7f922d58c54e415a95fb0a4b6fa249:b.txt"));
        Assert.Equal("B ready.\n", f.Read().Results.Single(result => result.Id == old.Id).Report);
        Assert.Equal(0, f.Read().Preparations.Values.Count(prepared => f.Read().Attempts[prepared.Launch.Attempt].Task == U));
    }

    private sealed class SupersedingComposer(IJoinComposer inner, PreparationFixture f, OperationId publication) : IJoinComposer
    {
        public async ValueTask<JoinOutcome> Compose(JoinRequest request, CancellationToken cancellation)
        {
            var outcome = await inner.Compose(request, cancellation);
            Assert.IsType<JoinOutcome.Ready>(outcome);
            Assert.IsType<RunDecision.Created>(f.Store.AcceptPublication(W, f.RunId, OperationIds.Derive(publication, "accepted"), OperationIds.Derive(publication, "plan")));
            return outcome;
        }
    }
}
