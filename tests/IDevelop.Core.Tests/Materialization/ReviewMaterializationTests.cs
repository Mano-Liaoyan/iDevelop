using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
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
        CloseReviewTurn(f, review);
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 1, 0)), prompt: "Repair the finding."));
        f.Git.Write("a.txt", "Fixed\n", fix.Checkout);
        f.Close(fix, "Repaired.\n");
        var repaired = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), fix.Execution.Launch.Attempt)).Result;
        return (review, fix, result, repaired);
    }

    private static void CloseReviewTurn(PreparationFixture f, Preparation.Ready ready)
    {
        var execution = ready.Execution;
        var attempt = f.Read().Attempts[execution.Launch.Attempt];
        var inputs = f.Read().Inputs[execution.Inputs];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(W, f.RunId, f.Op(), execution.Launch, inputs, execution.PromptHash));
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
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(W, f.RunId, f.Op(), execution.Launch, Checkpoint(folder)));
    }

    private static ResultRecord Agree(PreparationFixture f, Preparation.Ready ready)
    {
        var attempt = f.Read().Attempts[ready.Execution.Launch.Attempt];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(W, f.RunId, f.Op(), ready.Execution.Launch,
            f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));
        var folder = f.Store.AttemptFolder(W, f.RunId, U, attempt.Id);
        using (var log = AttemptLog.Open(folder))
        {
            log.Append(new AttemptEvent.TurnRequested(At, ready.Execution.Prompt, "codex", []));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("review")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Approved.\n")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
            log.Append(new AttemptEvent.Concluded(At, null));
        }
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(W, f.RunId, f.Op(), attempt.Id, TerminalAttemptOutcome.Succeeded, Checkpoint(folder)));
        return Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(W, f.RunId, f.Op(),
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
        var refreshed = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(W, f.RunId, f.Op(),
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal(Fixed, refreshed.Execution.Location.AttemptBase.Hex);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(refreshed.Checkout, "a.txt")));
        var input = f.Read().Inputs[refreshed.Execution.Inputs];
        Assert.Equal(new ReviewInput(T, repaired.Id), input.Review);
        Assert.Equal(1, refreshed.Execution.Prompt.Split("Repaired.", StringSplitOptions.None).Length - 1);
        Assert.Equal("Repaired.\n", File.ReadAllText(Path.Combine(refreshed.Checkout, input.Files.Single().RelativePath)));
        Assert.Equal("B ready.\n", File.ReadAllText(Path.Combine(refreshed.Checkout, oldInput.Files.Single().RelativePath)));
        var agreement = Agree(f, refreshed);
        Assert.Equal(new CodeOutput.Forwarded(input.Id), agreement.Code);
        var successor = Assert.IsType<Preparation.Ready>(await f.Prepare(D));
        var single = Assert.IsType<CodeSelection.Single>(f.Read().Inputs[successor.Execution.Inputs].Code);
        Assert.Equal(U, single.Source.Task);
        Assert.Equal(T, single.Source.Owner);
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
        var block = Assert.IsType<Preparation.Blocked>(await f.Materializer().PrepareTurn(W, f.RunId, operation,
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("DirtyWorktree", block.Block.Problem.ToString());
        Assert.Equal(before.Inputs.Count, f.Read().Inputs.Count);
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
        Assert.Equal("reviewer edits\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
        Assert.Equal(First, GitFixture.Read(f.Git.Open().ReadRef(review.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal(first.Id, f.Read().Inputs[review.Execution.Inputs].Review!.SubjectResult);
        Assert.Equal(repaired.Id, f.Read().CurrentResults[T].Id);
        f.Git.Write("a.txt", "A\n", review.Checkout);
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(W, f.RunId, operation,
            new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Accepted_fix_marks_finished_consumers_stale_and_keeps_running_consumers_frozen()
    {
        var workflow = Connect(Connect(Connect(FixtureWorkflow(Writer(T), Reviewer(), Task(C), Writer(D)), T, U), T, C), T, D);
        using var f = new PreparationFixture(workflow);
        var first = await f.Publish(T, f.A);
        var consumer = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        f.Close(consumer, "Consumed.\n");
        var consumed = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(W, f.RunId, f.Op(),
            consumer.Execution.Launch.Attempt, consumer.Execution.Inputs, "Consumed.\n")).Event).Result;
        var running = Assert.IsType<Preparation.Ready>(await f.Prepare(D));
        var runningInput = f.Read().Inputs[running.Execution.Inputs];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(W, f.RunId, f.Op(), running.Execution.Launch, runningInput, running.Execution.PromptHash));
        var reviewer = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: "Review."));
        CloseReviewTurn(f, reviewer);
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, reviewer.Execution.Launch.Attempt, 1, 0)), prompt: "Fix."));
        f.Git.Write("a.txt", "Fixed\n", fix.Checkout);
        f.Close(fix, "Repaired.\n");
        var repaired = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), fix.Execution.Launch.Attempt)).Result;
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
        Assert.Equal("StaleInput", Assert.IsType<RunDecision.Rejected>(f.Store.Record(W, f.RunId, f.Op(), new RunEvent.Planned(stale))).Reason.Problem.ToString());
        var invalid = stale with { Launch = new(review.Execution.Launch.Attempt, 3) };
        Assert.Equal("InvalidClaim", Assert.IsType<RunDecision.Rejected>(f.Store.Record(W, f.RunId, f.Op(), new RunEvent.Planned(invalid))).Reason.Problem.ToString());
        var wrongFix = await f.Prepare(T, cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 2, 0)), prompt: "Fix.");
        Assert.Equal("InvalidClaim", Assert.IsType<Preparation.Rejected>(wrongFix).Reason.Problem.ToString());
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == "journal.refresh-plan.after") throw new Crash(); })
            .PrepareTurn(W, f.RunId, operation, key, "Review again."));
        var refreshed = f.Read().Inputs.Values.Single(input => input.Task == U && input.Id != old.Id);
        var candidate = review.Execution with { Launch = key, Inputs = refreshed.Id, Location = review.Execution.Location with { AttemptBase = refreshed.CodeBase } };
        Assert.Equal("InputConflict", Assert.IsType<RunDecision.Rejected>(f.Store.Record(W, f.RunId, f.Op(), new RunEvent.Prepared(candidate, SharedRefs))).Reason.Problem.ToString());
        var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(W, f.RunId, operation, key, "Review again."));
        Assert.Equal(repaired.Id, f.Read().Inputs[ready.Execution.Inputs].Review!.SubjectResult);
        Assert.Equal(first.Id, f.Read().Inputs[old.Id].Review!.SubjectResult);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Every_refresh_probe_recovers_one_input_and_prepared_turn()
    {
        var steps = new List<string>();
        using (var baseline = new PreparationFixture(Workflow()))
        {
            var (review, _, _, _) = await FixedWriter(baseline);
            Assert.IsType<Preparation.Ready>(await baseline.Materializer(probe: steps.Add).PrepareTurn(W, baseline.RunId, baseline.Op(),
                new(review.Execution.Launch.Attempt, 2), "Review the fix."));
        }
        Assert.Contains("git.refresh-reset.after", steps);
        foreach (var point in steps.Distinct())
        {
            using var f = new PreparationFixture(Workflow());
            var (review, _, _, repaired) = await FixedWriter(f);
            var operation = f.Op();
            var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
            await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step => { if (step == point) throw new Crash(); })
                .PrepareTurn(W, f.RunId, operation, key, "Review the fix."));
            var ready = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(W, f.RunId, operation, key, "Review the fix."));
            Assert.Equal(Fixed, ready.Execution.Location.AttemptBase.Hex);
            Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
            Assert.Equal(repaired.Id, f.Read().Inputs[ready.Execution.Inputs].Review!.SubjectResult);
            Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Refresh>());
            Assert.Single(f.Read().Preparations, pair => pair.Key == key);
            Assert.Equal(1, ready.Execution.Prompt.Split("Repaired.", StringSplitOptions.None).Length - 1);
            Assert.Equal(ready, Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(W, f.RunId, operation, key, "Review the fix.")));
        }
    }
}
