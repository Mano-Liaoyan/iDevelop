using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

public sealed class PreparationTests
{
    [Theory]
    [InlineData("--sparse-index")]
    [InlineData("--no-sparse-index")]
    [InlineData("legacy")]
    public async Task A_sparse_clone_gives_the_writer_a_full_checkout_that_publishes_an_out_of_cone_edit(string layout)
    {
        using var f = CheckoutFixture(layout);
        var patterns = File.ReadAllBytes(f.Git.PathOf(".git/info/sparse-checkout"));
        var operation = f.Op();
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        Assert.Equal(writer, await f.Prepare(T, operation));
        Assert.Equal(new byte[] { 111, 117, 116, 115, 105, 100, 101, 0, 98, 121, 116, 101, 115, 10 },
            File.ReadAllBytes(Path.Combine(writer.Checkout, "outside/kept.bin")));
        Assert.Equal("H outside/kept.bin\n", f.Git.Run(writer.Checkout, "ls-files", "-v", "outside/kept.bin").Text);
        Assert.Equal("S outside/kept.bin\n", f.Git.Git("ls-files", "-v", "outside/kept.bin"));
        Assert.False(File.Exists(f.Git.PathOf("outside/kept.bin")));
        f.Git.Write("outside/kept.bin", "writer edit\n", writer.Checkout);
        f.Git.Write("a/inside.txt", "edited\n", writer.Checkout);
        f.Close(writer);
        var publicationOperation = f.Op();
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(writer.Execution.Location.Owner.Task), publicationOperation,
            writer.Execution.Launch.Attempt));
        var commit = Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex;
        Assert.Equal("writer edit\n", f.Git.Git("show", commit + ":outside/kept.bin"));
        Assert.Equal("edited\n", f.Git.Git("show", commit + ":a/inside.txt"));
        Assert.Equal(accepted, f.Materializer().Publish(f.Lease(writer.Execution.Location.Owner.Task), publicationOperation, writer.Execution.Launch.Attempt));
        Assert.Equal("", f.Git.Run(writer.Checkout, "status", "--porcelain").Text);
        Assert.Equal(patterns, File.ReadAllBytes(f.Git.PathOf(".git/info/sparse-checkout")));
        Assert.Equal("S outside/kept.bin\n", f.Git.Git("ls-files", "-v", "outside/kept.bin"));
        Assert.False(File.Exists(f.Git.PathOf("outside/kept.bin")));
    }

    [Theory]
    [InlineData("--sparse-index")]
    [InlineData("--no-sparse-index")]
    [InlineData("legacy")]
    public async Task An_out_of_cone_edit_in_a_sparse_clone_survives_salvage_and_retry(string layout)
    {
        using var f = CheckoutFixture(layout);
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("outside/kept.bin", "writer edit\n", writer.Checkout);
        f.Git.Write("a/inside.txt", "edited\n", writer.Checkout);
        f.Close(writer, outcome: TerminalAttemptOutcome.Failed);
        var salvageOperation = f.Op();
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(writer.Execution.Location.Owner.Task), salvageOperation, writer.Execution.Launch.Attempt));
        Assert.Equal("writer edit\n", f.Git.Git("show", retained.Commit.Hex + ":outside/kept.bin"));
        Assert.Equal("edited\n", f.Git.Git("show", retained.Commit.Hex + ":a/inside.txt"));
        Assert.Equal(retained, f.Materializer().Salvage(f.Lease(writer.Execution.Location.Owner.Task), salvageOperation, writer.Execution.Launch.Attempt));
        var resetOperation = f.Op();
        var confirmation = f.Op();
        var reset = Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), resetOperation, retained.Receipt.Plan, confirmation));
        Assert.Equal(writer.Execution.Location.AttemptBase, reset.Target);
        Assert.Equal(reset, f.Materializer().ResetForRetry(f.Lease(T), resetOperation, retained.Receipt.Plan, confirmation));
        Assert.Equal(retained.Commit.Hex + "\n", f.Git.Git("for-each-ref", "--contains", retained.Commit.Hex, "--format=%(objectname)", retained.Receipt.Ref));
        Assert.Equal(new byte[] { 111, 117, 116, 115, 105, 100, 101, 0, 98, 121, 116, 101, 115, 10 },
            File.ReadAllBytes(Path.Combine(writer.Checkout, "outside/kept.bin")));
        Assert.Equal("inside\n", File.ReadAllText(Path.Combine(writer.Checkout, "a/inside.txt")));
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("salvage")]
    public async Task Sparse_state_the_writer_creates_blocks_publication_and_salvage_and_keeps_files(string mode)
    {
        using var f = CheckoutFixture();
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal(0, f.Git.Run(writer.Checkout, "sparse-checkout", "set", "a").ExitCode);
        Assert.Equal("S outside/kept.bin\n", f.Git.Run(writer.Checkout, "ls-files", "-v", "outside/kept.bin").Text);
        Assert.False(File.Exists(Path.Combine(writer.Checkout, "outside/kept.bin")));
        f.Git.Write("a/inside.txt", "edited\n", writer.Checkout);
        f.Close(writer, outcome: mode == "publish" ? TerminalAttemptOutcome.Succeeded : TerminalAttemptOutcome.Failed);
        var index = GitFixture.Read(f.Git.Open().IndexPath(writer.Checkout));
        var bytes = File.ReadAllBytes(index);
        var operation = f.Op();
        MaterializationBlock block;
        if (mode == "publish")
        {
            var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(writer.Execution.Location.Owner.Task), operation, writer.Execution.Launch.Attempt));
            block = blocked.Block;
            Assert.Equal(blocked, f.Materializer().Publish(f.Lease(writer.Execution.Location.Owner.Task), operation, writer.Execution.Launch.Attempt));
        }
        else
        {
            var blocked = Assert.IsType<Salvage.Blocked>(f.Materializer().Salvage(f.Lease(writer.Execution.Location.Owner.Task), operation, writer.Execution.Launch.Attempt));
            block = blocked.Block;
            Assert.Equal(blocked, f.Materializer().Salvage(f.Lease(writer.Execution.Location.Owner.Task), operation, writer.Execution.Launch.Attempt));
        }
        Assert.Equal("DirtyWorktree", block.Problem.ToString());
        Assert.Equal("The index hides changes to outside/kept.bin with assume-unchanged or skip-worktree.", block.Detail);
        Assert.Equal(writer.Execution.Location.AttemptBase.Hex, GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch))?.Hex);
        Assert.Empty(f.Read().Results);
        Assert.Empty(f.Read().Salvages);
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(writer.Checkout, "a/inside.txt")));
        Assert.False(File.Exists(Path.Combine(writer.Checkout, "outside/kept.bin")));
    }

    [Fact]
    public async Task An_out_of_cone_edit_the_writer_restores_after_making_its_checkout_sparse_is_published()
    {
        using var f = CheckoutFixture();
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        NextSecond(20);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "sparse-checkout", "set", "a").ExitCode);
        f.Git.Write("outside/kept.bin", "writer edit\n", writer.Checkout);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "update-index", "--no-skip-worktree", "outside/kept.bin").ExitCode);
        Assert.Equal("H outside/kept.bin\n", f.Git.Run(writer.Checkout, "-c", "core.sparseCheckout=false",
            "ls-files", "-v", "outside/kept.bin").Text);
        f.Close(writer);
        var operation = f.Op();
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(writer.Execution.Location.Owner.Task), operation, writer.Execution.Launch.Attempt));
        var commit = Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex;
        Assert.Equal("writer edit\n", f.Git.Git("show", commit + ":outside/kept.bin"));
        Assert.Equal("writer edit\n", File.ReadAllText(Path.Combine(writer.Checkout, "outside/kept.bin")));
        Assert.Equal(accepted, f.Materializer().Publish(f.Lease(writer.Execution.Location.Owner.Task), operation, writer.Execution.Launch.Attempt));
    }

    private static void NextSecond(int afterMs)
    {
        var now = DateTime.UtcNow;
        var target = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc)
            .AddSeconds(1).AddMilliseconds(afterMs);
        while (DateTime.UtcNow < target) Thread.Sleep(1);
    }

    private static PreparationFixture CheckoutFixture(string? layout = null) => new(FixtureWorkflow(Writer(T)), configureBase: git =>
    {
        git.Write("a/inside.txt", "inside\n");
        git.Write("outside/kept.bin", "outside\0bytes\n");
        var commit = git.Commit("sparse");
        if (layout == "legacy")
        {
            git.Git("config", "core.sparseCheckout", "true");
            git.Write(".git/info/sparse-checkout", "/a/\n");
            git.Git("read-tree", "-mu", "HEAD");
        }
        else if (layout is not null) git.Git("sparse-checkout", "set", layout, "a");
        return commit;
    });

    [Theory]
    [InlineData("none", true)]
    [InlineData("git.submodules.before", true)]
    [InlineData("git.submodules.after", true)]
    [InlineData("none", false)]
    public async Task Preparation_preserves_an_initialized_submodule_commit(string point, bool continuation)
    {
        using var module = new GitFixture();
        module.Diamond();
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", module.Folder, "m");
            return git.Commit("module");
        });
        var environment = new Dictionary<string, string>(f.Git.Environment)
        {
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "protocol.file.allow", ["GIT_CONFIG_VALUE_0"] = "always",
        };
        Materializer Materializer(Action<string>? probe = null) => IDevelop.Execution.Materializer.Open(f.Git.Folder, f.Store,
            null, new QuiescentBoundary(), new Clock(), environment, probe);
        var ready = Assert.IsType<Preparation.Ready>(await Materializer().Prepare(f.Lease(T), f.Op(), new AttemptCause.Initial()));
        var checkout = Path.Combine(ready.Checkout, "m");
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3\n", f.Git.Run(checkout, "rev-parse", "HEAD").Text);
        f.Git.Write("b.txt", "B\n", checkout);
        Assert.Equal(0, f.Git.Run(checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "b").ExitCode);
        f.Close(ready);
        var operation = f.Op();
        AttemptCause cause = continuation ? new AttemptCause.Continue(ready.Execution.Launch.Attempt, f.Op()) :
            new AttemptCause.Retry(ready.Execution.Launch.Attempt, f.Op());
        if (point != "none")
            await Assert.ThrowsAsync<Crash>(async () => await Materializer(step => { if (step == point) throw new Crash(); })
                .Prepare(f.Lease(T), operation, cause));
        var outcome = await Materializer().Prepare(f.Lease(T), operation, cause);
        if (continuation)
        {
            var continued = Assert.IsType<Preparation.Ready>(outcome);
            Assert.Equal(ready.Execution.Location.Owner, continued.Execution.Location.Owner);
            Assert.Equal(continued, await Materializer().Prepare(f.Lease(T), operation, cause));
        }
        else
        {
            var blocked = Assert.IsType<Preparation.Blocked>(outcome);
            Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
            Assert.Equal("The checkout tip or contents differ from the recorded attempt base.", blocked.Block.Detail);
            Assert.Equal(blocked, await Materializer().Prepare(f.Lease(T), operation, cause));
        }
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67\n", f.Git.Run(checkout, "rev-parse", "HEAD").Text);
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(checkout, "b.txt")));
        Assert.Equal("commit\n", f.Git.Run(checkout, "cat-file", "-t", "2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67").Text);
        Assert.Equal("160000 adfe40b30c176fb407933286f51d15ea9b54cdc3 0\tm\n", f.Git.Run(ready.Checkout, "ls-files", "--stage", "m").Text);
    }

    [Fact]
    public async Task Adoption_rechecks_the_live_branch_tip_before_observing_creation()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        f.Git.Write(".worktrees/93f23689/90d5b0a2/keep", "mine\n");
        var operation = f.Op();
        Assert.Equal("UncertainOwnership", Assert.IsType<Preparation.Blocked>(await f.Prepare(T, operation)).Block.Problem.ToString());
        File.Move(f.Git.PathOf(".worktrees/93f23689/90d5b0a2/keep"), f.Git.PathOf("saved"));
        var foreign = f.Git.Git("commit-tree", "adfe40b30c176fb407933286f51d15ea9b54cdc3^{tree}",
            "-p", "adfe40b30c176fb407933286f51d15ea9b54cdc3", "-m", "out of band").Trim();
        Assert.Equal("8d14445e6226129e327b4f1f807c957d99479399", foreign);
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Materializer(probe: step =>
        {
            if (step == "git.adopt-worktree.before") f.Git.Git("update-ref", "refs/heads/idp/93f23689/task/90d5b0a2", foreign);
        }).Prepare(f.Lease(T), operation, new AttemptCause.Initial()));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Unobserved creation has an unexpected branch tip.", blocked.Block.Detail);
        Assert.Equal(blocked, await f.Prepare(T, operation));
        Assert.Equal("8d14445e6226129e327b4f1f807c957d99479399", GitFixture.Read(f.Git.Open().ReadRef("refs/heads/idp/93f23689/task/90d5b0a2"))?.Hex);
        Assert.Equal("mine\n", File.ReadAllText(f.Git.PathOf("saved")));
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public async Task Hidden_index_entries_block_clean_preparation_and_preserve_bytes(string flag)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Close(ready);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", flag, "a.txt").ExitCode);
        f.Git.Write("a.txt", "hidden edit\n", ready.Checkout);
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var bytes = File.ReadAllBytes(index);
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Prepare(T, cause: new AttemptCause.Retry(ready.Execution.Launch.Attempt, f.Op())));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", blocked.Block.Detail);
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Equal("hidden edit\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
    }

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
        var repeated = Assert.IsType<Preparation.Ready>(await f.Materializer(probe: steps.Add).Prepare(f.Lease(U), operation, new AttemptCause.Initial()));
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
        var store = RunStore.Open(sub);
        Assert.IsType<RunDecision.Created>(store.Approve(W, refused.RunId, refused.Op(), refused.Read().Revision,
            new(refused.A, BaseChoice.Head)));
        using var permit = Assert.IsType<ControlTake.Owned>(store.TakeControl(W, refused.RunId)).Permit;
        using var lease = Assert.IsType<LeaseTake.Taken>(permit.TakeTask(T)).Lease;
        var materializer = Execution.Materializer.Open(sub, store, null, new QuiescentBoundary(), new Clock(), refused.Git.Environment);
        var blocked = Assert.IsType<Preparation.Blocked>(await materializer.Prepare(lease, refused.Op(), new AttemptCause.Initial()));
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
                .Prepare(baseline.Lease(task), baseline.Op(), new AttemptCause.Initial()));
            Assert.Equal(linear ? "d4d26ecdf72779dbc9c5c983025fb51546c8f9ea" : "adfe40b30c176fb407933286f51d15ea9b54cdc3", ready.Execution.Location.AttemptBase.Hex);
        }
        Assert.Contains("git.create-worktree.after", steps);
        Assert.Contains("journal.prepared.after", steps);
        foreach (var step in steps.Distinct())
        {
            using var f = await Fixture();
            var operation = f.Op();
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: name => { if (name == step) throw new Crash(); })
                .Prepare(f.Lease(task), operation, new AttemptCause.Initial()));
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
                .Prepare(baseline.Fixture.Lease(T), baseline.Operation, new AttemptCause.Initial()));
        Assert.Contains("git.adopt-worktree.after", names);
        Assert.Contains("journal.adopt-worktree-intent.after", names);
        foreach (var name in names.Distinct())
        {
            var interrupted = await Occupied();
            using var f = interrupted.Fixture;
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == name) throw new Crash(); })
                .Prepare(f.Lease(T), interrupted.Operation, new AttemptCause.Initial()));
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
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(original.Execution.Location.Owner.Task), f.Op(), original.Execution.Launch.Attempt, RecoveryOutcome.NotStarted, f.Op(), "Did not launch."));
        f.Git.Write("left.txt", "unfinished\n", original.Checkout);
        var retry = Assert.IsType<Preparation.Blocked>(await f.Prepare(T, cause: new AttemptCause.Retry(original.Execution.Launch.Attempt, f.Op())));
        Assert.Equal("DirtyWorktree", retry.Block.Problem.ToString());
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(original.Checkout, "left.txt")));
    }

    private static int Count(string text, string section) => text.Split(section, StringSplitOptions.None).Length - 1;
    private sealed class Crash : Exception;
}
