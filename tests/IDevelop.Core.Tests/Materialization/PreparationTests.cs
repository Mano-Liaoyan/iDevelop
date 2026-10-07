using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class PreparationTests
{
    private static Workflow Linear(bool context = false)
    {
        var workflow = Connect(FixtureWorkflow(Writer(T), Writer(U), Writer(C)), T, U);
        return context ? Connect(workflow, C, U, ConnectionKind.Context) : workflow;
    }

    [Fact]
    public async Task Linear_input_uses_accepted_commit_and_delivers_exact_reports_artifacts_and_prompt()
    {
        using var f = new PreparationFixture(Linear(), new("81ddb7c330112c7f16700ed002803a04b0bce693"));
        await f.Publish(T, f.A, artifact: true);
        f.Git.Git("update-ref", f.Read().Preparations[new(A1, 1)].Location.Owner.Branch, "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        var operation = f.Op();
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U, operation));
        Assert.Equal("d4d26ecdf72779dbc9c5c983025fb51546c8f9ea", ready.Execution.Location.AttemptBase.Hex);
        Assert.Equal("d4d26ecdf72779dbc9c5c983025fb51546c8f9ea\n", f.Git.Run(ready.Checkout, "rev-parse", "HEAD").Text);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var report = ".idp/inputs/00000000-0000-0000-0000-000000000103/0a07c9c1-332b-8f5f-a0fb-3d93fb49f99b/report.md";
        Assert.Equal("B ready.\n", File.ReadAllText(Path.Combine(ready.Checkout, report)));
        Assert.Equal(new byte[] { 67, 0, 127 }, File.ReadAllBytes(Path.Combine(ready.Checkout, report[..^9], "artifacts/payload")));
        Assert.Equal(1, Count(ready.Execution.Prompt, f.Read().Inputs[ready.Execution.Inputs].Text));
        Assert.Contains(".idp/outbox/00000000-0000-0000-0000-000000000104/manifest.json", ready.Execution.Prompt);
        Assert.Contains("{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}", ready.Execution.Prompt);
        foreach (var checkout in new[] { ready.Checkout, f.Git.Folder })
        {
            Assert.Equal(0, f.Git.Run(checkout, "check-ignore", "-q", report).ExitCode);
            Assert.Equal(0, f.Git.Run(checkout, "check-ignore", "-q", ".idp/outbox/00000000-0000-0000-0000-000000000104/payload.bin").ExitCode);
            Assert.Equal(1, f.Git.Run(checkout, "check-ignore", "-q", "a.txt").ExitCode);
            Assert.Equal("A\n", File.ReadAllText(Path.Combine(checkout, "a.txt")));
        }
        var steps = new List<string>();
        var repeated = Assert.IsType<Preparation.Ready>(await f.Materializer(probe: steps.Add).Prepare(W, f.RunId, operation, U, new AttemptCause.Initial()));
        Assert.Equal(ready, repeated);
        Assert.Empty(steps);
        Assert.Single(f.Read().Preparations, pair => f.Read().Attempts[pair.Key.Attempt].Task == U);
    }

    [Fact]
    public async Task Missing_dependency_rejects_with_its_task_and_missing_context_is_recorded_without_blocking()
    {
        using var missing = new PreparationFixture(Connect(Linear(), C, U));
        await missing.Publish(T, missing.A);
        var rejection = Assert.IsType<Preparation.Rejected>(await missing.Prepare(U));
        Assert.Equal("MissingDependencyResult", rejection.Reason.Problem.ToString());
        Assert.Equal(C, rejection.Reason.Task);
        using var f = new PreparationFixture(Linear(context: true));
        await f.Publish(T, f.A);
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var input = f.Read().Inputs[ready.Execution.Inputs];
        Assert.Equal(new ConnectionKey(C, U), Assert.Single(input.Bindings.OfType<InputBinding.MissingContext>()).Edge);
        Assert.Equal("d4d26ecdf72779dbc9c5c983025fb51546c8f9ea", ready.Execution.Location.AttemptBase.Hex);
    }

    [Fact]
    public async Task Keys_retain_the_approved_base_and_subdirectory_refusal_allocates_nothing()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), run: new(Guid.Parse("019a9d2e-0000-7000-8000-000000000001")));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal("3940f0a5", f.Read().RunKey);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef("refs/idp/3940f0a5/base"))?.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        using var refused = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var sub = Directory.CreateDirectory(Path.Combine(refused.Git.Folder, "sub")).FullName;
        var blocked = Assert.IsType<Preparation.Blocked>(await refused.Materializer(project: sub).Prepare(W, refused.RunId, refused.Op(), T, new AttemptCause.Initial()));
        Assert.Equal("NotRepositoryRoot", blocked.Block.Problem.ToString());
        Assert.Null(refused.Read().RunKey);
        Assert.False(Directory.Exists(Path.Combine(refused.Git.Folder, ".worktrees")));
        Assert.Empty(GitFixture.Read(refused.Git.Open().RefSnapshot("refs/heads/idp/", "refs/idp/")));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(refused.Git.Folder, "a.txt")));
    }

    [Fact]
    public async Task Failed_creation_preserves_occupied_folder_and_retries_adopt_matching_leftover_branch()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        f.Git.Write(".worktrees/93f23689/90d5b0a2/keep", "mine\n");
        var operation = f.Op();
        var first = await f.Prepare(T, operation);
        var record = f.Read();
        var checkout = Path.Combine(f.Git.Folder, ".worktrees", record.RunKey!, record.TaskKeys[T]);
        Assert.Equal("93f23689", record.RunKey);
        Assert.Equal("90d5b0a2", record.TaskKeys[T]);
        Assert.Equal("UncertainOwnership", Assert.IsType<Preparation.Blocked>(first).Block.Problem.ToString());
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(checkout, "keep")));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/task/90d5b0a2"))?.Hex);
        File.Move(Path.Combine(checkout, "keep"), Path.Combine(f.Git.Folder, "saved"));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(f.Git.Folder, "saved")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_preparation_probe_recovers_on_a_fresh_materializer_without_duplicate_prepared_records(bool linear)
    {
        async Task<PreparationFixture> Fixture()
        {
            var fixture = new PreparationFixture(linear ? Linear() : FixtureWorkflow(Writer(T)),
                linear ? new("81ddb7c330112c7f16700ed002803a04b0bce693") : null);
            if (linear) await fixture.Publish(T, fixture.A);
            return fixture;
        }
        var task = linear ? U : T;
        var steps = new List<string>();
        using (var baseline = await Fixture())
        {
            var ready = Assert.IsType<Preparation.Ready>(await baseline.Materializer(probe: steps.Add)
                .Prepare(W, baseline.RunId, baseline.Op(), task, new AttemptCause.Initial()));
            Assert.Equal(linear ? "d4d26ecdf72779dbc9c5c983025fb51546c8f9ea" : "adfe40b30c176fb407933286f51d15ea9b54cdc3", ready.Execution.Location.AttemptBase.Hex);
        }
        Assert.Contains("git.create-worktree.after", steps);
        Assert.Contains("journal.prepared.after", steps);
        foreach (var step in steps.Distinct())
        {
            using var f = await Fixture();
            var operation = f.Op();
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: name => { if (name == step) throw new Crash(); })
                .Prepare(W, f.RunId, operation, task, new AttemptCause.Initial()));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(task, operation));
            Assert.Equal(linear ? "d4d26ecdf72779dbc9c5c983025fb51546c8f9ea" : "adfe40b30c176fb407933286f51d15ea9b54cdc3", ready.Execution.Location.AttemptBase.Hex);
            Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
            Assert.Single(f.Read().Preparations, pair => f.Read().Attempts[pair.Key.Attempt].Task == task);
            Assert.Equal(ready, Assert.IsType<Preparation.Ready>(await f.Prepare(task, operation)));
        }
    }

    [Fact]
    public async Task Interrupted_adoption_reuses_the_registered_owner_and_preserves_failed_creation_history()
    {
        async Task<(PreparationFixture Fixture, OperationId Operation)> Occupied()
        {
            var fixture = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var operation = fixture.Op();
            fixture.Git.Write(".worktrees/93f23689/90d5b0a2/keep", "mine\n");
            Assert.Equal("UncertainOwnership", Assert.IsType<Preparation.Blocked>(await fixture.Prepare(T, operation)).Block.Problem.ToString());
            File.Move(fixture.Git.PathOf(".worktrees/93f23689/90d5b0a2/keep"), fixture.Git.PathOf("saved"));
            return (fixture, operation);
        }
        var names = new List<string>();
        var baseline = await Occupied();
        using (baseline.Fixture)
            Assert.IsType<Preparation.Ready>(await baseline.Fixture.Materializer(probe: names.Add)
                .Prepare(W, baseline.Fixture.RunId, baseline.Operation, T, new AttemptCause.Initial()));
        Assert.Contains("git.adopt-worktree.after", names);
        Assert.Contains("journal.adopt-worktree-intent.after", names);
        foreach (var name in names.Distinct())
        {
            var interrupted = await Occupied();
            using var f = interrupted.Fixture;
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == name) throw new Crash(); })
                .Prepare(W, f.RunId, interrupted.Operation, T, new AttemptCause.Initial()));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, interrupted.Operation));
            Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", ready.Execution.Location.AttemptBase.Hex);
            Assert.Equal("mine\n", File.ReadAllText(f.Git.PathOf("saved")));
            Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
            Assert.Single(f.Read().Preparations);
            Assert.Equal("UncertainOwnership", Assert.Single(f.Read().Blocks).Value.Block.Problem.ToString());
        }
    }

    [Fact]
    public async Task Dirty_retry_checkout_is_preserved_and_blocked()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var original = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(W, f.RunId, f.Op(), original.Execution.Launch.Attempt,
            RecoveryOutcome.NotStarted, f.Op(), "Did not launch."));
        f.Git.Write("left.txt", "unfinished\n", original.Checkout);
        var retry = Assert.IsType<Preparation.Blocked>(await f.Prepare(T, cause: new AttemptCause.Retry(original.Execution.Launch.Attempt, f.Op())));
        Assert.Equal("DirtyWorktree", retry.Block.Problem.ToString());
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(original.Checkout, "left.txt")));
    }

    private static int Count(string text, string section) => text.Split(section, StringSplitOptions.None).Length - 1;
    private sealed class Crash : Exception;
}
