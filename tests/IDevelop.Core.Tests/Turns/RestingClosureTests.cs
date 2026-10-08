using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

/// <summary>Mark done, a review's conclusion and cancellation of a resting attempt, and Mark done's publication (E3a.5b).</summary>
public sealed class RestingClosureTests
{
    private static Workflow WithU(Workflow workflow) => Edit(workflow, TestNodes.Place(Agent(U), new(0, 0)));

    /// <summary>A chat writer whose first turn wrote result.txt and now waits for the person.</summary>
    private static async Task<SettledTurn> Waiting(TurnFixture f)
    {
        var turn = await f.Settled(await f.Start());
        Assert.Equal("WaitingForInput", turn.Attempt.Status.ToString());
        Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(turn.Release()).Receipt);
        return turn;
    }

    /// <summary>An attempt of <paramref name="task"/> whose one turn ended in review, closed with its matched capture.</summary>
    private static async Task<AttemptId> InReview(TurnFixture f, TaskId task)
    {
        var p = f.Preparation;
        var ready = Assert.IsType<Preparation.Ready>(await p.Prepare(task));
        var execution = ready.Execution;
        var attempt = p.Read().Attempts[execution.Launch.Attempt];
        var inputs = p.Read().Inputs[execution.Inputs];
        Assert.IsType<RunDecision.Granted>(p.Store.Claim(p.Lease(task), p.Op(), execution.Launch, inputs, execution.PromptHash));
        var definition = p.Read().Revision.Snapshot.Tasks[task];
        var folder = p.Store.AttemptFolder(W, p.RunId, task, attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
            new AttemptEvent.Requested(At, attempt.Id, task, definition.Title, definition.Execution!, execution.Prompt, "codex", [])
            { RunBinding = new(W, p.RunId, attempt.Revision, inputs.Id), Subject = C, Conversation = definition.Conversation }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("review")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Changes requested.")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }
        Assert.IsType<RootObservation.Observed>(p.Materializer().ObserveRootExit(p.Lease(task), p.Op(), execution.Launch, new RootExit.Exited(0)));
        Assert.IsType<CaptureDisposition.Matched>(Assert.IsType<Settlement.Closed>(await p.Materializer().Settle(p.Lease(task), p.Op(),
            execution.Launch, Checkpoint(folder))).Disposition);
        Assert.Equal("InReview", AttemptEvidence.Read(folder).Record!.Status.ToString());
        p.Release(task);
        return attempt.Id;
    }

    private static Task<RestingClose> Close(TurnFixture f, OperationId operation, AttemptId attempt, RestingEnd end) =>
        f.Runs.CloseResting(f.Preparation.Permit, operation, attempt, end).WaitAsync(Bound);

    private static string Folder(TurnFixture f, TaskId task, AttemptId attempt) => f.Preparation.Store.AttemptFolder(W, f.Preparation.RunId, task, attempt);

    private static int Lines(TurnFixture f, TaskId task, AttemptId attempt, string type) =>
        File.ReadAllLines(Path.Combine(Folder(f, task, attempt), "events.jsonl")).Count(line => line.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal));

    private static int Closures(TurnFixture f, AttemptId attempt) =>
        f.Preparation.Read().Receipts.Values.Count(entry => entry.Event is RunEvent.AttemptClosed closed && closed.Attempt == attempt);

    [Theory]
    [InlineData("published")]
    [InlineData("late")]
    [InlineData("extra")]
    public async Task Mark_done_publishes_the_final_capture(string row)
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var turn = await Waiting(f);
        var attempt = turn.Address.Launch.Attempt;
        if (row == "late")
        {
            File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "late\n");
            var blocked = Assert.IsType<RestingClose.Blocked>(await Close(f, f.Preparation.Op(), attempt, new RestingEnd.MarkDone())).Block;
            Assert.Equal("DirtyWorktree", blocked.Problem.ToString());
            Assert.Equal(new BlockScope.Checkout(["result.txt"]), blocked.Scope);
            Assert.Equal(0, Closures(f, attempt));
            Assert.Equal(0, Lines(f, T, attempt, "markedDone"));
            Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
            return;
        }
        if (row == "extra")
        {
            // Any line besides the one Mark done leaves the closure log apart from the captured turn.
            using (var log = AttemptLog.Open(Folder(f, T, attempt)))
            {
                log.Append(new AttemptEvent.MessageQueued(At, "Also this.", false) { Id = "extra" });
                log.Append(new AttemptEvent.MarkedDone(At));
            }
            Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(), attempt,
                TerminalAttemptOutcome.Succeeded, Checkpoint(Folder(f, T, attempt))));
            var refused = Assert.IsType<Publication.Rejected>(f.Preparation.Materializer().Publish(f.Preparation.Lease(T), f.Preparation.Op(), attempt));
            Assert.Equal("OutcomeMismatch", refused.Reason.Problem.ToString());
            Assert.Empty(f.Preparation.Read().Results);
            return;
        }
        var closed = Assert.IsType<RestingClose.Closed>(await Close(f, f.Preparation.Op(), attempt, new RestingEnd.MarkDone())).Attempt;
        Assert.Equal("Succeeded", Assert.IsType<AttemptEnd.Logged>(closed.End).Outcome.ToString());
        Assert.Equal("NotSettled", Assert.IsType<Release.Held>(closed.Release()).Reason.Problem.ToString());
        Assert.IsType<LeaseTake.Busy>(f.Preparation.Permit.TakeTask(T));
        var published = f.Preparation.Materializer().Publish(closed.Lease, f.Preparation.Op(), attempt);
        var result = Assert.IsType<Publication.Accepted>(published).Result;
        Assert.Equal("done\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Equal(result.Id, Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(closed.Release()).Receipt).Result);
        Assert.Single(f.Preparation.Read().Results);
        Assert.Equal(1, f.Launches);
        Assert.Equal(1, Lines(f, T, attempt, "markedDone"));
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
    }

    [Theory]
    [InlineData("MarkDone")]
    [InlineData("Conclude")]
    [InlineData("Cancel")]
    public async Task Resting_closure_converges_after_its_log_append(string end)
    {
        await using var f = new TurnFixture(end == "MarkDone" ? ConversationMode.Chat : ConversationMode.Autonomous);
        await f.Open(end == "Cancel" ? f.Waiting() : null);
        AttemptId attempt;
        if (end == "Conclude") attempt = await InReview(f, T);
        else if (end == "MarkDone") attempt = (await Waiting(f)).Address.Launch.Attempt;
        else
        {
            var running = await f.Start();
            await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1");
            Assert.IsType<SendResult.Queued>(await running.SendAsync("Use the fixture", true).WaitAsync(Bound));
            var stopped = await f.Settled(running);
            Assert.Equal("Running", Assert.IsType<TurnDisposition.Resting>(Assert.IsType<Release.Released>(stopped.Release()).Receipt).Status.ToString());
            attempt = stopped.Address.Launch.Attempt;
        }
        RestingEnd closing = end switch
        {
            "MarkDone" => new RestingEnd.MarkDone(),
            "Conclude" => new RestingEnd.Conclude("Needs work."),
            _ => new RestingEnd.Cancel(),
        };
        var operation = f.Preparation.Op();
        await f.CloseCrash(operation, attempt, end, end == "Conclude" ? "Needs work." : null, "runner.close.append.after");
        var type = end switch { "MarkDone" => "markedDone", "Conclude" => "concluded", _ => "cancelRequested" };
        Assert.Equal(1, Lines(f, T, attempt, type));
        Assert.Equal(0, Closures(f, attempt));
        var closed = Assert.IsType<RestingClose.Closed>(await Close(f, operation, attempt, closing)).Attempt;
        Assert.Equal(1, Lines(f, T, attempt, type));
        Assert.Equal(1, Closures(f, attempt));
        Assert.Equal(end switch { "MarkDone" => "Succeeded", "Conclude" => "Failed", _ => "Cancelled" },
            Assert.IsType<AttemptEnd.Logged>(closed.End).Outcome.ToString());
        Assert.Same(closed, Assert.IsType<RestingClose.Closed>(await Close(f, operation, attempt, closing)).Attempt);
        Assert.Equal("OutcomeMismatch", Assert.IsType<RestingClose.Refused>(await Close(f, f.Preparation.Op(), attempt,
            new RestingEnd.Conclude("Other."))).Reason.Problem.ToString());
        Assert.Equal(1, Closures(f, attempt));
        Assert.Equal(end == "Conclude" ? 0 : 1, f.Launches);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("stopped")]
    [InlineData("drift")]
    [InlineData("review")]
    public async Task Cancel_and_conclusion_close_resting_attempts(string row)
    {
        await using var f = new TurnFixture(row is "waiting" or "drift" ? ConversationMode.Chat : ConversationMode.Autonomous);
        await f.Open(row == "stopped" ? f.Waiting() : null);
        AttemptId attempt;
        if (row == "review") attempt = await InReview(f, T);
        else if (row == "stopped")
        {
            var running = await f.Start();
            await WaitUntilAsync(() => f.Log(running.Address.Launch).Record?.SessionId == "session-1");
            Assert.IsType<SendResult.Queued>(await running.SendAsync("Use the fixture", true).WaitAsync(Bound));
            var stopped = await f.Settled(running);
            Assert.IsType<Release.Released>(stopped.Release());
            attempt = stopped.Address.Launch.Attempt;
        }
        else attempt = (await Waiting(f)).Address.Launch.Attempt;
        if (row == "drift") File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "late\n");
        var closed = Assert.IsType<RestingClose.Closed>(await Close(f, f.Preparation.Op(), attempt,
            row == "review" ? new RestingEnd.Conclude(null) : new RestingEnd.Cancel())).Attempt;
        Assert.Equal(row == "review" ? "Succeeded" : "Cancelled", Assert.IsType<AttemptEnd.Logged>(closed.End).Outcome.ToString());
        Assert.Equal(row == "review" ? "Succeeded" : "Cancelled", AttemptEvidence.Read(Folder(f, T, attempt)).Record!.Status.ToString());
        Assert.Empty(f.Preparation.Read().Results);
        var drift = f.Preparation.Read().Blocks.Values.Where(state => !state.Resolved).ToArray();
        if (row == "drift")
        {
            var block = Assert.Single(drift).Block;
            Assert.Equal(attempt, block.Attempt);
            Assert.Equal(new BlockScope.Checkout(["result.txt"]), block.Scope);
        }
        else Assert.Empty(drift);
        if (row != "review") Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(closed.Release()).Receipt);
        Assert.Equal(row == "review" ? 0 : 1, f.Launches);
        Assert.Equal(row == "review" ? 1 : 1, f.Claims(T));
    }

    [Theory]
    [InlineData("MarkDone")]
    [InlineData("Conclude")]
    public async Task A_closure_needs_the_rest_it_ends(string end)
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var attempt = end == "MarkDone" ? await InReview(f, T) : (await Waiting(f)).Address.Launch.Attempt;
        var before = File.ReadAllBytes(Path.Combine(Folder(f, T, attempt), "events.jsonl"));
        var refused = Assert.IsType<RestingClose.Refused>(await Close(f, f.Preparation.Op(), attempt,
            end == "MarkDone" ? new RestingEnd.MarkDone() : new RestingEnd.Conclude(null)));
        Assert.Equal("InvalidClaim", refused.Reason.Problem.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Folder(f, T, attempt), "events.jsonl")));
        Assert.Equal(0, Closures(f, attempt));
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
    }

    [Theory]
    [InlineData("same")]
    [InlineData("later")]
    [InlineData("turn")]
    public async Task A_failed_check_clears_when_a_later_check_succeeds(string row)
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var attempt = (await Waiting(f)).Address.Launch.Attempt;
        var branch = f.Preparation.Read().Preparations[new(attempt, 1)].Location.Owner.Branch;
        var tip = f.Preparation.Git.Git("rev-parse", branch).Trim();
        f.Preparation.Git.Git("branch", "foreign", tip);
        f.Preparation.Git.Git("symbolic-ref", branch, "refs/heads/foreign");
        var operation = f.Preparation.Op();
        var blocked = Assert.IsType<RestingClose.Blocked>(await Close(f, operation, attempt, new RestingEnd.MarkDone())).Block;
        Assert.Equal("UncertainOwnership", blocked.Problem.ToString());
        Assert.Equal(new BlockScope.Operation(), blocked.Scope);
        Assert.Equal(0, Closures(f, attempt));
        f.Preparation.Git.Git("update-ref", "--no-deref", branch, tip);
        if (row == "turn")
        {
            // A later turn's own-baseline recheck verifies what the failed closure check could not.
            FakeAgents.Install(f.Fakes, f.Client, FakeAgents.Resuming(f.Client, "session-1")
                .Print(FakeAgents.SessionLine(f.Client, "session-1")).Print(FakeAgents.ReplyLines(f.Client, "More?")));
            var next = await f.Settled(await f.Start(new TurnIntent.Next(f.Preparation.Op(), new(attempt, 2), "Use the fixture")));
            Assert.Equal("WaitingForInput", next.Attempt.Status.ToString());
            Assert.DoesNotContain(f.Preparation.Read().Blocks.Values, state => !state.Resolved);
            Assert.Equal(2, f.Launches);
            return;
        }
        var closed = Assert.IsType<RestingClose.Closed>(await Close(f, row == "same" ? operation : f.Preparation.Op(), attempt,
            new RestingEnd.MarkDone())).Attempt;
        Assert.Equal("Succeeded", Assert.IsType<AttemptEnd.Logged>(closed.End).Outcome.ToString());
        Assert.DoesNotContain(f.Preparation.Read().Blocks.Values, state => !state.Resolved);
        Assert.Equal(1, Closures(f, attempt));
        Assert.IsType<Publication.Accepted>(f.Preparation.Materializer().Publish(closed.Lease, f.Preparation.Op(), attempt));
        Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(closed.Release()).Receipt);
    }

    [Theory]
    [InlineData("MarkDone")]
    [InlineData("Conclude")]
    public async Task An_earlier_drift_block_holds_mark_done_and_conclusion(string end)
    {
        await using var f = new TurnFixture(end == "MarkDone" ? ConversationMode.Chat : ConversationMode.Autonomous);
        await f.Open();
        var attempt = end == "MarkDone" ? (await Waiting(f)).Address.Launch.Attempt : await InReview(f, T);
        RestingEnd closing = end == "MarkDone" ? new RestingEnd.MarkDone() : new RestingEnd.Conclude(null);
        var file = Path.Combine(f.Checkout, "root.txt");
        var original = File.ReadAllText(file);
        File.WriteAllText(file, "late\n");
        var first = Assert.IsType<RestingClose.Blocked>(await Close(f, f.Preparation.Op(), attempt, closing)).Block;
        Assert.Equal(new BlockScope.Checkout(["root.txt"]), first.Scope);
        // Reverting the file outside iDevelop is no recorded recheck, so the block still holds the closure.
        File.WriteAllText(file, original);
        Assert.Equal(first, Assert.IsType<RestingClose.Blocked>(await Close(f, f.Preparation.Op(), attempt, closing)).Block);
        Assert.Equal(0, Closures(f, attempt));
        Assert.Single(f.Preparation.Read().Blocks);
    }

    [Fact]
    public async Task Another_operations_fault_holds_mark_done_as_it_would_hold_publication()
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var attempt = (await Waiting(f)).Address.Launch.Attempt;
        var other = f.Preparation.Op();
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.Record(f.Preparation.Permit, other, new RunEvent.Blocked(
            new(other, T, attempt, MaterializationProblem.UncertainOwnership, null, [], "Another step failed.") { Scope = new BlockScope.Operation() })));
        var blocked = Assert.IsType<RestingClose.Blocked>(await Close(f, f.Preparation.Op(), attempt, new RestingEnd.MarkDone())).Block;
        Assert.Equal(other, blocked.Operation);
        Assert.Equal(0, Closures(f, attempt));
        Assert.Equal(0, Lines(f, T, attempt, "markedDone"));
    }

    [Theory]
    [InlineData("MarkDone")]
    [InlineData("Cancel")]
    public async Task An_unfinished_git_step_stops_mark_done_but_not_cancel(string end)
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var attempt = (await Waiting(f)).Address.Launch.Attempt;
        var record = f.Preparation.Read();
        var prepared = record.Preparations[new(attempt, 1)];
        var plan = record.Plans.Single(pair => pair.Value is MaterializationPlan.Preparation preparation && preparation.Attempt == attempt).Key;
        // A worktree step that never recorded its observation, as a crash in another preparation of this checkout leaves.
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.Record(f.Preparation.Permit, f.Preparation.Op(), new RunEvent.GitIntended(plan,
            new GitMutation.CreateWorktree(prepared.Location.Owner, prepared.Location.AttemptBase, true))));
        var result = await Close(f, f.Preparation.Op(), attempt, end == "MarkDone" ? new RestingEnd.MarkDone() : new RestingEnd.Cancel());
        if (end == "MarkDone")
        {
            Assert.Equal("UnresolvedOwnership", Assert.IsType<RestingClose.Refused>(result).Reason.Problem.ToString());
            Assert.Equal(0, Closures(f, attempt));
            Assert.Equal(0, Lines(f, T, attempt, "markedDone"));
        }
        else Assert.Equal("Cancelled", Assert.IsType<AttemptEnd.Logged>(Assert.IsType<RestingClose.Closed>(result).Attempt.End).Outcome.ToString());
        Assert.Empty(f.Preparation.Read().Blocks);
    }

    [Fact]
    public async Task A_resting_closure_binds_its_operation_by_content()
    {
        await using var f = new TurnFixture(ConversationMode.Chat, configure: WithU);
        await f.Open();
        var waiting = (await Waiting(f)).Address.Launch.Attempt;
        var review = await InReview(f, U);
        var reused = f.Preparation.Op();
        var marked = Assert.IsType<RestingClose.Closed>(await Close(f, reused, waiting, new RestingEnd.MarkDone())).Attempt;
        Assert.IsType<Publication.Accepted>(f.Preparation.Materializer().Publish(marked.Lease, f.Preparation.Op(), waiting));
        Assert.IsType<Release.Released>(marked.Release());
        // Conclude without a failure ends as Succeeded too, but its closing event differs from Mark done's.
        Assert.Equal("OutcomeMismatch", Assert.IsType<RestingClose.Refused>(await Close(f, f.Preparation.Op(), waiting,
            new RestingEnd.Conclude(null))).Reason.Problem.ToString());
        var again = Assert.IsType<RestingClose.Closed>(await Close(f, f.Preparation.Op(), waiting, new RestingEnd.MarkDone())).Attempt;
        Assert.Equal(marked.End, again.End);
        Assert.IsType<TurnDisposition.Published>(Assert.IsType<Release.Released>(again.Release()).Receipt);
        Assert.Equal(1, Closures(f, waiting));
        Assert.Equal(1, Lines(f, T, waiting, "markedDone"));

        await f.Reopen();
        var before = File.ReadAllBytes(Path.Combine(Folder(f, U, review), "events.jsonl"));
        Assert.Equal("OperationConflict", Assert.IsType<RestingClose.Refused>(await Close(f, reused, review,
            new RestingEnd.Conclude("Needs work."))).Reason.Problem.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Folder(f, U, review), "events.jsonl")));
        Assert.Equal(0, Closures(f, review));
        var failed = Assert.IsType<RestingClose.Closed>(await Close(f, f.Preparation.Op(), review, new RestingEnd.Conclude("Needs work."))).Attempt;
        Assert.Equal("Failed", Assert.IsType<AttemptEnd.Logged>(failed.End).Outcome.ToString());
        Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(failed.Release()).Receipt);
        Assert.Equal("OutcomeMismatch", Assert.IsType<RestingClose.Refused>(await Close(f, f.Preparation.Op(), review,
            new RestingEnd.Conclude("Other."))).Reason.Problem.ToString());
        var same = Assert.IsType<RestingClose.Closed>(await Close(f, f.Preparation.Op(), review, new RestingEnd.Conclude("Needs work."))).Attempt;
        Assert.Equal(failed.End, same.End);
        Assert.IsType<Release.Released>(same.Release());
        Assert.Equal(1, Closures(f, review));
        Assert.Equal(1, Lines(f, U, review, "concluded"));
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("journal")]
    public async Task Faulted_and_refused_closures_converge_in_the_same_window(string row)
    {
        await using var f = new TurnFixture(ConversationMode.Chat);
        await f.Open();
        var attempt = (await Waiting(f)).Address.Launch.Attempt;
        var operation = f.Preparation.Op();
        FileStream? held = null;
        f.Runs.Probe = point =>
        {
            if (row == "fault" && point == "runner.close.append.after") throw new InvalidOperationException("The closure stopped.");
            if (row == "journal" && point == "runner.close.attempt.before") held = f.LockJournal();
        };
        if (row == "fault")
            await Assert.ThrowsAsync<InvalidOperationException>(() => Close(f, operation, attempt, new RestingEnd.Cancel()));
        else
        {
            Assert.Equal("JournalBusy", Assert.IsType<RestingClose.Refused>(await Close(f, operation, attempt, new RestingEnd.Cancel()))
                .Reason.Problem.ToString());
            held!.Dispose();
        }
        Assert.Equal(1, Lines(f, T, attempt, "cancelRequested"));
        Assert.Equal(0, Closures(f, attempt));
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
        f.Runs.Probe = null;
        var closed = Assert.IsType<RestingClose.Closed>(await Close(f, operation, attempt, new RestingEnd.Cancel())).Attempt;
        Assert.Equal("Cancelled", Assert.IsType<AttemptEnd.Logged>(closed.End).Outcome.ToString());
        Assert.Equal(1, Lines(f, T, attempt, "cancelRequested"));
        Assert.Equal(1, Closures(f, attempt));
        Assert.IsType<TurnDisposition.Ended>(Assert.IsType<Release.Released>(closed.Release()).Receipt);
    }

    [Fact]
    public async Task Release_accepts_a_preservation_receipt()
    {
        await using var f = new TurnFixture();
        await f.Open();
        f.Runs.Probe = point =>
        {
            if (point == "journal.capture-1.after") File.WriteAllText(Path.Combine(f.Checkout, "result.txt"), "late\n");
        };
        var turn = await f.Settled(await f.Start());
        Assert.IsType<CaptureDisposition.Diverged>(turn.Capture.Disposition);
        Assert.Equal("NotSettled", Assert.IsType<Release.Held>(turn.Release()).Reason.Problem.ToString());
        var preservation = f.Preparation.Op();
        Assert.IsType<Preservation.Preserved>(await f.Preparation.Materializer().Preserve(turn.Lease, preservation, turn.Address.Launch.Attempt));
        // Preserve records the drift it found as a block on the attempt, so the preservation must answer before that block.
        Assert.Contains(f.Preparation.Read().Blocks.Values, state => !state.Resolved && state.Block.Attempt == turn.Address.Launch.Attempt);
        var receipt = Assert.IsType<TurnDisposition.Preserved>(Assert.IsType<Release.Released>(turn.Release()).Receipt);
        Assert.Equal(OperationIds.Derive(preservation, "preserve-plan"), receipt.Plan);
        Assert.IsType<LeaseTake.Taken>(f.Preparation.Permit.TakeTask(T)).Lease.Dispose();
    }
}
