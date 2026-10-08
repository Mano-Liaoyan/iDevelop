using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.TestSupport.FakeAgents;
using AsyncTask = System.Threading.Tasks.Task;

namespace IDevelop.Core.Tests;

public sealed class ProjectRunOwnershipTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _processEvidence;

    public ProjectRunOwnershipTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _processEvidence = Path.Combine(_temp.Create("evidence"), "process.txt");
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).RecordWorkingDirectory(_processEvidence)
            .Print(SessionLine(ClientId.Codex, "session-1")).Print(ReplyLines(ClientId.Codex, "Done.")));
    }

    [Fact]
    public async AsyncTask A_standalone_run_cannot_take_a_run_owned_task_between_its_turns_and_stall_the_owner()
    {
        var task = Task(T, model: "gpt-6-sol") with { Conversation = ConversationMode.Chat };
        var unrelated = Task(U, model: "gpt-6-sol");
        using var f = new RunFixtures(Edit(FixtureWorkflow(task), new WorkflowEdit.Rename("Delivery")));
        f.Approve();
        var reserved = f.Reserve();
        f.Claim(reserved);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), new(A1, 1),
            f.WriteLog(reserved, report: "More?", conversation: ConversationMode.Chat)));
        Assert.False(f.Read().Closures.ContainsKey(A1));
        f.ReleaseControl();
        var sequence = f.Read().Sequence;
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());

        Assert.Equal(new StartResult.Refused(new StartProblem.RunOwned("Delivery")), runs.Start(task));
        Assert.Equal(new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.RunOwned("Delivery"))),
            await runs.SendAsync(task, "Continue.", stopTurn: false));
        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.Blocked(new StartProblem.RunOwned("Delivery"))),
            runs.OpenInTerminal(T));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", T.ToString())));
        Assert.Empty(runs.Active);
        Assert.False(File.Exists(_processEvidence));
        Assert.Equal(sequence, f.Read().Sequence);

        var control = await StartAndSettle(runs, unrelated);
        Assert.Equal((U, AttemptStatus.Succeeded, "Done."), (control.Task, control.Status, control.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", U.ToString())));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [Fact]
    public async AsyncTask A_durable_reservation_refuses_standalone_start_after_its_process_releases_the_task_lock()
    {
        var task = Task(T, model: "gpt-6-sol");
        var unrelated = Task(U, model: "gpt-6-sol");
        using var f = new RunFixtures(Edit(FixtureWorkflow(task), new WorkflowEdit.Rename("Delivery")));
        f.Approve();
        var release = Path.Combine(f.Project, "release");
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());
        using (var racer = new Racer("reserve", f.Project, W.ToString(), Run.ToString(), T.ToString(), release))
        {
            Assert.Equal("Created@3", await racer.Line());
            Assert.Equal(new StartResult.Refused(new StartProblem.RunInAnotherWindow()), runs.Start(task));
            File.WriteAllText(release, "release");
            await racer.Exit();
        }

        Assert.Equal(new StartResult.Refused(new StartProblem.RunOwned("Delivery")), runs.Start(task));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", T.ToString())));
        Assert.False(File.Exists(_processEvidence));
        Assert.Equal(3, f.Read().Sequence);
        var control = await StartAndSettle(runs, unrelated);
        Assert.Equal((U, AttemptStatus.Succeeded, "Done."), (control.Task, control.Status, control.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", U.ToString())));
        Assert.Equal(3, f.Read().Sequence);
    }

    [Fact]
    public async AsyncTask An_approved_task_without_a_plan_or_reservation_refuses_standalone_start()
    {
        var task = Task(T, model: "gpt-6-sol");
        var unrelated = Task(U, model: "gpt-6-sol");
        using var f = new RunFixtures(Edit(FixtureWorkflow(task), new WorkflowEdit.Rename("Delivery")));
        f.Approve();
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());

        Assert.Equal(new StartResult.Refused(new StartProblem.RunOwned("Delivery")), runs.Start(task));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", T.ToString())));
        Assert.False(File.Exists(_processEvidence));
        Assert.Equal(1, f.Read().Sequence);
        var control = await StartAndSettle(runs, unrelated);
        Assert.Equal((U, AttemptStatus.Succeeded, "Done."), (control.Task, control.Status, control.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", U.ToString())));
        Assert.Equal(1, f.Read().Sequence);
    }

    [Fact]
    public async AsyncTask An_abandoned_attempt_with_an_unresolved_claim_still_owns_its_task()
    {
        var task = Task(T, model: "gpt-6-sol");
        var unrelated = Task(U, model: "gpt-6-sol");
        using var f = new RunFixtures(Edit(FixtureWorkflow(task), new WorkflowEdit.Rename("Delivery")));
        f.Approve();
        f.Claim(f.Reserve());
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(f.Permit, f.Op(), f.Op(), "Abandoned."));
        f.ReleaseControl();
        var sequence = f.Read().Sequence;
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());

        Assert.Equal(new StartResult.Refused(new StartProblem.RunOwned("Delivery")), runs.Start(task));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", T.ToString())));
        Assert.False(File.Exists(_processEvidence));
        Assert.Equal(sequence, f.Read().Sequence);
        var control = await StartAndSettle(runs, unrelated);
        Assert.Equal((U, AttemptStatus.Succeeded, "Done."), (control.Task, control.Status, control.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", U.ToString())));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [Fact]
    public async AsyncTask A_closed_attempt_in_a_settled_run_does_not_own_its_task()
    {
        var task = Task(T, model: "gpt-6-sol");
        using var f = new RunFixtures(FixtureWorkflow(task));
        f.Approve();
        f.Claim(f.Reserve());
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1,
            RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Stopped));
        Assert.Equal(RunPhase.Stopped, f.Read().Phase);
        Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[A1]).Outcome);
        f.ReleaseControl();
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());

        var started = await StartAndSettle(runs, task);
        Assert.Equal((T, AttemptStatus.Succeeded, "Done."), (started.Task, started.Status, started.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
    }

    [Fact]
    public async AsyncTask An_abandoned_run_does_not_own_its_unclosed_task()
    {
        var task = Task(T, model: "gpt-6-sol");
        using var f = new RunFixtures(FixtureWorkflow(task));
        f.Approve();
        f.Reserve();
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(f.Permit, f.Op(), f.Op(), "Abandoned."));
        Assert.Equal(RunPhase.Abandoned, f.Read().Phase);
        Assert.False(f.Read().Closures.ContainsKey(A1));
        f.ReleaseControl();
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());

        var started = await StartAndSettle(runs, task);
        Assert.Equal((T, AttemptStatus.Succeeded, "Done."), (started.Task, started.Status, started.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"schema\":3")]
    public async AsyncTask A_journal_without_a_complete_entry_does_not_block_a_standalone_start(string contents)
    {
        var task = Task(T, model: "gpt-6-sol");
        using var f = new RunFixtures(FixtureWorkflow(Task(U, model: "gpt-6-sol")));
        f.Approve();
        var journal = f.Journal(new(Id(20)), OtherRun);
        File.WriteAllText(journal, contents);
        var bytes = File.ReadAllBytes(journal);
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());

        var started = await StartAndSettle(runs, task);
        Assert.Equal((T, AttemptStatus.Succeeded, "Done."), (started.Task, started.Status, started.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.Project, ".idp", "attempts", T.ToString())));
        Assert.Equal(bytes, File.ReadAllBytes(journal));
        Assert.Equal(1, f.Read().Sequence);
    }

    [Fact]
    public async AsyncTask An_unreadable_journal_refuses_with_its_storage_problem_and_releases_the_standalone_lock()
    {
        var task = Task(T, model: "gpt-6-sol");
        using var f = new RunFixtures(FixtureWorkflow(task));
        var journal = f.Journal(new(Id(20)), OtherRun);
        File.WriteAllText(journal, "broken\n");
        await using var runs = ProjectRuns.Open(f.Project, await _fakes.DiscoverAsync());

        Assert.Equal(new StartResult.Refused(new StartProblem.CannotRecord(
            $"iDevelop could not read this project's workflow runs. {Path.GetRelativePath(f.Project, journal)}: InvalidData.")), runs.Start(task));
        Assert.False(File.Exists(_processEvidence));
        Assert.Empty(runs.Active);
        using (var lease = StandaloneLease.TryTake(f.Project, T)) Assert.NotNull(lease);
        File.Delete(journal);
        var started = await StartAndSettle(runs, task);
        Assert.Equal((T, AttemptStatus.Succeeded, "Done."), (started.Task, started.Status, started.Result));
        Assert.Equal(Folders.AsCurrentFolder(f.Project), File.ReadAllText(_processEvidence));
    }

    private static async System.Threading.Tasks.Task<AttemptRecord> StartAndSettle(ProjectRuns runs, TaskDefinition task)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            if (runs.Latest.GetValueOrDefault(task.Id) is { Status: not AttemptStatus.Running } record && runs.Active.IsEmpty)
                settled.TrySetResult(record);
        }
        runs.Changed += OnChanged;
        try
        {
            Assert.IsType<StartResult.Started>(runs.Start(task));
            return await settled.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { runs.Changed -= OnChanged; }
    }

    public void Dispose() => _temp.Dispose();
}
