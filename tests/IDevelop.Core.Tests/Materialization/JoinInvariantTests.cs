using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Materialization.JoinTests;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

public sealed class JoinInvariantTests
{
    private const string JoinRef = "refs/heads/idp/93f23689/join/c67f2fc3";

    private static async Task<OwnedCode> Write(PreparationFixture f, TaskId task, string path, string text, string message,
        string? committerDate = null, TimeProvider? clock = null)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(task));
        if (committerDate is null) OwnCommit(f, ready, path, text, message);
        else
        {
            f.Git.Write(path, text, ready.Checkout);
            Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "--all").ExitCode);
            var environment = new Dictionary<string, string>(f.Git.Environment) { ["GIT_COMMITTER_DATE"] = committerDate };
            Assert.Equal(0, f.Git.Run(ready.Checkout, environment, "-c", "commit.gpgSign=false", "commit", "-q", "-m", message).ExitCode);
        }
        await f.Close(ready);
        var materializer = clock is null ? Open(f) : MergeJoins.Open(f.Git.Folder, f.Store, new QuiescentBoundary(), clock, f.Git.Environment);
        return Assert.IsType<CodeOutput.Produced>(Assert.IsType<Publication.Accepted>(materializer.Publish(f.Lease(ready.Execution.Location.Owner.Task), f.Op(),
            ready.Execution.Launch.Attempt)).Result.Code).Code;
    }

    [Fact]
    public async Task Join_committer_time_is_the_latest_of_distinct_parent_times()
    {
        using var f = new PreparationFixture(Diamond(true));
        await Write(f, T, "b.txt", "B\n", "b", "2026-10-07T00:01:00Z", new FixedClock(At.AddMinutes(1)));
        await Write(f, C, "c.txt", "C\n", "c", "2026-10-07T00:03:00Z", new FixedClock(At.AddMinutes(3)));
        await Write(f, D, "e.txt", "E\n", "e", "2026-10-07T00:02:00Z", new FixedClock(At.AddMinutes(2)));
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        Assert.Equal("1791331380\n", f.Git.Git("show", "-s", "--format=%ct", ready.Execution.Location.AttemptBase.Hex));
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
        Assert.Equal("E\n", File.ReadAllText(Path.Combine(ready.Checkout, "e.txt")));
    }

    private sealed class FixedClock(DateTimeOffset at) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => at; }

    [Fact]
    public async Task Forwarded_duplicate_revision_is_one_parent_and_every_source_is_recorded()
    {
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Writer(T), Task(C), Writer(D), Writer(U)), T, C), T, U), C, U), D, U);
        using var f = new PreparationFixture(workflow);
        await Write(f, T, "b.txt", "B\n", "b");
        var forwarded = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        await f.Close(forwarded, "Forwarded.\n");
        Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(), forwarded.Execution.Launch.Attempt, forwarded.Execution.Inputs, "Forwarded.\n"));
        await Write(f, D, "e.txt", "E\n", "e");
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        var plan = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Join>());
        Assert.Equal(3, plan.Sources.Length);
        Assert.Equal(2, GitFixture.Read(f.Git.Open().ReadCommit(ready.Execution.Location.AttemptBase)).Parents.Length);
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
        Assert.Equal("E\n", File.ReadAllText(Path.Combine(ready.Checkout, "e.txt")));
    }

    [Fact]
    public async Task Descendant_third_source_merges_through_both_accumulator_parents()
    {
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(D), Writer(U)), T, D), T, U), C, U), D, U);
        using var f = new PreparationFixture(workflow);
        await Write(f, T, "b.txt", "B\n", "b");
        await Write(f, C, "c.txt", "C\n", "c");
        await Write(f, D, "b.txt", "B then D\n", "e");
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        Assert.Equal("B then D\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
        Assert.Equal(3, GitFixture.Read(f.Git.Open().ReadCommit(ready.Execution.Location.AttemptBase)).Parents.Length);
    }

    [Fact]
    public async Task Recorded_join_commit_must_equal_the_commit_restored_from_its_recipe()
    {
        using var f = new PreparationFixture(Diamond());
        await Sources(f);
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await Prepare(f, operation, point => { if (point == "journal.plan.after") throw new Crash(); }));
        var preparation = (MaterializationPlan.Preparation)f.Read().Plans[OperationIds.Derive(operation, "plan")];
        var repository = f.Git.Open();
        var recipe = new CommitRecipe(new("0f9ebdad5c1d87d0be67f3e280cb51f62545a42a"),
            [new("b350f18e8c7f922d58c54e415a95fb0a4b6fa249"), new("e954b83b974db2a85981aa86b3a2eabee42d7803")],
            "join\n", "E2 <e2@example.test>", "E2 <e2@example.test>", At);
        var joinOperation = OperationIds.Derive(operation, "join");
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, joinOperation, new RunEvent.Planned(
            new MaterializationPlan.Join(U, preparation.Inputs, preparation.Sources, recipe, f.A, null, JoinRef))));
        var blocked = Assert.IsType<JoinOutcome.Blocked>(await new MergeJoins(f.Git.Folder, f.Store, f.Git.Environment)
            .Compose(new(f.Permit, joinOperation, U, preparation.Inputs, preparation.Sources, null), CancellationToken.None));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Equal("The recorded join commit does not match its recipe.", blocked.Block.Detail);
        Assert.Null(GitFixture.Read(repository.ReadRef(JoinRef)));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(repository.ReadRef("refs/heads/main"))?.Hex);
    }

    [Fact]
    public async Task Directory_rename_conflict_evidence_includes_paths_only_named_in_messages()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: git =>
        {
            git.Write("d/file", "base\n");
            return git.Commit("directory");
        });
        var b = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal(0, f.Git.Run(b.Checkout, "mv", "d", "renamed").ExitCode);
        Assert.Equal(0, f.Git.Run(b.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "rename").ExitCode);
        await f.Close(b);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(b.Execution.Location.Owner.Task), f.Op(), b.Execution.Launch.Attempt));
        await Write(f, C, "d/added", "added\n", "add");
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, f.Op()));
        Assert.Equal("FanInConflict", blocked.Block.Problem.ToString());
        Assert.Equal(new[] { "d/added", "renamed/added" }, blocked.Block.Conflict!.Paths);
        Assert.Equal(new[] { "renamed/added" }, blocked.Block.Conflict.Stages.Select(stage => stage.Path));
        Assert.Equal("Merge step 1 of 1 conflicts in d/added, renamed/added.", blocked.Block.Detail);
    }

    private static TaskDefinition Reviewer() => new(U, new Blueprint(new("example.review", 1), "Review",
        new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")), [], new(Task().Execution, ConversationMode.Autonomous))) { Title = "Review" };

    private static Workflow FanIn() => Connect(Connect(Connect(FixtureWorkflow(Writer(T), Writer(D), Task(C), Reviewer()), D, C), C, U), T, U);

    private static void CloseReviewTurn(PreparationFixture f, Preparation.Ready ready)
    {
        var execution = ready.Execution;
        var attempt = f.Read().Attempts[execution.Launch.Attempt];
        var inputs = f.Read().Inputs[execution.Inputs];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(execution.Location.Owner.Task), f.Op(), execution.Launch, inputs, execution.PromptHash));
        var definition = f.Read().Revision.Snapshot.Tasks[U];
        var folder = f.Store.AttemptFolder(W, f.RunId, U, attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
            new AttemptEvent.Requested(At, attempt.Id, U, definition.Title, definition.Execution!, execution.Prompt, "codex", [])
            { RunBinding = new(W, f.RunId, attempt.Revision, inputs.Id), ReadOnly = true, Subject = T, Conversation = definition.Conversation }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("review")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Changes requested.")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }
        Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(execution.Location.Owner.Task), f.Op(), execution.Launch, new RootExit.Exited(0)));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), execution.Launch, Checkpoint(folder)));
    }

    private static async Task<Preparation.Ready> ReviewedJoin(PreparationFixture f)
    {
        await f.Publish(T, f.A);
        await Write(f, D, "c.txt", "C\n", "c");
        var forwarded = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        await f.Close(forwarded, "Forwarded.\n");
        Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(), forwarded.Execution.Launch.Attempt, forwarded.Execution.Inputs, "Forwarded.\n"));
        var review = Assert.IsType<Preparation.Ready>(await Open(f).Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial(), "Review."));
        CloseReviewTurn(f, review);
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T, cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 1, 0)), prompt: "Fix."));
        OwnCommit(f, fix, "a.txt", "Fixed\n", "fix");
        await f.Close(fix, "Repaired.\n");
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(fix.Execution.Location.Owner.Task), f.Op(), fix.Execution.Launch.Attempt));
        return review;
    }

    [Fact]
    public async Task Clean_reviewer_refresh_retains_the_previous_join_before_moving_the_branch()
    {
        using var f = new PreparationFixture(FanIn());
        var review = await ReviewedJoin(f);
        var first = review.Execution.Location.AttemptBase;
        var ready = Assert.IsType<Preparation.Ready>(await Open(f).PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), f.Op(),
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        var joins = f.Read().Plans.Values.OfType<MaterializationPlan.Join>().ToArray();
        Assert.Equal(2, joins.Length);
        Assert.Equal(first, Assert.Single(joins, plan => plan.Commit == ready.Execution.Location.AttemptBase).Previous);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "cat-file", "-e", first.Hex + "^{commit}").ExitCode);
        Assert.Equal(ready.Execution.Location.AttemptBase.Hex, GitFixture.Read(f.Git.Open().ReadRef(JoinRef))?.Hex);
    }

    [Fact]
    public async Task Reviewer_refresh_retry_with_republished_sources_rejects_the_existing_plans_input_identity()
    {
        using var f = new PreparationFixture(FanIn());
        var review = await ReviewedJoin(f);
        var operation = f.Op();
        var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
        await Assert.ThrowsAsync<Crash>(async () => await Open(f, point => { if (point == "journal.join-plan.after") throw new Crash(); })
            .PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review the fix."));
        var current = f.Read().CurrentResults[T];
        var again = Assert.IsType<Preparation.Ready>(await f.Prepare(T, cause: new AttemptCause.Continue(((ResultOrigin.Executed)current.Origin).Attempt, f.Op())));
        OwnCommit(f, again, "a.txt", "Fixed again\n", "fix again");
        await f.Close(again);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(again.Execution.Location.Owner.Task), f.Op(), again.Execution.Launch.Attempt));
        var blocked = Assert.IsType<Preparation.Blocked>(await Open(f).PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review the fix."));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Equal($"Join operation {OperationIds.Derive(operation, "join").Value:D} has different inputs.", blocked.Block.Detail);
        Assert.Equal(review.Execution.Location.AttemptBase.Hex, GitFixture.Read(f.Git.Open().ReadRef(JoinRef))?.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
    }
}
