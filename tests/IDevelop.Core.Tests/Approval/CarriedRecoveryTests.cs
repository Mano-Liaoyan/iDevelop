using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Approval.CarriedResultTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using RunFixtures = IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>
/// Carrying earlier results survives a crash at each of its steps (#90): an approval that replayed, kept, or recorded them
/// finishes as one run when the person confirms again, and a node run while a run is active carries what its tasks need
/// once, however often it is repeated.
/// </summary>
public sealed class CarriedRecoveryTests
{
    private static readonly OperationId First = new(Guid.Parse("00000000-0000-0000-0000-0000000cc001"));
    private static readonly OperationId AfterRestart = new(Guid.Parse("00000000-0000-0000-0000-0000000cc002"));

    /// <summary>Confirms <paramref name="node"/>'s preview in a racer process, which exits at <paramref name="point"/>.</summary>
    private static async Task Crash(ApprovalFixture f, TaskId node, string point)
    {
        f.Install();
        var file = Path.Combine(f.Evidence, "workflow.json");
        File.WriteAllBytes(file, WorkflowFile.Serialize(f.Workflow));
        using var racer = new Racer(f.Git.Environment, "approve-crash", f.Project, file, f.Fakes.Folder, First.Value.ToString("D"), "Head", point,
            "node:" + node.Value.ToString("D"));
        Assert.Equal("Previewed", await racer.Line());
        Assert.Equal(point, await racer.Line());
        await racer.Exit();
        Assert.Equal(73, racer.ExitCode);
    }

    private static string CarriedRefs(ApprovalFixture f, RunId run) => f.GitText("for-each-ref", "--format=%(refname) %(objectname)", RunLayout.CarriedPrefix(run));

    [Theory]
    [InlineData("approval.pinned.after")]
    [InlineData("approval.carry.built")]
    [InlineData("approval.carry.kept")]
    [InlineData("approval.approved.after")]
    public async Task A_crash_at_each_step_of_carrying_then_a_repeated_confirmation_carries_once_in_one_run(string point)
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        f.Answer(B, Writes(B, "out-b.txt", "B\n").Outbox("notes", [7, 8]));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        // The base moves, so the carried code is a new commit the approval writes and keeps.
        f.Git.Write("root.txt", "root moved\n");
        f.GitText("add", "root.txt");
        f.GitText("-c", "commit.gpgSign=false", "commit", "-q", "-m", "Move the base");
        await f.Close();
        await Crash(f, A, point);
        Assert.Equal(point == "approval.approved.after" ? 2 : 1, f.ApprovedRuns().Length);

        await f.Open();
        var preview = f.Runs.Preflight(f.Workflow, A);
        Assert.Equal((B, first), (Assert.Single(preview.Carried).Task, Assert.Single(preview.Carried).Run));
        var starts = await Task.WhenAll(Start(f, preview, BaseChoice.Head, AfterRestart), Start(f, preview, BaseChoice.Head, First));

        var second = Assert.Single(f.ApprovedRuns(), run => run != first);
        Assert.All(starts, start => Assert.Equal(second, start.Coordinator.Address.Run));
        await Completed(starts[0].Coordinator);
        Assert.Equal([1, 1, 1], Launches(f, A, B, C));
        var record = f.Read(second);
        var carried = record.CurrentResults[B];
        Assert.Equal(new ResultOrigin.Carried(first, f.Read(first).CurrentResults[B].Id), carried.Origin);
        Assert.Single(record.Results, result => result.Origin is ResultOrigin.Carried);
        var code = Assert.IsType<CodeOutput.Produced>(carried.Code).Code.Commit;
        Assert.Equal($"{RunLayout.CarriedCode(second, carried.Id)} {code.Hex}\n", CarriedRefs(f, second));
        Assert.Equal(("B\n", "root moved\n"), (Copied(f, C, "out-b.txt"), Copied(f, C, "root.txt")));
        Assert.Equal([7, 8], File.ReadAllBytes(Path.Combine(f.Project, ".idp", "runs", RunFixtures.W.ToString(), second.ToString(),
            Assert.Single(carried.Artifacts).StoredPath)));
    }

    [Fact]
    public async Task A_confirmation_that_replaces_an_unfinished_approval_lets_go_of_what_it_carried()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        f.Answer(B, Writes(B, "out-b.txt", "B\n").Outbox("notes", [7, 8]));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        f.Git.Write("root.txt", "root moved\n");
        f.GitText("add", "root.txt");
        f.GitText("-c", "commit.gpgSign=false", "commit", "-q", "-m", "Move the base");
        await f.Close();
        await Crash(f, A, "approval.carry.kept");
        var pending = Assert.Single(new ApprovalIntents(f.Project, RunFixtures.W).All(), intent => intent.Node == A).Run;
        Assert.NotEqual("", CarriedRefs(f, pending));
        Assert.True(Directory.Exists(Path.Combine(f.Project, ".idp", "runs", RunFixtures.W.ToString(), pending.ToString(), "results")));

        // Another confirmation with other content replaces the unfinished one.
        await f.Open();
        f.Workflow = RunFixtures.Edit(f.Workflow, new WorkflowEdit.EditTitle(A, "A2"));
        var started = await Start(f, f.Runs.Preflight(f.Workflow, A), BaseChoice.Head, AfterRestart);
        await Completed(started.Coordinator);

        Assert.NotEqual(pending, started.Coordinator.Address.Run);
        Assert.Equal("", CarriedRefs(f, pending));
        Assert.False(Directory.Exists(Path.Combine(f.Project, ".idp", "runs", RunFixtures.W.ToString(), pending.ToString())));
        Assert.Equal([1, 1, 1], Launches(f, A, B, C));
    }

    /// <summary><c>A → C ← B</c>, and a root <c>D</c> that holds its run open until <c>d-go</c> exists.</summary>
    private static ApprovalFixture Held(out string gate)
    {
        var f = new ApprovalFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, C), (B, C)));
        gate = Path.Combine(f.Evidence, "d-go");
        JoinAnswers(f).Answer(D, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-D")).WaitForFile(gate)
            .Write("out-d.txt", "D\n").Print(FakeAgents.ReplyLines(ClientId.Codex, "D ready.\n")));
        return f;
    }

    [Fact]
    public async Task A_node_run_while_a_run_is_active_carries_the_earlier_results_its_tasks_need()
    {
        await using var f = Held(out var gate);
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        var started = await Start(f, f.Runs.Preflight(f.Workflow, D), BaseChoice.Head, new(Guid.NewGuid()));
        var coordinator = started.Coordinator;
        await coordinator.Until(view => view.Tasks[D].State == TaskState.Running).WaitAsync(Bound);

        Assert.IsType<RunCommand.Accepted>(await coordinator.Request(coordinator.Address, A, new(Guid.NewGuid())).WaitAsync(Bound));

        var joined = await coordinator.Until(view => view.Tasks[C].State == TaskState.Done).WaitAsync(Bound);
        Assert.True(joined.Tasks[B].Carried);
        Assert.Equal(("A\n", "B\n"), (Copied(f, C, "out-a.txt"), Copied(f, C, "out-b.txt")));
        File.WriteAllText(gate, "go");
        await Completed(coordinator);
        Assert.Equal([1, 1, 1, 1], Launches(f, A, B, C, D));
        var record = f.Read(coordinator.Address.Run);
        Assert.Equal([D, A], record.Requested!);
        var requested = Assert.IsType<RunEvent.Requested>(record.Receipts.Values.Single(entry => entry.Event is RunEvent.Requested).Event);
        Assert.Equal(new ResultOrigin.Carried(first, f.Read(first).CurrentResults[B].Id), Assert.Single(requested.Carried!.Value).Result.Origin);
    }

    [Fact]
    public async Task A_request_that_crashed_after_keeping_its_carried_results_records_them_once_when_repeated()
    {
        await using var f = Held(out var gate);
        await f.Open();
        await RunNode(f, B, 1);
        f.Git.Write("root.txt", "root moved\n");
        f.GitText("add", "root.txt");
        f.GitText("-c", "commit.gpgSign=false", "commit", "-q", "-m", "Move the base");
        var started = await Start(f, f.Runs.Preflight(f.Workflow, D), BaseChoice.Head, new(Guid.NewGuid()));
        var coordinator = started.Coordinator;
        await coordinator.Until(view => view.Tasks[D].State == TaskState.Running).WaitAsync(Bound);
        var run = coordinator.Address.Run;
        var confirmation = new OperationId(Guid.NewGuid());
        var operation = RunOperations.Request(confirmation, A);
        // What the crashed request had done: replayed, kept, and copied B's result, and recorded nothing.
        var record = f.Read(run);
        var history = RunHistory.Of(RunStore.Open(f.Project).Records(RunFixtures.W), record.Revision.Snapshot);
        var crashed = Carrying.Build(f.Git.Open(), record with { Requested = [D, A] }, history, operation, preview: false);
        Assert.Null(Carrying.Keep(f.Git.Open(), f.Project, run, RunFixtures.W, crashed, history));
        Assert.DoesNotContain(f.Read(run).Receipts.Values, entry => entry.Event is RunEvent.Requested);

        Assert.Equal(new RunCommand.Accepted(), await coordinator.Request(coordinator.Address, A, confirmation).WaitAsync(Bound));
        Assert.Equal(new RunCommand.Accepted(), await coordinator.Request(coordinator.Address, A, confirmation).WaitAsync(Bound));

        await coordinator.Until(view => view.Tasks[C].State == TaskState.Done).WaitAsync(Bound);
        File.WriteAllText(gate, "go");
        await Completed(coordinator);
        record = f.Read(run);
        var requested = Assert.IsType<RunEvent.Requested>(record.Receipts.Values.Single(entry => entry.Event is RunEvent.Requested).Event);
        var carried = Assert.Single(requested.Carried!.Value);
        Assert.Equal(Assert.Single(crashed.Carried).Result, carried.Result, new Same<ResultRecord>());
        Assert.Equal($"{RunLayout.CarriedCode(run, carried.Result.Id)} {Assert.IsType<CodeOutput.Produced>(carried.Result.Code).Code.Commit.Hex}\n",
            f.GitText("for-each-ref", "--format=%(refname) %(objectname)", RunLayout.CarriedPrefix(run)));
        Assert.Equal(("B\n", "root moved\n"), (Copied(f, C, "out-b.txt"), Copied(f, C, "root.txt")));
        Assert.Equal([1, 1, 1, 1], Launches(f, A, B, C, D));
    }

    private sealed class Same<T> : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => RunJournal.Canonical(x) == RunJournal.Canonical(y);

        public int GetHashCode(T obj) => 0;
    }
}
