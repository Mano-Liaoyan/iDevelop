using System.Collections.Immutable;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

public sealed class ReviewMaterializationTests
{
    private const string First = "d4d26ecdf72779dbc9c5c983025fb51546c8f9ea";
    private const string Fixed = "6463a9ac1ed53bd0e862c236ad6b9aa47eca4e16";
    private static TaskDefinition Reviewer() => new(U, new Blueprint(new("example.review", 1), "Review",
        new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")), [], new(Task().Execution, ConversationMode.Autonomous))) { Title = "Review" };
    private static Workflow Workflow() => Connect(Connect(FixtureWorkflow(Writer(T), Reviewer(), Writer(D)), T, U), U, D);

    private static async Task<(Preparation.Ready Review, Preparation.Ready Fix, ResultRecord FirstResult, ResultRecord FixedResult)> FixedWriter(PreparationFixture f)
    {
        var result = await f.Publish(T, f.A);
        var review = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: "Review the changes."));
        await CloseReviewTurn(f, review);
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 1, 0)), prompt: "Repair the finding."));
        f.Git.Write("a.txt", "Fixed\n", fix.Checkout);
        await f.Close(fix, "Repaired.\n");
        var repaired = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(fix.Execution.Location.Owner.Task), f.Op(), fix.Execution.Launch.Attempt)).Result;
        return (review, fix, result, repaired);
    }

    internal static async Task CloseReviewTurn(PreparationFixture f, Preparation.Ready ready)
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
        Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(execution.Location.Owner.Task), f.Op(), execution.Launch, Checkpoint(folder)));
    }

    private static async Task<ResultRecord> Agree(PreparationFixture f, Preparation.Ready ready)
    {
        var attempt = f.Read().Attempts[ready.Execution.Launch.Attempt];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(ready.Execution.Location.Owner.Task), f.Op(),
            ready.Execution.Launch, f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));
        var folder = f.Store.AttemptFolder(W, f.RunId, U, attempt.Id);
        using (var log = AttemptLog.Open(folder))
        {
            log.Append(new AttemptEvent.TurnRequested(At, ready.Execution.Prompt, "codex", []));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("review")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Approved.\n")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
            log.Append(new AttemptEvent.Concluded(At, null));
        }
        Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch, new RootExit.Exited(0)));
        Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch, Checkpoint(folder)));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), attempt.Id, TerminalAttemptOutcome.Succeeded, Checkpoint(folder)));
        return Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(),
            attempt.Id, ready.Execution.Inputs, "Approved.\n")).Event).Result;
    }

    [Fact]
    public async Task Review_fix_keeps_the_subject_checkout_and_agreement_forwards_the_refreshed_writer()
    {
        using var f = new PreparationFixture(Workflow());
        var (review, fix, first, repaired) = await FixedWriter(f);
        var original = f.Read().Preparations[new(A1, 1)];
        Assert.Equal(".worktrees/93f23689/90d5b0a2", fix.Execution.Location.Owner.RelativePath);
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2", fix.Execution.Location.Owner.Branch);
        Assert.Equal(original.Location.Owner, fix.Execution.Location.Owner);
        Assert.Equal(First, fix.Execution.Location.AttemptBase.Hex);
        Assert.Equal(first.Id, repaired.Supersedes);
        Assert.Equal(Fixed, Assert.IsType<CodeOutput.Produced>(repaired.Code).Code.Commit.Hex);
        var oldInput = f.Read().Inputs[review.Execution.Inputs];
        Assert.Equal(first.Id, oldInput.Review!.SubjectResult);
        Assert.Equal(First, oldInput.CodeBase.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
        var refreshed = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), f.Op(),
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal(Fixed, refreshed.Execution.Location.AttemptBase.Hex);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(refreshed.Checkout, "a.txt")));
        var input = f.Read().Inputs[refreshed.Execution.Inputs];
        Assert.Equal(new ReviewInput(T, repaired.Id), input.Review);
        Assert.Equal(1, refreshed.Execution.Prompt.Split("Repaired.", StringSplitOptions.None).Length - 1);
        Assert.Equal("Repaired.\n", File.ReadAllText(Path.Combine(refreshed.Checkout, input.Files.Single().RelativePath)));
        Assert.Equal("B ready.\n", File.ReadAllText(Path.Combine(refreshed.Checkout, oldInput.Files.Single().RelativePath)));
        var agreement = await Agree(f, refreshed);
        Assert.Equal(new CodeOutput.Forwarded(input.Id), agreement.Code);
        var successor = Assert.IsType<Preparation.Ready>(await f.Prepare(D));
        var single = Assert.IsType<CodeSelection.Single>(f.Read().Inputs[successor.Execution.Inputs].Code);
        Assert.Equal(U, single.Source.Task);
        Assert.Equal(new[] { T }, single.Source.Owners);
        Assert.Equal(Fixed, single.Source.Commit.Hex);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(successor.Checkout, "a.txt")));
        Assert.Equal("A\n", f.Git.Git("show", First + ":a.txt"));
        Assert.Equal(First, f.Read().Inputs[review.Execution.Inputs].CodeBase.Hex);
        Assert.Equal(first.Id, f.Read().Inputs[review.Execution.Inputs].Review!.SubjectResult);
    }

    [Fact]
    public async Task Dirty_reviewer_blocks_refresh_without_recording_new_inputs_or_resetting_files()
    {
        using var f = new PreparationFixture(Workflow());
        var (review, _, first, repaired) = await FixedWriter(f);
        f.Git.Write("a.txt", "reviewer edits\n", review.Checkout);
        var before = f.Read();
        var operation = f.Op();
        var block = Assert.IsType<Preparation.Blocked>(await f.Materializer().PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), operation,
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("DirtyWorktree", block.Block.Problem.ToString());
        Assert.Equal(before.Inputs.Count, f.Read().Inputs.Count);
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
        Assert.Equal("reviewer edits\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
        Assert.Equal(First, GitFixture.Read(f.Git.Open().ReadRef(review.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal(first.Id, f.Read().Inputs[review.Execution.Inputs].Review!.SubjectResult);
        Assert.Equal(repaired.Id, f.Read().CurrentResults[T].Id);
        f.Git.Write("a.txt", "A\n", review.Checkout);
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), operation,
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Ignored_reviewer_file_blocks_refresh_when_the_fix_tracks_its_path()
    {
        using var f = new PreparationFixture(Workflow(), configureBase: git =>
        {
            git.Write(".gitignore", "cache.txt\n");
            return git.Commit("ignore cache");
        });
        await f.Publish(T, GitFixture.Read(f.Git.Open().ResolveCommit("HEAD"))!.Value);
        var review = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: "Review."));
        await CloseReviewTurn(f, review);
        f.Git.Write("cache.txt", "reviewer\n", review.Checkout);
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 1, 0)), prompt: "Fix."));
        f.Git.Write("cache.txt", "subject\n", fix.Checkout);
        Assert.Equal(0, f.Git.Run(fix.Checkout, "add", "-f", "cache.txt").ExitCode);
        await f.Close(fix, "Repaired.\n");
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(fix.Execution.Location.Owner.Task), f.Op(), fix.Execution.Launch.Attempt));
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Materializer().PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), f.Op(),
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("Ignored files obstruct the reset target and must be preserved.", blocked.Block.Detail);
        Assert.Equal("reviewer\n", File.ReadAllText(Path.Combine(review.Checkout, "cache.txt")));
        Assert.Equal("subject\n", File.ReadAllText(Path.Combine(fix.Checkout, "cache.txt")));
    }

    [Fact]
    public async Task Accepted_fix_marks_finished_consumers_stale_and_keeps_running_consumers_frozen()
    {
        var workflow = Connect(Connect(Connect(FixtureWorkflow(Writer(T), Reviewer(), Task(C), Writer(D)), T, U), T, C), T, D);
        using var f = new PreparationFixture(workflow);
        var first = await f.Publish(T, f.A);
        var consumer = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        await f.Close(consumer, "Consumed.\n");
        var consumed = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(),
            consumer.Execution.Launch.Attempt, consumer.Execution.Inputs, "Consumed.\n")).Event).Result;
        var running = Assert.IsType<Preparation.Ready>(await f.Prepare(D));
        var runningInput = f.Read().Inputs[running.Execution.Inputs];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(running.Execution.Location.Owner.Task), f.Op(),
            running.Execution.Launch, runningInput, running.Execution.PromptHash));
        var reviewer = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: "Review."));
        await CloseReviewTurn(f, reviewer);
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, reviewer.Execution.Launch.Attempt, 1, 0)), prompt: "Fix."));
        f.Git.Write("a.txt", "Fixed\n", fix.Checkout);
        await f.Close(fix, "Repaired.\n");
        var repaired = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(fix.Execution.Location.Owner.Task), f.Op(), fix.Execution.Launch.Attempt)).Result;
        Assert.Equal(first.Id, repaired.Supersedes);
        Assert.Contains(consumed.Id, f.Read().StaleResults);
        Assert.Equal(first.Id, Assert.Single(f.Read().Claims[running.Execution.Launch].Inputs.Bindings.OfType<InputBinding.Provided>()).Result);
        Assert.Equal(First, f.Read().Claims[running.Execution.Launch].Inputs.CodeBase.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(running.Checkout, "a.txt")));
        Assert.Equal("A\n", f.Git.Git("show", First + ":a.txt"));
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(fix.Checkout, "a.txt")));
        Assert.Equal(first.Id, f.Read().Inputs[reviewer.Execution.Inputs].Review!.SubjectResult);
    }

    [Fact]
    public async Task Refresh_reducer_requires_closed_previous_turn_fresh_bindings_and_an_observed_reset()
    {
        using var f = new PreparationFixture(Workflow());
        var (review, _, first, repaired) = await FixedWriter(f);
        var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
        var old = f.Read().Inputs[review.Execution.Inputs];
        var stale = new MaterializationPlan.Refresh(key, new(Id(300)), old.Bindings, InputMaterial.Sources(f.Read(), old.Bindings), old.Review);
        Assert.Equal("StaleInput", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(stale))).Reason.Problem.ToString());
        var invalid = stale with { Launch = new(review.Execution.Launch.Attempt, 3) };
        Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(invalid))).Reason.Problem.ToString());
        var wrongFix = await f.Prepare(T, cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 2, 0)), prompt: "Fix.");
        Assert.Equal("InvalidClaim", Assert.IsType<Preparation.Rejected>(wrongFix).Reason.Problem.ToString());
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == "journal.refresh-plan.after") throw new Crash(); })
            .PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review again."));
        var refreshed = f.Read().Inputs.Values.Single(input => input.Task == U && input.Id != old.Id);
        var candidate = review.Execution with { Launch = key, Inputs = refreshed.Id, Location = review.Execution.Location with { AttemptBase = refreshed.CodeBase } };
        Assert.Equal("InputConflict", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Prepared(candidate, SharedRefs))).Reason.Problem.ToString());
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review again."));
        Assert.Equal(repaired.Id, f.Read().Inputs[ready.Execution.Inputs].Review!.SubjectResult);
        Assert.Equal(first.Id, f.Read().Inputs[old.Id].Review!.SubjectResult);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }


    private static async Task<Preparation.Ready> FixedFanIn(PreparationFixture f)
    {
        await f.Publish(T, f.A);
        await f.Publish(D, f.A, "Independent.\n");
        var forwarded = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        await f.Close(forwarded, "Forwarded.\n");
        Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(), forwarded.Execution.Launch.Attempt,
            forwarded.Execution.Inputs, "Forwarded.\n"));
        var review = Assert.IsType<Preparation.Ready>(await f.Materializer(new RefreshComposer(f)).Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial(), "Review."));
        await CloseReviewTurn(f, review);
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 1, 0)), prompt: "Fix."));
        f.Git.Write("a.txt", "Fixed\n", fix.Checkout);
        await f.Close(fix, "Repaired.\n");
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(fix.Execution.Location.Owner.Task), f.Op(), fix.Execution.Launch.Attempt));
        return review;
    }

    private static Workflow FanInWorkflow() => Connect(Connect(Connect(
        FixtureWorkflow(Writer(T), Writer(D), Task(C), Reviewer()), D, C), C, U), T, U);

    [Fact]
    public async Task Reviewer_refresh_with_distinct_code_sources_records_join_required_without_a_refresh()
    {
        using var f = new PreparationFixture(FanInWorkflow());
        var review = await FixedFanIn(f);
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Materializer().PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), f.Op(),
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("JoinRequired", blocked.Block.Problem.ToString());
        Assert.Equal(U, blocked.Block.Task);
        Assert.Equal("00000000-0000-0000-0000-000000000109", blocked.Block.Attempt?.Value.ToString("D"));
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
        Assert.Equal(0, f.Read().Preparations.Keys.Count(key => key.Attempt == review.Execution.Launch.Attempt && key.Turn == 2));
        Assert.Single(f.Read().Blocks);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
        Assert.Equal("76e13b8982291f82ffbee6a1302464dedddae3f5", f.Git.Run(review.Checkout, "rev-parse", "HEAD").Text.Trim());
        Assert.Equal("e5d322528d8df1838e6e71d591820ac3f283a0ae", Assert.IsType<CodeOutput.Produced>(f.Read().CurrentResults[T].Code).Code.Commit.Hex);
    }

    [Fact]
    public async Task Reviewer_refresh_accepts_verified_join_and_forwards_its_exact_snapshot()
    {
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Writer(T), Writer(D), Task(C), Reviewer(), Writer(new(Id(6)))),
            D, C), C, U), T, U), U, new(Id(6)));
        using var f = new PreparationFixture(workflow);
        var review = await FixedFanIn(f);
        var operation = f.Op();
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer(new RefreshComposer(f)).PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), operation,
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("767f6c4b2e37787915d125cafad11d34f8620668", ready.Execution.Location.AttemptBase.Hex);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("approved\n", File.ReadAllText(Path.Combine(ready.Checkout, "plan.txt")));
        Assert.Equal("refs/idp/93f23689/pin/c67f2fc3/00000000-0000-0000-0000-000000000109/1/capture-1\n" +
            "refs/idp/93f23689/pin/c67f2fc3/00000000-0000-0000-0000-000000000109/1/capture-2\n" +
            "refs/idp/93f23689/pin/c67f2fc3/00000000-0000-0000-0000-000000000109/1/root\n" +
            "refs/idp/93f23689/resalvage/c67f2fc3/00000000-0000-0000-0000-000000000109/3e518864-e0d7-82d3-9b24-f6c4cb9835c6",
            f.Git.Git("for-each-ref", "--contains", "76e13b8982291f82ffbee6a1302464dedddae3f5", "--format=%(refname)").Trim());
        Assert.Equal("A\n", f.Git.Git("show", "76e13b8982291f82ffbee6a1302464dedddae3f5:a.txt"));
        var refresh = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
        Assert.Equal("767f6c4b2e37787915d125cafad11d34f8620668", refresh.Composed?.Commit.Hex);
        Assert.Equal(new[] { "e5d322528d8df1838e6e71d591820ac3f283a0ae", "3e473e4c6ad86aa4bf4f56d444ca398d8acb4572" }, refresh.Sources.Select(source => source.Commit.Hex));
        var input = f.Read().Inputs[ready.Execution.Inputs];
        Assert.Equal("767f6c4b2e37787915d125cafad11d34f8620668", Assert.IsType<CodeSelection.Joined>(input.Code).Join.Commit.Hex);
        Assert.Equal(T, input.Review!.Subject);
        Assert.Equal(1, ready.Execution.Prompt.Split("Repaired.", StringSplitOptions.None).Length - 1);
        Assert.Equal(ready, Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(ready.Execution.Location.Owner.Task), operation,
            ready.Execution.Launch, "Review the fix.")));
        Assert.Equal(new CodeOutput.Forwarded(input.Id), (await Agree(f, ready)).Code);
        var successor = Assert.IsType<Preparation.Ready>(await f.Prepare(new(Id(6))));
        var successorInput = f.Read().Inputs[successor.Execution.Inputs];
        var source = Assert.IsType<CodeSelection.Single>(successorInput.Code).Source;
        Assert.Equal("767f6c4b2e37787915d125cafad11d34f8620668", source.Commit.Hex);
        Assert.Equal(new[] { "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000005" }, source.Owners.Select(owner => owner.ToString()));
        Assert.Contains("Owners: 00000000-0000-0000-0000-000000000002, 00000000-0000-0000-0000-000000000005\n", successorInput.Text);
        Assert.DoesNotContain("Owner: 00000000-0000-0000-0000-000000000003", successorInput.Text);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("tree")]
    [InlineData("parents")]
    [InlineData("receipt")]
    [InlineData("ref")]
    public async Task Reviewer_refresh_rejects_a_join_with_changed_sources_tree_parents_receipt_or_ref(string mode)
    {
        using var f = new PreparationFixture(FanInWorkflow());
        var review = await FixedFanIn(f);
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Materializer(new RefreshComposer(f, mode)).PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), f.Op(),
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
        Assert.Equal("76e13b8982291f82ffbee6a1302464dedddae3f5", f.Git.Run(review.Checkout, "rev-parse", "HEAD").Text.Trim());
    }

    [Fact]
    public async Task Every_join_refresh_probe_recovers_the_verified_join_and_one_prepared_turn()
    {
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(FanInWorkflow()))
        {
            var reviewer = await FixedFanIn(baseline);
            Assert.IsType<Preparation.Ready>(await baseline.Materializer(new RefreshComposer(baseline), steps.Add)
                .PrepareTurn(baseline.Lease(reviewer.Execution.Location.Owner.Task), baseline.Op(), new(reviewer.Execution.Launch.Attempt, 2), "Review the fix."));
        }
        Assert.Contains("git.refresh-reset.after", steps);
        Assert.Contains("git.refresh-branch.after", steps);
        Assert.Contains("git.refresh-retain.after", steps);
        Assert.Contains("git.refresh-capture.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(FanInWorkflow());
            var review = await FixedFanIn(f);
            var operation = f.Op();
            var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(new RefreshComposer(f), step => { if (step == point) throw new Crash(); })
                .PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review the fix."));
            var ready = Assert.IsType<Preparation.Ready>(await f.Materializer(new RefreshComposer(f)).PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation,
                key, "Review the fix."));
            Assert.Equal("767f6c4b2e37787915d125cafad11d34f8620668", ready.Execution.Location.AttemptBase.Hex);
            Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
            Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
            Assert.Single(f.Read().Preparations, pair => pair.Key == key);
        }
    }

    private sealed class RefreshComposer(PreparationFixture f, string mode = "valid") : IJoinComposer
    {
        public ValueTask<JoinOutcome> Compose(JoinRequest request, CancellationToken cancellation)
        {
            if (f.Read().Plans.GetValueOrDefault(request.Operation) is MaterializationPlan.Join persisted)
                return ValueTask.FromResult<JoinOutcome>(new JoinOutcome.Ready(new(request.Operation, persisted.Sources, persisted.Commit,
                    persisted.Recipe.Tree, persisted.Ref)));
            var parents = request.Sources.Select(source => source.Commit).Distinct().ToArray();
            var merged = f.Git.Git("merge-tree", "--write-tree", parents[0].Hex, parents[1].Hex).Trim();
            var recipe = new CommitRecipe(new(merged), [.. parents], "join\n", "E2 <e2@example.test>", "E2 <e2@example.test>", At);
            var actual = mode == "parents" ? recipe with { Parents = [.. parents.Reverse()] } : recipe;
            if (mode == "tree") actual = recipe with { Tree = GitFixture.Read(f.Git.Open().ReadCommit(f.A)).Tree };
            var commit = GitFixture.Read(f.Git.Open().CreateCommit(actual));
            var record = f.Read();
            var reference = $"refs/heads/idp/{record.RunKey}/join/{record.TaskKeys[request.Task]}";
            var plan = new MaterializationPlan.Join(request.Task, request.Inputs, request.Sources, recipe, commit, request.ExpectedJoin, reference);
            if (mode != "receipt")
            {
                Assert.IsType<RunDecision.Recorded>(f.Store.Record(request.Permit, request.Operation, new RunEvent.Planned(plan)));
                if (new RefPublisher(f.Store).Publish(request.Permit, request.Operation, request.Operation,
                    "join", f.Git.Open(), new(reference, request.ExpectedJoin, commit)) is not RefPublication.Completed)
                    throw new InvalidOperationException("Join publication failed.");
                if (mode == "ref") f.Git.Git("update-ref", reference, f.A.Hex);
            }
            var sources = mode == "sources" ? request.Sources.Reverse().ToImmutableArray() : request.Sources;
            return ValueTask.FromResult<JoinOutcome>(new JoinOutcome.Ready(new(request.Operation, sources, commit, new(merged), reference)));
        }
    }

    [Fact]
    public async Task Every_refresh_probe_recovers_one_input_and_prepared_turn()
    {
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(Workflow()))
        {
            var (review, _, _, _) = await FixedWriter(baseline);
            Assert.IsType<Preparation.Ready>(await baseline.Materializer(probe: steps.Add).PrepareTurn(baseline.Lease(review.Execution.Location.Owner.Task), baseline.Op(),
                new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        }
        Assert.Contains("git.refresh-reset.after", steps);
        Assert.Contains("git.refresh-branch.after", steps);
        Assert.Contains("git.refresh-retain.after", steps);
        Assert.Contains("git.refresh-capture.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(Workflow());
            var (review, _, _, repaired) = await FixedWriter(f);
            var operation = f.Op();
            var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review the fix."));
            var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review the fix."));
            Assert.Equal(Fixed, ready.Execution.Location.AttemptBase.Hex);
            Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
            Assert.Equal(repaired.Id, f.Read().Inputs[ready.Execution.Inputs].Review!.SubjectResult);
            Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
            Assert.Single(f.Read().Preparations, pair => pair.Key == key);
            Assert.Equal(1, ready.Execution.Prompt.Split("Repaired.", StringSplitOptions.None).Length - 1);
            Assert.Equal(ready, Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation,
                key, "Review the fix.")));
        }
    }

    [Fact]
    public async Task A_reviewer_commit_is_preserved_and_blocks_refresh()
    {
        using var f = new PreparationFixture(Workflow());
        var (review, _, _, _) = await FixedWriter(f);
        f.Git.Write("note.txt", "reviewer note\n", review.Checkout);
        Assert.Equal(0, f.Git.Run(review.Checkout, "add", "note.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(review.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "note.txt").ExitCode);
        Assert.Equal("ad71d2166357a1557cf40534643d9bf04099cfe9", f.Git.Run(review.Checkout, "rev-parse", "HEAD").Text.Trim());
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Materializer().PrepareTurn(f.Lease(review.Execution.Location.Owner.Task), f.Op(),
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("The task branch differs from its journaled state.", blocked.Block.Detail);
        Assert.Equal("reviewer note\n", File.ReadAllText(Path.Combine(review.Checkout, "note.txt")));
        Assert.Equal("ad71d2166357a1557cf40534643d9bf04099cfe9", GitFixture.Read(f.Git.Open().ReadRef(review.Execution.Location.Owner.Branch))?.Hex);
    }

    [Theory]
    [InlineData("journal.refresh-reset-intent.after")]
    [InlineData("git.refresh-branch.after")]
    public async Task Refresh_resumed_under_a_foreign_head_blocks_and_keeps_the_foreign_branch(string point)
    {
        using var f = new PreparationFixture(Workflow());
        var (review, _, _, _) = await FixedWriter(f);
        var operation = f.Op();
        var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review the fix."));
        f.Git.Git("branch", "foreign", "81ddb7c330112c7f16700ed002803a04b0bce693");
        Assert.Equal(0, f.Git.Run(review.Checkout, "symbolic-ref", "HEAD", "refs/heads/foreign").ExitCode);
        var blocked = Assert.IsType<Preparation.Blocked>(await f.Materializer().PrepareTurn(f.Lease(f.Read().Attempts[key.Attempt].Task), operation, key, "Review the fix."));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("Registration, common directory, symbolic HEAD and recorded worktree owner do not agree.", blocked.Block.Detail);
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", GitFixture.Read(f.Git.Open().ReadRef("refs/heads/foreign"))?.Hex);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
    }
}
