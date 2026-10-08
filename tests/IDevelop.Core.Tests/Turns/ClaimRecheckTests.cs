using System.Text;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

/// <summary>The rechecks a claim runs before it is recorded, and the append-time guard behind them (E3a.5b).</summary>
public sealed class ClaimRecheckTests
{
    private const string Review = "Review the changes.";

    private static Workflow Consumer(Workflow workflow) => Connect(Edit(workflow, TestNodes.Place(Agent(U), new(0, 0))), T, U);

    private static Workflow Context(Workflow workflow) => Connect(Edit(workflow, TestNodes.Place(Agent(U), new(0, 0))), T, U, ConnectionKind.Context);

    private static TaskDefinition Reviewer() => new(C, new Blueprint(new("example.review", 1), "Review",
        new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")), [], new(Task().Execution, ConversationMode.Autonomous)))
    { Title = "Review" };

    // T writes, the review C forwards T's code, and D consumes the review's result.
    private static Workflow Forwarded(Workflow workflow) => Connect(Connect(Edit(Edit(workflow,
        TestNodes.Place(Reviewer(), new(0, 0))), TestNodes.Place(Agent(D), new(0, 0))), T, C), C, D);

    private static TurnIntent.First Start(TurnFixture f, TaskId task, OperationId? operation = null) =>
        new(operation ?? f.Preparation.Op(), task, new AttemptCause.Initial());

    private static async Task<TurnStart> Begin(TurnFixture f, TurnIntent intent) =>
        await f.Runs.StartTurn(f.Preparation.Permit, intent).WaitAsync(Bound);

    private static async Task<SettledTurn> Published(TurnFixture f)
    {
        var producer = await f.Settled(await f.Start());
        f.Publish(producer);
        Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(producer.Release()).Receipt);
        return producer;
    }

    private static int Unresolved(TurnFixture f) => f.Preparation.Read().Blocks.Values.Count(block => !block.Resolved);

    /// <summary>The review C reads T's published code in one turn and agrees, so its result forwards T's code.</summary>
    private static async Task<ResultRecord> Agree(TurnFixture f)
    {
        var p = f.Preparation;
        var ready = Assert.IsType<Preparation.Ready>(await p.Prepare(C, prompt: Review));
        var execution = ready.Execution;
        var attempt = p.Read().Attempts[execution.Launch.Attempt];
        var inputs = p.Read().Inputs[execution.Inputs];
        Assert.IsType<RunDecision.Granted>(p.Store.Claim(p.Lease(C), p.Op(), execution.Launch, inputs, execution.PromptHash));
        var definition = p.Read().Revision.Snapshot.Tasks[C];
        var folder = p.Store.AttemptFolder(W, p.RunId, C, attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
            new AttemptEvent.Requested(At, attempt.Id, C, definition.Title, definition.Execution!, execution.Prompt, "codex", [])
            { RunBinding = new(W, p.RunId, attempt.Revision, inputs.Id), ReadOnly = true, Subject = T, Conversation = definition.Conversation }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("review")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Approved.\n")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
            log.Append(new AttemptEvent.Concluded(At, null));
        }
        Assert.IsType<RootObservation.Observed>(p.Materializer().ObserveRootExit(p.Lease(C), p.Op(), execution.Launch, new RootExit.Exited(0)));
        Assert.IsType<Settlement.Closed>(await p.Materializer().Settle(p.Lease(C), p.Op(), execution.Launch, Checkpoint(folder)));
        Assert.IsType<RunDecision.Recorded>(p.Store.CloseAttempt(p.Permit, p.Op(), attempt.Id, TerminalAttemptOutcome.Succeeded, Checkpoint(folder)));
        var result = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(p.Store.AcceptReport(p.Permit, p.Op(),
            attempt.Id, execution.Inputs, "Approved.\n")).Event).Result;
        p.Release(C);
        return result;
    }

    /// <summary>A retry of <paramref name="task"/> whose claim is recorded and never settles.</summary>
    private static async Task<LaunchKey> UnsettledRetry(TurnFixture f, TaskId task, AttemptId previous, string? prompt = null)
    {
        var p = f.Preparation;
        var retry = Assert.IsType<Preparation.Ready>(await p.Prepare(task, cause: new AttemptCause.Retry(previous, p.Op()), prompt: prompt));
        Assert.IsType<RunDecision.Granted>(p.Store.Claim(p.Lease(task), p.Op(), retry.Execution.Launch,
            p.Read().Inputs[retry.Execution.Inputs], retry.Execution.PromptHash));
        return retry.Execution.Launch;
    }

    [Fact]
    public async Task Drift_after_accepted_publication_blocks_the_consumer_first_claim()
    {
        await using var f = new TurnFixture(configure: Consumer);
        await f.Open();
        var producer = await Published(f);
        var attempt = producer.Address.Launch.Attempt;
        var result = Assert.Single(f.Preparation.Read().Results);
        File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "late\n");
        var intent = Start(f, U);
        var block = Assert.IsType<TurnStart.Blocked>(await Begin(f, intent)).Block;
        Assert.Equal("DirtyWorktree", block.Problem.ToString());
        Assert.Equal(new BlockScope.Checkout(["result.txt"]), block.Scope);
        Assert.Equal(T, block.Task);
        Assert.Equal(attempt, block.Attempt);
        Assert.Equal(1, f.Launches);
        Assert.Equal(0, f.Claims(U));
        var drift = Assert.Single(f.Preparation.Read().Blocks, pair => !pair.Value.Resolved);
        Assert.Equal(attempt, drift.Value.Block.Attempt);
        var duplicate = Assert.IsType<Publication.Accepted>(f.Preparation.Materializer().Publish(f.Preparation.Lease(T), f.Preparation.Op(), attempt));
        Assert.Equal(result.Id, duplicate.Result.Id);
        Assert.False(f.Preparation.Read().Blocks[drift.Key].Resolved);
        Assert.Single(f.Preparation.Read().Results);
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));

        var preservation = f.Preparation.Op();
        Assert.IsType<Preservation.Preserved>(await f.Preparation.Materializer().Preserve(f.Preparation.Lease(T), preservation, attempt));
        var preview = Assert.IsType<RestorePreviewRead.Previewed>(f.Preparation.Materializer()
            .PreviewRestore(f.Preparation.Lease(T), attempt, preservation)).Preview;
        Assert.IsType<Restoration.Restored>(f.Preparation.Materializer().Restore(f.Preparation.Lease(T), f.Preparation.Op(), attempt,
            preservation, f.Preparation.Op(), preview.Identity));
        f.Preparation.Release(T);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Checkout, "result.txt")));
        Assert.Equal(0, Unresolved(f));
        FakeAgents.Install(f.Fakes, f.Client, FakeAgents.Fresh(f.Client)
            .Copy("result.txt", Path.Combine(f.Evidence, "consumer.txt"))
            .Print(FakeAgents.SessionLine(f.Client, "session-2"))
            .Print(FakeAgents.ReplyLines(f.Client, "Done.")));
        var consumer = await f.Settled(Assert.IsType<TurnStart.Started>(await Begin(f, intent)).Turn);
        Assert.Equal("Succeeded", consumer.Attempt.Status.ToString());
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Evidence, "consumer.txt")));
        Assert.Equal(2, f.Launches);
        Assert.Equal(1, f.Claims(U));
    }

    [Fact]
    public async Task Repeated_consumer_starts_return_the_same_drift_block()
    {
        await using var f = new TurnFixture(configure: workflow => Connect(Edit(Consumer(workflow), TestNodes.Place(Agent(D), new(0, 0))), T, D));
        await f.Open();
        await Published(f);
        File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "late\n");
        var intent = Start(f, U);
        var first = Assert.IsType<TurnStart.Blocked>(await Begin(f, intent)).Block;
        var id = Assert.Single(f.Preparation.Read().Blocks).Key;
        Assert.Equal(first, Assert.IsType<TurnStart.Blocked>(await Begin(f, intent)).Block);
        // Another consumer of the same producer gets the recorded block, not a second one.
        Assert.Equal(first, Assert.IsType<TurnStart.Blocked>(await Begin(f, Start(f, D))).Block);
        Assert.Equal(id, Assert.Single(f.Preparation.Read().Blocks).Key);
        Assert.Equal(0, f.Claims(U));
        Assert.Equal(0, f.Claims(D));
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task Forwarded_code_is_rechecked_at_its_original_owner()
    {
        await using var f = new TurnFixture(configure: Forwarded);
        await f.Open();
        var producer = await Published(f);
        var review = await Agree(f);
        Assert.IsType<CodeOutput.Forwarded>(review.Code);
        File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "late\n");
        var block = Assert.IsType<TurnStart.Blocked>(await Begin(f, Start(f, D))).Block;
        Assert.Equal(T, block.Task);
        Assert.Equal(producer.Address.Launch.Attempt, block.Attempt);
        Assert.Equal(new BlockScope.Checkout(["result.txt"]), block.Scope);
        Assert.DoesNotContain(f.Preparation.Read().Blocks.Values, state => state.Block.Task == C);
        Assert.Equal(1, Unresolved(f));
        Assert.Equal(0, f.Claims(D));
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task A_code_owner_still_settling_holds_back_the_consumer()
    {
        await using var f = new TurnFixture(configure: Forwarded);
        await f.Open();
        var producer = await Published(f);
        await Agree(f);
        await UnsettledRetry(f, T, producer.Address.Launch.Attempt);
        // The retry still runs and changes its checkout, which is no drift to record against the published attempt.
        File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "retry\n");
        var refused = Assert.IsType<TurnStart.Refused>(await Begin(f, Start(f, D)));
        Assert.Equal("UnresolvedOwnership", refused.Reason.Problem.ToString());
        Assert.Empty(f.Preparation.Read().Blocks);
        Assert.Equal(0, f.Claims(D));
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task A_code_owner_with_an_unfinished_publication_holds_back_the_consumer()
    {
        await using var f = new TurnFixture(configure: Consumer);
        await f.Open();
        var producer = await Published(f);
        FakeAgents.Install(f.Fakes, f.Client, FakeAgents.Fresh(f.Client)
            .Print(FakeAgents.SessionLine(f.Client, "session-2"))
            .Write("result.txt", "retry\n")
            .Print(FakeAgents.ReplyLines(f.Client, "Done.")));
        var retry = await f.Settled(await f.Start(new TurnIntent.First(f.Preparation.Op(), T,
            new AttemptCause.Retry(producer.Address.Launch.Attempt, f.Preparation.Op()))));
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(),
            retry.Address.Launch.Attempt, TerminalAttemptOutcome.Succeeded, retry.Log));
        // The retry's publication stops after it recorded the branch move it intends.
        Assert.Throws<InvalidOperationException>(() => f.Preparation.Materializer(probe: point =>
        {
            if (point == "journal.branch-intent.after") throw new InvalidOperationException("Stopped.");
        }).Publish(retry.Lease, f.Preparation.Op(), retry.Address.Launch.Attempt));
        Assert.Single(f.Preparation.Read().Results);
        Assert.Equal("UnresolvedOwnership", Assert.IsType<TurnStart.Refused>(await Begin(f, Start(f, U))).Reason.Problem.ToString());
        Assert.Empty(f.Preparation.Read().Blocks);
        Assert.Equal(0, f.Claims(U));
        Assert.Equal(2, f.Launches);
    }

    [Fact]
    public async Task An_unsettled_forwarding_reviewer_holds_back_the_consumer()
    {
        await using var f = new TurnFixture(configure: Forwarded);
        await f.Open();
        await Published(f);
        var review = await Agree(f);
        await UnsettledRetry(f, C, Assert.IsType<ResultOrigin.Executed>(review.Origin).Attempt, Review);
        Assert.Equal("UnresolvedOwnership", Assert.IsType<TurnStart.Refused>(await Begin(f, Start(f, D))).Reason.Problem.ToString());
        Assert.Empty(f.Preparation.Read().Blocks);
        Assert.Equal(0, f.Claims(D));
    }

    [Theory]
    [InlineData(ConnectionKind.Dependency)]
    [InlineData(ConnectionKind.Context)]
    public async Task A_report_only_producer_still_settling_holds_back_the_consumer(ConnectionKind kind)
    {
        await using var f = new TurnFixture(readOnly: true, configure: kind == ConnectionKind.Dependency ? Consumer : Context);
        await f.Open(FakeAgents.Fresh(ClientId.Codex)
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.\n")));
        var producer = await f.Settled(await f.Start());
        var attempt = producer.Address.Launch.Attempt;
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(), attempt,
            TerminalAttemptOutcome.Succeeded, producer.Log));
        Assert.IsType<RunDecision.Created>(f.Preparation.Store.AcceptReport(f.Preparation.Permit, f.Preparation.Op(), attempt,
            producer.Preparation.Inputs, producer.Attempt.Result!));
        Assert.IsType<Release.Released>(producer.Release());
        await UnsettledRetry(f, T, attempt);
        var start = await Begin(f, Start(f, U));
        if (kind == ConnectionKind.Dependency)
        {
            Assert.Equal("UnresolvedOwnership", Assert.IsType<TurnStart.Refused>(start).Reason.Problem.ToString());
            Assert.Equal(0, f.Claims(U));
            Assert.Equal(1, f.Launches);
        }
        else
        {
            await f.Settled(Assert.IsType<TurnStart.Started>(start).Turn);
            Assert.Equal(1, f.Claims(U));
            Assert.Equal(2, f.Launches);
        }
        Assert.Empty(f.Preparation.Read().Blocks);
    }

    [Fact]
    public async Task The_claim_guard_holds_against_a_block_recorded_after_the_recheck()
    {
        await using var f = new TurnFixture(configure: Consumer);
        await f.Open();
        var producer = await Published(f);
        var recorded = 0;
        // After the rechecks and before the claim's transaction, so only the append-time guard can see this block.
        f.Runs.Probe = point =>
        {
            if (point != "journal.claim.before" || Interlocked.Increment(ref recorded) != 1) return;
            var operation = f.Preparation.Op();
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.Record(f.Preparation.Permit, operation, new RunEvent.Blocked(
                new(operation, T, producer.Address.Launch.Attempt, MaterializationProblem.DirtyWorktree, null, [], "Late drift.")
                { Scope = new BlockScope.Checkout(["result.txt"]) })));
        };
        var refused = Assert.IsType<TurnStart.Refused>(await Begin(f, Start(f, U)));
        Assert.Equal("UnresolvedOwnership", refused.Reason.Problem.ToString());
        Assert.Equal(1, recorded);
        Assert.Equal(0, f.Claims(U));
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public void The_claim_guard_is_append_only()
    {
        var workflow = Connect(FixtureWorkflow(Task(T), Task(U)), T, U);
        using var f = new RunFixtures(workflow);
        var bytes = File.ReadAllBytes(Fixture.Path("e3a5b-guarded-claim/events.jsonl"));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        var record = f.Read();
        Assert.Equal(15L, record.Sequence);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(string.Concat(RunJournal.Decode(bytes).Entries.Select(RunJournal.Encode))));
        var consumer = Assert.Single(record.Attempts.Values, attempt => attempt.Task == U);
        Assert.True(record.Claims.ContainsKey(new(consumer.Id, 1)));
        Assert.True(RunReducer.ProducerHold(record, record.Inputs[consumer.InitialInputs]));

        using var fresh = new RunFixtures(workflow);
        fresh.Approve();
        var producer = fresh.Reserve(T);
        fresh.Complete(producer, "A ready.\n");
        Assert.IsType<RunDecision.Recorded>(fresh.Store.Record(fresh.Permit, fresh.Op(), new RunEvent.Blocked(new(fresh.Op(), T,
            producer.Attempt.Id, MaterializationProblem.DirtyWorktree, null, [], "The producer checkout changed after its result was accepted.")
        { Scope = new BlockScope.Checkout(["result.txt"]) })));
        var reservation = fresh.Reserve(U);
        fresh.Prepare(reservation);
        Assert.Equal(RunProblem.UnresolvedOwnership, Problem(fresh.Store.Claim(fresh.Lease(U), fresh.Op(), new(reservation.Attempt.Id, 1),
            reservation.Inputs, Prompt)));
        Assert.DoesNotContain(fresh.Read().Claims.Keys, key => key.Attempt == reservation.Attempt.Id);
    }

    [Fact]
    public async Task Changed_delivered_inputs_block_the_first_claim()
    {
        await using var f = new TurnFixture(configure: Consumer);
        await f.Open();
        await Published(f);
        f.Runs.Probe = point =>
        {
            if (point != "runner.claim.before") return;
            var prepared = f.Preparation.Read().Preparations.Values.Single(p => p.Location.Owner.Task == U);
            var report = Directory.EnumerateFiles(Path.Combine(f.Preparation.Git.Folder, prepared.Location.Owner.RelativePath, ".idp", "inputs"),
                "report.md", SearchOption.AllDirectories).Single();
            File.WriteAllText(report, "Changed.\n");
        };
        var blocked = Assert.IsType<TurnStart.Blocked>(await Begin(f, Start(f, U))).Block;
        Assert.Equal(U, blocked.Task);
        Assert.Equal(0, f.Claims(U));
        Assert.Equal(1, f.Launches);
    }

    [Fact]
    public async Task Reserved_inputs_superseded_before_first_claim()
    {
        await using var f = new TurnFixture(readOnly: true, configure: workflow =>
            Connect(Edit(workflow, TestNodes.Place(Agent(U).WithField("brief", "Build B")!, new(0, 0))), T, U));
        await f.Open();
        var p = f.Preparation;
        var first = Assert.IsType<Preparation.Ready>(await p.Prepare(T));
        await p.Close(first, "A ready.\n");
        var accepted = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(p.Store.AcceptReport(p.Permit, p.Op(),
            first.Execution.Launch.Attempt, first.Execution.Inputs, "A ready.\n")).Event).Result;
        f.Runs.Probe = point =>
        {
            if (point != "runner.claim.before") return;
            var retry = Assert.IsType<Preparation.Ready>(p.Prepare(T, cause: new AttemptCause.Retry(first.Execution.Launch.Attempt, p.Op()))
                .AsTask().GetAwaiter().GetResult());
            p.Close(retry, "A2\n").GetAwaiter().GetResult();
            Assert.IsType<RunDecision.Created>(p.Store.AcceptReport(p.Permit, p.Op(), retry.Execution.Launch.Attempt, retry.Execution.Inputs,
                "A2\n", accepted.Id));
        };
        var refused = Assert.IsType<TurnStart.Refused>(await Begin(f, Start(f, U)));
        Assert.Equal("StaleInput", refused.Reason.Problem.ToString());
        Assert.Equal(0, f.Launches);
        Assert.Equal(0, f.Claims(U));
        Assert.Contains("Build B", Assert.Single(p.Read().Preparations.Values, prepared => prepared.Location.Owner.Task == U).Prompt);
    }

    [Theory]
    [InlineData("Next", false)]
    [InlineData("Next", true)]
    [InlineData("Continue", false)]
    [InlineData("Continue", true)]
    public async Task A_change_after_preparation_blocks_a_continuing_claim(string intent, bool indexOnly)
    {
        await using var f = new TurnFixture(intent == "Next" ? ConversationMode.Chat : ConversationMode.Autonomous);
        TurnIntent next;
        OperationId? preservation = null;
        if (intent == "Next")
        {
            await f.Open(FakeAgents.Fresh(f.Client)
                .Print(FakeAgents.SessionLine(f.Client, "session-1"))
                .Write("keep.txt", "keep\n")
                .Print(FakeAgents.ReplyLines(f.Client, "More?")));
            var waiting = await f.Settled(await f.Start());
            Assert.Equal("WaitingForInput", waiting.Attempt.Status.ToString());
            Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(waiting.Release()).Receipt);
            next = new TurnIntent.Next(f.Preparation.Op(), new(waiting.Address.Launch.Attempt, 2), "Use the fixture");
        }
        else
        {
            var (previous, confirmation, preserved) = await RecoveryLaunchTests.Baselined(f);
            preservation = preserved;
            next = new TurnIntent.First(f.Preparation.Op(), T, new AttemptCause.Continue(previous, confirmation));
        }
        var launches = f.Launches;
        f.Runs.Probe = point =>
        {
            if (point != "runner.claim.before") return;
            File.WriteAllText(Path.Combine(f.Checkout, "keep.txt"), "late\n");
            if (!indexOnly) return;
            Assert.Equal(0, f.Preparation.Git.Run(f.Checkout, "add", "keep.txt").ExitCode);
            File.WriteAllText(Path.Combine(f.Checkout, "keep.txt"), "keep\n");
        };
        var blocked = Assert.IsType<TurnStart.Blocked>(await Begin(f, next)).Block;
        Assert.Equal("DirtyWorktree", blocked.Problem.ToString());
        Assert.Equal(new[] { "keep.txt" }, Assert.IsType<BlockScope.Checkout>(blocked.Scope).Paths);
        var attempt = Assert.IsType<AttemptId>(blocked.Attempt);
        Assert.Equal(T, blocked.Task);
        Assert.Equal(intent == "Next" ? 1 : 0, f.Preparation.Read().Claims.Keys.Count(key => key.Attempt == attempt));
        Assert.Equal(launches, f.Launches);
        if (preservation is { } preserved2)
        {
            var commit = f.Preparation.Read().Preservations[OperationIds.Derive(preserved2, "preserve-plan")].Commit;
            Assert.Equal("keep\n", f.Preparation.Git.Git("show", commit.Hex + ":keep.txt"));
        }
        Assert.Equal(1, Unresolved(f));
        f.Runs.Probe = null;
        // Another operation for the same turn gets the recorded block, not a second one.
        if (next is TurnIntent.Next later)
            Assert.Equal(blocked, Assert.IsType<TurnStart.Blocked>(await Begin(f, later with { Operation = f.Preparation.Op() })).Block);
        Assert.Equal(1, Unresolved(f));
        Assert.Equal(launches, f.Launches);
    }
}
