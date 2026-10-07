using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class JoinBoundaryTests
{
    private static Workflow Diamond() => Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U)), T, U), C, U);

    private static async Task Sources(PreparationFixture f)
    {
        f.Git.Git("checkout", "-q", "-b", "b", f.A.Hex);
        f.Git.Write("b.txt", "B\n");
        f.Git.Git("add", "b.txt");
        f.Git.Git("-c", "commit.gpgSign=false", "commit", "-q", "-m", "b");
        var b = new CommitId(f.Git.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", b.Hex);
        f.Git.Git("checkout", "-q", "-b", "c", f.A.Hex);
        f.Git.Write("c.txt", "C\n");
        f.Git.Git("add", "c.txt");
        f.Git.Git("-c", "commit.gpgSign=false", "commit", "-q", "-m", "c");
        var c = new CommitId(f.Git.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("7025720b8121cfd45a182b2ead881a0c0461beb0", c.Hex);
        f.Git.Git("checkout", "-q", "main");
        await f.Publish(T, b);
        await f.Publish(C, c);
    }

    [Fact]
    public async Task Unavailable_joins_preserve_sources_and_record_a_join_required_block()
    {
        using var f = new PreparationFixture(Diamond());
        await Sources(f);
        var operation = f.Op();
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(U, operation));
        Assert.Equal("JoinRequired", blocked.Block.Problem.ToString());
        Assert.Equal(operation, blocked.Block.Operation);
        Assert.Equal(U, blocked.Block.Task);
        Assert.Single(f.Read().Blocks);
        Assert.Equal(0, f.Read().Preparations.Values.Count(prepared => f.Read().Attempts[prepared.Launch.Attempt].Task == U));
        Assert.Equal("B\n", f.Git.Git("show", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67:b.txt"));
        Assert.Equal("C\n", f.Git.Git("show", "7025720b8121cfd45a182b2ead881a0c0461beb0:c.txt"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("tree")]
    [InlineData("parents")]
    [InlineData("receipt")]
    [InlineData("ref")]
    public async Task Ready_join_requires_matching_plan_observation_object_tree_parents_and_ref(string mode)
    {
        using var f = new PreparationFixture(Diamond());
        await Sources(f);
        var composer = new Composer(f, mode);
        var outcome = await f.Materializer(composer).Prepare(W, f.RunId, f.Op(), U, new AttemptCause.Initial());
        if (mode == "valid")
        {
            var ready = Assert.IsType<Preparation.Ready>(outcome);
            Assert.Equal("49bd30d4f9a2fb9f90d3dd0cf443001d9fa4a71b", f.Read().Inputs[ready.Execution.Inputs].CodeBase.Hex);
            Assert.Equal("49bd30d4f9a2fb9f90d3dd0cf443001d9fa4a71b", ready.Execution.Location.AttemptBase.Hex);
            Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
            Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
            Assert.Equal(new[] { "b350f18e8c7f922d58c54e415a95fb0a4b6fa249", "e954b83b974db2a85981aa86b3a2eabee42d7803" },
                GitFixture.Read(f.Git.Open().ReadCommit(f.Read().Inputs[ready.Execution.Inputs].CodeBase)).Parents.Select(parent => parent.Hex));
        }
        else
        {
            Assert.Equal("InputUnavailable", Assert.IsType<Preparation.Blocked>(outcome).Block.Problem.ToString());
            Assert.Equal(0, f.Read().Preparations.Values.Count(prepared => f.Read().Attempts[prepared.Launch.Attempt].Task == U));
            Assert.Equal("A\n", File.ReadAllText(Path.Combine(f.Git.Folder, "a.txt")));
        }
    }

    private sealed class Composer(PreparationFixture f, string mode) : IJoinComposer
    {
        public ValueTask<JoinOutcome> Compose(JoinRequest request, CancellationToken cancellation)
        {
            var repository = f.Git.Open();
            var mergedTree = new TreeId(f.Git.Git("merge-tree", "--write-tree", "b350f18e8c7f922d58c54e415a95fb0a4b6fa249",
                "e954b83b974db2a85981aa86b3a2eabee42d7803").Trim());
            var parents = request.Sources.Select(source => source.Commit).Distinct().ToArray();
            var recipe = new CommitRecipe(mergedTree, [.. parents], "join\n", "E2 <e2@example.test>", "E2 <e2@example.test>", At);
            var actual = mode == "parents" ? recipe with { Parents = [.. parents.Reverse()] } : recipe;
            if (mode == "tree") actual = recipe with { Tree = GitFixture.Read(repository.ReadCommit(f.A)).Tree };
            var commit = GitFixture.Read(repository.CreateCommit(actual));
            var record = f.Read();
            var reference = $"refs/heads/idp/{record.RunKey}/join/{record.TaskKeys[U]}";
            var plan = new MaterializationPlan.Join(U, request.Inputs, request.Sources, recipe, commit, request.ExpectedJoin, reference);
            if (mode != "receipt")
            {
                Assert.IsType<RunDecision.Recorded>(f.Store.Record(W, f.RunId, request.Operation, new RunEvent.Planned(plan)));
                f.Observe(request.Operation, new(reference, request.ExpectedJoin, commit));
                if (mode == "ref") f.Git.Git("update-ref", reference, f.A.Hex);
            }
            return ValueTask.FromResult<JoinOutcome>(new JoinOutcome.Ready(new(request.Operation, request.Sources, commit, mergedTree, reference)));
        }
    }
}
