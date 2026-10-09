using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// Every ready task of a run starts at once (#89). Each task that meets others marks its start and waits until each of the
/// others has started too, so a run that started them one after another never ends; the attempt logs then show that their
/// running intervals overlap.
/// </summary>
public sealed class ConcurrencyTests
{
    private static string Started(CoordinatorFixture f, TaskId task) => Path.Combine(f.Evidence, $"{Name(task)}.started");

    /// <summary>A turn that marks its start, waits until each of <paramref name="others"/> has started, then writes and answers.</summary>
    private static FakeRule Meets(CoordinatorFixture f, TaskId task, params TaskId[] others) =>
        Meeting(f, task, others).Write($"{Name(task).ToLowerInvariant()}.txt", $"{Name(task)}\n").Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    /// <summary>As <see cref="Meets"/>, for a read-only task: it writes nothing.</summary>
    private static FakeRule MeetsReading(CoordinatorFixture f, TaskId task, params TaskId[] others) =>
        Meeting(f, task, others).Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    private static FakeRule Meeting(CoordinatorFixture f, TaskId task, TaskId[] others)
    {
        var rule = FakeRule.On().RecordArguments(Started(f, task)).Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task)));
        foreach (var other in others) rule = rule.WaitForFile(Started(f, other));
        return rule;
    }

    /// <summary>A turn that writes a partial file and waits for <paramref name="gate"/> before it answers.</summary>
    private static FakeRule Waits(TaskId task, string gate) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task)))
        .Write("partial.txt", "partial\n")
        .WaitForFile(gate)
        .Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    /// <summary>When the task's newest attempt launched its root and when the root exited, from the attempt's log.</summary>
    internal static (DateTimeOffset Launched, DateTimeOffset Exited) Interval(string folder)
    {
        var events = AttemptEvidence.Read(folder).Events;
        return (events.OfType<AttemptEvent.Launched>().Single().At, events.OfType<AttemptEvent.Exited>().Single().At);
    }

    private static (DateTimeOffset Launched, DateTimeOffset Exited) Interval(CoordinatorFixture f, TaskId task)
    {
        var record = f.Read();
        return Interval(f.Preparation.Store.AttemptFolder(Runs.RunFixtures.W, f.Preparation.RunId, task, RunProjection.LatestAttempts(record)[task]));
    }

    /// <summary>Each pair of intervals overlaps: each root launched before every other root exited.</summary>
    internal static void Overlap(params (DateTimeOffset Launched, DateTimeOffset Exited)[] intervals)
    {
        foreach (var first in intervals)
            foreach (var second in intervals.Where(other => other != first))
                Assert.True(first.Launched < second.Exited, $"A root launched at {first.Launched:O}, after another exited at {second.Exited:O}.");
    }

    /// <summary>The highest number of client roots the coordinator's projections showed starting or running at once.</summary>
    private sealed class SlotWatch : IDisposable
    {
        private readonly WorkflowRunCoordinator _coordinator;
        private int _most;
        private int _refusals;

        public SlotWatch(WorkflowRunCoordinator coordinator)
        {
            _coordinator = coordinator;
            coordinator.Changed += OnChanged;
        }

        public int Most => Volatile.Read(ref _most);

        /// <summary>How many projections showed a task whose start or step was refused.</summary>
        public int Refusals => Volatile.Read(ref _refusals);

        private void OnChanged(object? sender, EventArgs e)
        {
            var view = _coordinator.View;
            int most;
            do most = Volatile.Read(ref _most); while (view.Slots > most && Interlocked.CompareExchange(ref _most, view.Slots, most) != most);
            if (view.Tasks.Values.Any(task => task.State == TaskState.Refused)) Interlocked.Increment(ref _refusals);
        }

        public void Dispose() => _coordinator.Changed -= OnChanged;
    }

    [Fact]
    public async Task A_fan_out_starts_its_three_independent_tasks_at_once_and_their_runs_overlap()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, B), (A, C), (A, D)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Meets(f, B, C, D)).Answer(C, Meets(f, C, B, D)).Answer(D, Meets(f, D, B, C));
        await f.Open();
        using var slots = new SlotWatch(f.Coordinator);
        await f.Resume();
        var done = await f.UntilStatus(RunStatus.Completed);

        Assert.Equal(3, slots.Most);
        Assert.Equal(0, slots.Refusals);
        Overlap(Interval(f, B), Interval(f, C), Interval(f, D));
        Assert.True(Interval(f, A).Exited < new[] { B, C, D }.Min(task => Interval(f, task).Launched));
        Assert.Equal([1, 1, 1, 1], new[] { A, B, C, D }.Select(f.Launches));
        Assert.All(new[] { B, C, D }, task => Assert.Equal("A\n", f.ResultFile(task, "a.txt")));
        Assert.Equal(((string?)"B\n", (string?)null, (string?)null), (f.ResultFile(B, "b.txt"), f.ResultFile(B, "c.txt"), f.ResultFile(B, "d.txt")));
        Assert.All(done.Tasks.Values, task => Assert.Equal(TaskState.Done, task.State));
    }

    [Fact]
    public async Task A_diamond_runs_its_branches_together_and_its_join_waits_for_both()
    {
        await using var f = new CoordinatorFixture(Diamond());
        f.Answer(A, Meets(f, A, X)).Answer(B, Meets(f, B, C)).Answer(C, Meets(f, C, B)).Answer(X, MeetsReading(f, X, A))
            .Answer(D, FakeRule.On()
                .Print(FakeAgents.SessionLine(ClientId.Codex, "session-D"))
                .Copy("b.txt", Path.Combine(f.Evidence, "d-b.txt"))
                .Copy("c.txt", Path.Combine(f.Evidence, "d-c.txt"))
                .Print(FakeAgents.ReplyLines(ClientId.Codex, "D ready.\n")));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        Overlap(Interval(f, B), Interval(f, C));
        // X runs beside A, since it depends on nothing.
        Overlap(Interval(f, A), Interval(f, X));
        var record = f.Read();
        long Sequence(Func<RunEvent, bool> match) => record.Receipts.Values.Single(entry => match(entry.Event)).Sequence;
        long Published(TaskId task) => Sequence(e => e is RunEvent.ResultAccepted accepted && accepted.Result.Task == task);
        var joined = Sequence(e => e is RunEvent.TurnClaimed claim && record.Attempts[claim.Key.Attempt].Task == D);
        Assert.True(joined > Published(B) && joined > Published(C), "D was claimed before both branches handed on their results.");
        Assert.Equal(["B\n", "C\n"], new[] { "b", "c" }.Select(name => File.ReadAllText(Path.Combine(f.Evidence, $"d-{name}.txt"))));
        Assert.Contains("B ready.\n", f.Prompt(D));
        Assert.Contains("C ready.\n", f.Prompt(D));
        Assert.Equal([1, 1, 1, 1, 1], new[] { A, B, C, D, X }.Select(f.Launches));
    }

    [Fact]
    public async Task Below_the_bound_starts_follow_task_order_and_a_root_exit_starts_the_next()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D)]));
        f.Answer(B, Waits(B, Path.Combine(f.Evidence, "b"))).Answer(C, Waits(C, Path.Combine(f.Evidence, "c"))).Answer(D, Writes(D, "d.txt", "D\n"));
        await f.Open();
        f.Runs.ClientRoots = 2;
        using var slots = new SlotWatch(f.Coordinator);
        await f.Resume();
        var two = await f.Until(view => view.Tasks[B].State == TaskState.Running && view.Tasks[C].State == TaskState.Running);
        Assert.Equal(TaskState.Ready, two.Tasks[D].State);
        await TurnFixture.WaitUntilAsync(() => f.Launches(B) == 1 && f.Launches(C) == 1);
        Assert.Equal(TaskState.Ready, (await f.Decided()).Tasks[D].State);
        Assert.Equal(0, f.Launches(D));

        File.WriteAllText(Path.Combine(f.Evidence, "b"), "open");
        await f.Until(view => view.Tasks[B].State == TaskState.Done && view.Tasks[D].State == TaskState.Done);
        File.WriteAllText(Path.Combine(f.Evidence, "c"), "open");
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal(2, slots.Most);
        var record = f.Read();
        var exited = record.Receipts.Values.Single(entry => entry.Event is RunEvent.RootExitObserved exit && record.Attempts[exit.Launch.Attempt].Task == B).Sequence;
        var claimed = record.Receipts.Values.Single(entry => entry.Event is RunEvent.TurnClaimed claim && record.Attempts[claim.Key.Attempt].Task == D).Sequence;
        Assert.True(claimed > exited, "D was claimed before B's root exit was recorded.");
    }

    [Fact]
    public async Task Stop_while_three_run_cancels_each_and_starts_nothing_more()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, D)));
        var never = Path.Combine(f.Evidence, "never");
        f.Answer(A, Waits(A, never)).Answer(B, Waits(B, never)).Answer(C, Waits(C, never)).Answer(D, Writes(D, "d.txt", "D\n"));
        await f.Open();
        await f.Resume();
        var running = await f.Until(view => new[] { A, B, C }.All(task => view.Tasks[task].State == TaskState.Running));
        Assert.Equal((3, TaskState.Pending), (running.Slots, running.Tasks[D].State));
        await TurnFixture.WaitUntilAsync(() => new[] { A, B, C }.All(task => File.Exists(Path.Combine(f.Checkout(task), "partial.txt"))));

        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
        var stopped = await f.UntilStatus(RunStatus.Stopped);

        Assert.All(new[] { A, B, C }, task =>
            Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(stopped.Tasks[task].End).Outcome));
        Assert.All(new[] { A, B, C }, task => Assert.Equal("partial\n", File.ReadAllText(Path.Combine(f.Checkout(task), "partial.txt"))));
        Assert.Equal((0, TaskState.Pending), (stopped.Slots, stopped.Tasks[D].State));
        Assert.Equal([1, 1, 1, 0], new[] { A, B, C, D }.Select(f.Launches));
        Assert.Empty(f.Read().Results);
        Assert.Equal(3, f.Read().Closures.Count);
        Assert.Equal(RunPhase.Stopped, f.Read().Phase);
    }

    [Fact]
    public async Task Cancelling_one_of_three_running_tasks_stops_only_its_own_client()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C)]));
        var gate = Path.Combine(f.Evidence, "bc");
        f.Answer(A, Waits(A, Path.Combine(f.Evidence, "never"))).Answer(B, Waits(B, gate)).Answer(C, Waits(C, gate));
        await f.Open();
        await f.Resume();
        var running = await f.Until(view => new[] { A, B, C }.All(task => view.Tasks[task].State == TaskState.Running));
        await TurnFixture.WaitUntilAsync(() => new[] { A, B, C }.All(task => File.Exists(Path.Combine(f.Checkout(task), "partial.txt"))));

        var cancel = await f.Coordinator.Cancel(f.Address, A, new TurnKey(running.Tasks[A].Attempt!.Value, 1)).WaitAsync(Bound);
        Assert.Equal(CommandOutcome.Applied, cancel.Outcome);
        var one = await f.Until(view => view.Tasks[A].State == TaskState.Failed);
        Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(one.Tasks[A].End).Outcome);
        Assert.Equal((TaskState.Running, TaskState.Running), (one.Tasks[B].State, one.Tasks[C].State));

        File.WriteAllText(gate, "open");
        var rest = await f.Until(view => view.Tasks[B].State == TaskState.Done && view.Tasks[C].State == TaskState.Done);
        Assert.Equal(RunStatus.NeedsAttention, rest.Status);
        Assert.All(new[] { B, C }, task => Assert.Equal((string?)"partial\n", f.ResultFile(task, "partial.txt")));
        Assert.All(new[] { B, C }, task => Assert.True(Interval(f, A).Exited < Interval(f, task).Exited));
        Assert.Equal([1, 1, 1], new[] { A, B, C }.Select(f.Launches));
    }

    [Fact]
    public async Task A_crash_while_three_run_reopens_with_each_uncertain_and_launches_none_again()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, D)));
        var gate = Path.Combine(f.Evidence, "gate");
        f.Answer(A, Waits(A, gate)).Answer(B, Waits(B, gate)).Answer(C, Waits(C, gate)).Answer(D, Writes(D, "d.txt", "D\n"));
        await f.CrashRun(3);
        // Three roots run, each under its claim; a client may not have read its turn yet.
        Assert.Equal((3, 3, 0), (f.TotalLaunches, f.Read().Claims.Count, f.Launches(D)));
        // The clients outlive the crash, as Unix roots do; they end now, and nothing records their ends.
        File.WriteAllText(gate, "open");

        await f.Open();
        var paused = await f.Decided();
        Assert.Equal(RunStatus.Paused, paused.Status);
        Assert.All(new[] { A, B, C }, task => Assert.Equal(TaskState.Uncertain, paused.Tasks[task].State));
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention &&
            new[] { A, B, C }.All(task => view.Tasks[task] is { State: TaskState.Uncertain, Unresolved: UnresolvedReason.Uncertain }));
        Assert.Equal(TaskState.Pending, stuck.Tasks[D].State);
        Assert.Equal(0, stuck.Slots);
        Assert.Equal(3, f.TotalLaunches);
        Assert.Equal(3, f.Read().Claims.Count);

        var closures = new[] { A, B, C }.Select(task => f.Coordinator.ConfirmStopped(f.Address, task, stuck.Tasks[task].Attempt!.Value,
            "Gone after the crash.", f.Preparation.Op()).WaitAsync(Bound)).ToArray();
        Assert.All(await Task.WhenAll(closures), closure => Assert.IsType<RunCommand.Accepted>(closure));
        var closed = await f.Until(view => new[] { A, B, C }.All(task => view.Tasks[task].State == TaskState.Failed));
        Assert.All(new[] { A, B, C }, task =>
            Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(closed.Tasks[task].End).Outcome));
        Assert.Equal(RunStatus.NeedsAttention, (await f.UntilStatus(RunStatus.NeedsAttention)).Status);
        Assert.Equal((3, 0), (f.TotalLaunches, f.Launches(D)));
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task Two_publications_at_once_serialize_on_the_repository_and_the_join_takes_both()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, B), (A, C), (B, D), (C, D)));
        f.Answer(A, Writes(A, "a.txt", "A\n")).Answer(B, Meets(f, B, C)).Answer(C, Meets(f, C, B))
            .Answer(D, FakeRule.On()
                .Print(FakeAgents.SessionLine(ClientId.Codex, "session-D"))
                .Copy("b.txt", Path.Combine(f.Evidence, "d-b.txt"))
                .Copy("c.txt", Path.Combine(f.Evidence, "d-c.txt"))
                .Print(FakeAgents.ReplyLines(ClientId.Codex, "D ready.\n")));
        await f.Open();
        // Both branch publications come in at once. The first holds the repository for longer than a busy lock was once
        // waited for, while the second waits for the lock instead of being refused.
        // A publishes first, alone; B's and C's are the second and third.
        var publishing = new CountdownEvent(2);
        var locking = new CountdownEvent(2);
        int publications = 0, locks = 0, accepted = 0, held = 0;
        f.Runs.Probe = point =>
        {
            if (point == "coordinator.publish.before" && Interlocked.Increment(ref publications) is 2 or 3)
            {
                publishing.Signal();
                Assert.True(publishing.Wait(Bound), "The second branch's publication never came.");
            }
            else if (point == "publish.lock.before" && Interlocked.Increment(ref locks) is 2 or 3) locking.Signal();
            else if (point == "journal.accepted.before" && Interlocked.Increment(ref accepted) == 2)
            {
                Assert.True(locking.Wait(Bound), "The second branch's publication never asked for the repository.");
                held = 1;
                Thread.Sleep(TimeSpan.FromMilliseconds(1500));
            }
        };
        using var slots = new SlotWatch(f.Coordinator);
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal(1, held);
        Assert.Equal(0, slots.Refusals);
        Assert.DoesNotContain(f.Read().Blocks.Values, block => !block.Resolved);
        Overlap(Interval(f, B), Interval(f, C));
        Assert.Equal(["B\n", "C\n"], new[] { "b", "c" }.Select(name => File.ReadAllText(Path.Combine(f.Evidence, $"d-{name}.txt"))));
        Assert.Equal((string?)"A\n", f.ResultFile(D, "a.txt"));
        Assert.Equal([1, 1, 1, 1], new[] { A, B, C, D }.Select(f.Launches));
    }

    [Fact]
    public async Task A_person_s_command_hears_of_a_busy_repository_after_the_short_wait()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A, conversation: ConversationMode.Chat)]));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Tasks[A].State == TaskState.Waiting);
        var turn = new TurnKey(waiting.Tasks[A].Attempt!.Value, 1);
        ConversationCommandResult busy;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        using (var held = f.Preparation.Git.Open().TakeMutationLock())
        {
            Assert.NotNull(held);
            busy = await f.Coordinator.MarkDone(f.Address, A, turn).WaitAsync(Bound);
            waited.Stop();
        }

        // The run's own steps wait up to 30 seconds for the lock; a person's Mark done reports the busy repository instead.
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"Mark done waited {waited.Elapsed}.");
        Assert.Equal((CommandOutcome.Refused, WorkflowRunCoordinator.BusyMessage), (busy.Outcome, busy.Detail));
        Assert.Equal(CommandOutcome.Applied, (await f.Coordinator.MarkDone(f.Address, A, turn).WaitAsync(Bound)).Outcome);
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("A\n", f.ResultFile(A, "a.txt"));

        // The coordinator's own commands for a person, such as the rebase preview, wait as briefly.
        RebasePreviewRead preview;
        waited.Restart();
        using (var held = f.Preparation.Git.Open().TakeMutationLock())
        {
            Assert.NotNull(held);
            preview = await f.Coordinator.ReviewUpdatedInputs(f.Address, A).WaitAsync(Bound);
            waited.Stop();
        }
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"The preview waited {waited.Elapsed}.");
        Assert.Equal(RunProblem.JournalBusy, Assert.IsType<RebasePreviewRead.Rejected>(preview).Reason.Problem);
    }

    [Fact]
    public async Task Closing_the_project_ends_a_start_s_wait_for_the_repository()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]));
        f.Answer(A, Writes(A, "a.txt", "A\n"));
        await f.Open();
        var looked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runs.Probe = point => { if (point == "runner.lookup") looked.TrySetResult(); };
        TimeSpan closing;
        using (var held = f.Preparation.Git.Open().TakeMutationLock())
        {
            Assert.NotNull(held);
            await f.Resume();
            await looked.Task.WaitAsync(Bound);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await f.Close();
            closing = timer.Elapsed;
        }

        // The halted start stops waiting for the lock, so closing need not wait out the project's 5-second leave timeout.
        Assert.True(closing < TimeSpan.FromSeconds(3), $"Closing took {closing}.");
        Assert.Empty(f.Read().Attempts);
        await f.OpenAgain();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(A));
    }

    [Fact]
    public async Task Three_preparations_at_once_wait_for_the_repository_in_turn()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D)]));
        f.Answer(B, Meets(f, B, C, D)).Answer(C, Meets(f, C, B, D)).Answer(D, Meets(f, D, B, C));
        await f.Open();
        // The first preparation holds the repository for longer than a busy lock was once waited for.
        var looking = new CountdownEvent(3);
        var held = 0;
        f.Runs.Probe = point =>
        {
            if (point == "runner.lookup" && !looking.IsSet) looking.Signal();
            else if (point == "journal.reserve.before" && Interlocked.Exchange(ref held, 1) == 0)
            {
                Assert.True(looking.Wait(Bound), "Not every start came.");
                Thread.Sleep(TimeSpan.FromMilliseconds(1500));
            }
        };
        using var slots = new SlotWatch(f.Coordinator);
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal((1, 0, 3), (held, slots.Refusals, slots.Most));
        Overlap(Interval(f, B), Interval(f, C), Interval(f, D));
        Assert.Equal([1, 1, 1], new[] { B, C, D }.Select(f.Launches));
    }
}
