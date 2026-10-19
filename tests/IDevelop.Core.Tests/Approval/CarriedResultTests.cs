using IDevelop.Core.Tests.Coordination;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using RunFixtures = IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>
/// A node's run counts a current result from an earlier run as complete (#90, the owner's decision of 2026-10-09): it
/// carries that result into the new run as its report and artifacts, so the tasks after the node start once all of their
/// predecessors are complete. The earlier run's work is already in the branch the run starts from (#144), which these
/// tests put there with <see cref="ApprovalFixture.MergeResult"/>. A result that no longer counts is not carried, and its
/// task must run again. Run Workflow carries nothing.
/// </summary>
public sealed class CarriedResultTests
{
    private static OperationId Command(int n) => new(Guid.Parse($"00000000-0000-0000-0000-0000000ca{n:D3}"));

    /// <summary><c>A → C ← B</c>.</summary>
    internal static Workflow Join(bool readOnlyB = false) => Graph([Agent(A), Agent(B, readOnly: readOnlyB), Agent(C)], (A, C), (B, C));

    /// <summary>A turn that copies <paramref name="files"/> out of its checkout, so the test sees what the task got.</summary>
    internal static FakeRule Copies(ApprovalFixture f, TaskId task, params string[] files)
    {
        var rule = FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task)));
        foreach (var file in files) rule = rule.Copy(file, Path.Combine(f.Evidence, $"{Name(task)}-{file}"));
        return rule.Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));
    }

    internal static ApprovalFixture JoinAnswers(ApprovalFixture f) =>
        f.Answer(A, Writes(A, "out-a.txt", "A\n"), Writes(A, "out-a.txt", "A2\n"))
            .Answer(B, Writes(B, "out-b.txt", "B\n"), Writes(B, "out-b.txt", "B2\n"))
            .Answer(C, Copies(f, C, "out-a.txt", "out-b.txt", "root.txt"), Copies(f, C, "out-a.txt", "out-b.txt", "root.txt"));

    /// <summary>Runs <paramref name="node"/> through its preflight until the run completes.</summary>
    internal static async Task<(RunId Run, RunView View)> RunNode(ApprovalFixture f, TaskId node, int command)
    {
        var started = await Start(f, f.Runs.Preflight(f.Workflow, node), Command(command));
        return (started.Coordinator.Address.Run, await Completed(started.Coordinator));
    }

    internal static int[] Launches(ApprovalFixture f, params TaskId[] tasks) => [.. tasks.Select(f.Launches)];

    internal static string Copied(ApprovalFixture f, TaskId task, string file) => File.ReadAllText(Path.Combine(f.Evidence, $"{Name(task)}-{file}"));

    /// <summary>The refs under <c>refs/idp/</c> that keep carried results, which a run writes none of.</summary>
    internal static string CarriedRefs(ApprovalFixture f) => f.GitText("for-each-ref", "--format=%(refname)", "refs/idp/carried/");

    [Fact]
    public async Task A_join_starts_after_the_node_once_its_other_predecessor_has_a_result_from_an_earlier_run()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        Assert.Equal([0, 1, 0], Launches(f, A, B, C));
        var earlier = f.Read(first).CurrentResults[B];
        f.MergeResult(first, B);

        var preview = f.Runs.Preflight(f.Workflow, A);

        Assert.Empty(preview.Gaps);
        Assert.Equal(new PreflightCarried(B, first, earlier.Id, null), Assert.Single(preview.Carried));
        var (second, done) = await RunNode(f, A, 2);
        Assert.Equal([1, 1, 1], Launches(f, A, B, C));
        Assert.Equal(("A\n", "B\n"), (Copied(f, C, "out-a.txt"), Copied(f, C, "out-b.txt")));
        var record = f.Read(second);
        var carried = record.CurrentResults[B];
        Assert.Equal(new ResultOrigin.Carried(first, earlier.Id), carried.Origin);
        Assert.Equal(earlier.Report, carried.Report);
        Assert.True(done.Tasks[B].Carried);
        Assert.Equal(TaskState.Done, done.Tasks[C].State);
        // The carried result brings its report and artifacts; B's work reaches C through the branch the run started from.
        Assert.Null(carried.Code);
        Assert.Equal("", CarriedRefs(f));
        Assert.Equal([B], record.Results.Where(result => result.Origin is ResultOrigin.Carried).Select(result => result.Task));
    }

    [Fact]
    public async Task A_node_whose_predecessor_has_no_current_result_in_any_run_starts_nothing_and_names_it()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        await f.Open();
        Assert.Equal<PreflightGap>([new PreflightGap.After(C, [A, B])], f.Runs.Preflight(f.Workflow, C).Gaps);
        await RunNode(f, B, 1);

        var preview = f.Runs.Preflight(f.Workflow, C);

        Assert.Equal<PreflightGap>([new PreflightGap.After(C, [A])], preview.Gaps);
        var refused = Assert.IsType<WorkflowStart.Refused>(await f.Runs.StartWorkflow(f.Workflow, new(preview, Command(2))).WaitAsync(Bound));
        Assert.Equal(ApprovalProblem.NotConfirmable, refused.Problem);
        Assert.Single(f.ApprovedRuns());
    }

    [Fact]
    public async Task A_node_whose_predecessors_both_have_earlier_results_runs_on_them_and_a_carried_result_carries_on_again()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        f.MergeResult(first, B);
        var (second, _) = await RunNode(f, A, 2);
        f.MergeResult(second, C);

        var preview = f.Runs.Preflight(f.Workflow, C);

        Assert.Empty(preview.Gaps);
        // B's newest run is the second, which carried it: the third run carries that carried result, as the same result.
        var inSecond = f.Read(second);
        Assert.Equal([(A, second, inSecond.CurrentResults[A].Id), (B, second, inSecond.CurrentResults[B].Id)],
            preview.Carried.Select(row => (row.Task, row.Run, row.Result)));
        var (third, _) = await RunNode(f, C, 3);
        Assert.Equal([1, 1, 2], Launches(f, A, B, C));
        var record = f.Read(third);
        Assert.Equal(new ResultOrigin.Carried(second, inSecond.CurrentResults[B].Id), record.CurrentResults[B].Origin);
        Assert.Null(record.CurrentResults[B].Code);
        Assert.Equal(("A\n", "B\n"), (Copied(f, C, "out-a.txt"), Copied(f, C, "out-b.txt")));
    }

    [Fact]
    public async Task A_chain_across_runs_carries_each_result_the_needed_one_took_first()
    {
        // A → B → D ← C: the first run runs A and B, and D waits for C.
        await using var f = new ApprovalFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, B), (B, D), (C, D)));
        f.Answer(A, Writes(A, "out-a.txt", "A\n")).Answer(B, Writes(B, "out-b.txt", "B\n")).Answer(C, Writes(C, "out-c.txt", "C\n"))
            .Answer(D, Copies(f, D, "out-a.txt", "out-b.txt", "out-c.txt"));
        await f.Open();
        var (first, firstView) = await RunNode(f, A, 1);
        Assert.Equal([1, 1, 0, 0], Launches(f, A, B, C, D));
        Assert.Equal([C], firstView.Tasks[D].HeldBy);
        f.MergeResult(first, B);

        var (second, _) = await RunNode(f, C, 2);

        Assert.Equal([1, 1, 1, 1], Launches(f, A, B, C, D));
        Assert.Equal(("A\n", "B\n", "C\n"), (Copied(f, D, "out-a.txt"), Copied(f, D, "out-b.txt"), Copied(f, D, "out-c.txt")));
        var approved = Assert.IsType<RunEvent.Approved>(f.Read(second).Receipts.Values.Single(entry => entry.Sequence == 1).Event);
        Assert.Equal([A, B], approved.Carried!.Value.Select(item => item.Result.Task));
        // B's carried result took A's carried one.
        var carried = approved.Carried!.Value;
        Assert.Equal(carried[0].Result.Id, Assert.IsType<InputBinding.Provided>(Assert.Single(carried[1].Inputs.Bindings)).Result);
        Assert.Equal(new ResultOrigin.Carried(first, f.Read(first).CurrentResults[A].Id), carried[0].Result.Origin);
    }

    [Fact]
    public async Task A_carried_result_brings_no_code_so_the_run_starts_from_the_branch_s_HEAD_as_it_is()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        f.Answer(C, Copies(f, C, "out-a.txt", "root.txt"));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        // B's work stays out of the branch, and the branch moves on.
        f.Git.Write("root.txt", "root moved\n");
        f.GitText("add", "root.txt");
        f.GitText("-c", "commit.gpgSign=false", "commit", "-q", "-m", "Move the base");
        var head = new CommitId(f.GitText("rev-parse", "HEAD").Trim());

        var preview = f.Runs.Preflight(f.Workflow, A);

        Assert.Null(Assert.Single(preview.Carried).Refusal);
        var (second, _) = await RunNode(f, A, 2);
        Assert.Equal([1, 1, 1], Launches(f, A, B, C));
        var record = f.Read(second);
        Assert.Equal(head, record.Base.Commit);
        var carried = record.CurrentResults[B];
        Assert.Equal(new ResultOrigin.Carried(first, f.Read(first).CurrentResults[B].Id), carried.Origin);
        Assert.Null(carried.Code);
        Assert.IsType<CodeSelection.Root>(record.Inputs[carried.Inputs].Code);
        Assert.Equal("", CarriedRefs(f));
        Assert.Equal(("A\n", "root moved\n"), (Copied(f, C, "out-a.txt"), Copied(f, C, "root.txt")));
        Assert.Null(f.ResultFile(second, C, "out-b.txt"));
    }

    [Fact]
    public async Task A_report_from_an_earlier_run_is_carried_as_it_is_with_no_code_to_replay()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join(readOnlyB: true)));
        f.Answer(B, Reports(B)).Answer(C, Copies(f, C, "out-a.txt", "root.txt"));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        f.Git.Write("root.txt", "root moved\n");
        f.GitText("add", "root.txt");
        f.GitText("-c", "commit.gpgSign=false", "commit", "-q", "-m", "Move the base");

        var preview = f.Runs.Preflight(f.Workflow, A);

        var row = Assert.Single(preview.Carried);
        Assert.Equal((B, first, null), (row.Task, row.Run, row.Refusal));
        var (second, _) = await RunNode(f, A, 2);
        Assert.Equal([1, 1, 1], Launches(f, A, B, C));
        var carried = f.Read(second).CurrentResults[B];
        Assert.Null(carried.Code);
        Assert.Equal("B ready.\n", carried.Report);
        Assert.Contains("B ready.", File.ReadAllText(Path.Combine(f.Folder(C), "1.stdin")));
        Assert.Equal(("A\n", "root moved\n"), (Copied(f, C, "out-a.txt"), Copied(f, C, "root.txt")));
    }

    [Fact]
    public async Task An_approval_reads_a_review_carried_from_an_earlier_run_from_that_run_s_record_of_the_review()
    {
        await using var f = new ApprovalFixture(CarriedReview());
        var (_, again, request) = await RunCarriedReview(f);

        Assert.Equal([1, 1], Launches(f, A, C));
        Assert.IsType<ResultOrigin.Carried>(f.Read(again.Address.Run).CurrentResults[C].Origin);
        var review = Assert.Single(again.GateInputs(request)!.Value).Review!;
        Assert.Equal((true, 1), (review.Verdict.Approves, review.Round));
    }

    [Fact]
    public async Task A_review_node_whose_result_a_run_carried_reads_the_earlier_run_s_record_of_the_review()
    {
        await using var f = new ApprovalFixture(CarriedReview());
        var (earlier, again, _) = await RunCarriedReview(f);

        var log = again.ReviewLog(C)!;

        Assert.IsType<ResultOrigin.Carried>(f.Read(again.Address.Run).CurrentResults[C].Origin);
        Assert.Equal((RunProjection.LatestAttempts(f.Read(earlier))[C], AttemptStatus.Succeeded), (log.Id, log.Status));
        Assert.Equal((true, 1), (ReviewLedger.Fold(log).Rounds[^1].Verdict!.Approves, ReviewLedger.Fold(log).Round));
    }

    /// <summary><c>A → C → X</c>, where <c>C</c> reviews <c>A</c> and <c>X</c> is an approval.</summary>
    private static Workflow CarriedReview() =>
        Graph([Agent(A), RunReviewTests.Reviewer(C, "Build C."), new TaskDefinition(X, BuiltInBlueprints.Approval) { Title = "Gate" }], (A, C), (C, X));

    /// <summary>
    /// Runs <see cref="CarriedReview"/> to completion, where the review agrees at once, then runs <c>X</c> again, which
    /// carries the review's result, until its request waits. Returns the first run, the second run's coordinator, and its request.
    /// </summary>
    private static async Task<(RunId Earlier, WorkflowRunCoordinator Again, GateId Request)> RunCarriedReview(ApprovalFixture f)
    {
        const string Agreed = """{"status": "verdict", "verdict": "approve", "findings": []}""";
        f.Answer(A, Writes(A, "out-a.txt", "A\n")).Answer(C, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-C"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, RunReviewTests.Verdict(Agreed))));
        await f.Open();
        var started = await Start(f, f.Runs.Preflight(f.Workflow, A), Command(1));
        await Approve(started.Coordinator);
        await Completed(started.Coordinator);

        var again = await Start(f, f.Runs.Preflight(f.Workflow, X), Command(2));
        return (started.Coordinator.Address.Run, again.Coordinator, await Approve(again.Coordinator, answer: false));
    }

    /// <summary>Waits for <c>X</c>'s request and approves it unless <paramref name="answer"/> is false.</summary>
    private static async Task<GateId> Approve(WorkflowRunCoordinator coordinator, bool answer = true)
    {
        var waiting = await coordinator.Until(view => view.Tasks[X].Gate is { Status: GateStatus.Waiting }).WaitAsync(Bound);
        var gate = waiting.Tasks[X].Gate!;
        if (answer)
            Assert.IsType<GateReply.Recorded>(await coordinator.Approve(coordinator.Address, new(gate.Request.Id, gate.Request.Inputs), new(Guid.NewGuid())).WaitAsync(Bound));
        return gate.Request.Id;
    }

    [Fact]
    public async Task A_carried_result_s_artifacts_are_copied_into_the_new_run_and_checked_by_digest()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        f.Answer(B, Writes(B, "out-b.txt", "B\n").Outbox("notes", [1, 2, 3]));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        var earlier = Assert.Single(f.Read(first).CurrentResults[B].Artifacts);
        f.MergeResult(first, B);

        var (second, _) = await RunNode(f, A, 2);

        var carried = f.Read(second).CurrentResults[B];
        var artifact = Assert.Single(carried.Artifacts);
        Assert.Equal((earlier.Name, earlier.Content, earlier.ByteLength), (artifact.Name, artifact.Content, artifact.ByteLength));
        Assert.Equal(RunStorage.ArtifactPath(carried.Id, "notes"), artifact.StoredPath);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(f.Project, ".idp", "runs", RunFixtures.W.ToString(), second.ToString(), artifact.StoredPath)));
        Assert.Contains("- notes: ", File.ReadAllText(Path.Combine(f.Folder(C), "1.stdin")));
    }

    [Fact]
    public async Task A_result_whose_task_or_connections_changed_since_it_ran_does_not_count()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        await f.Open();
        await RunNode(f, B, 1);
        var original = f.Workflow;
        f.Workflow = RunFixtures.Edit(f.Workflow, new WorkflowEdit.SetExecution(B, original.Tasks[B].Execution! with { Model = "m2" }));

        Assert.Empty(f.Runs.Preflight(f.Workflow, A).Carried);
        Assert.Equal<PreflightGap>([new PreflightGap.After(C, [A, B])], f.Runs.Preflight(f.Workflow, C).Gaps);

        // A new connection into B makes it out of date too.
        f.Workflow = RunFixtures.Edit(RunFixtures.Edit(original, TestNodes.Place(Agent(D), new(0, 0))), new WorkflowEdit.Connect(new(D, B), ConnectionKind.Context));
        Assert.Empty(f.Runs.Preflight(f.Workflow, A).Carried);
        // Back to what it ran with, it counts again.
        f.Workflow = original;
        Assert.Single(f.Runs.Preflight(f.Workflow, A).Carried);
    }

    [Fact]
    public async Task A_carried_result_that_took_results_of_several_tasks_comes_after_their_carried_results_and_takes_no_join()
    {
        // X → B ← D (D plays Y), and A → C ← B. Run Workflow runs everything; running A again carries X, Y, and B.
        await using var f = new ApprovalFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D), Agent(X)], (X, B), (D, B), (B, C), (A, C)));
        f.Answer(X, Writes(X, "out-x.txt", "X\n")).Answer(D, Writes(D, "out-y.txt", "Y\n")).Answer(B, Writes(B, "out-b.txt", "B\n"))
            .Answer(A, Writes(A, "out-a.txt", "A\n"), Writes(A, "out-a.txt", "A2\n"))
            .Answer(C, Copies(f, C, "out-a.txt", "out-b.txt", "out-x.txt", "out-y.txt"), Copies(f, C, "out-a.txt", "out-b.txt", "out-x.txt", "out-y.txt"));
        await f.Open();
        var started = await Start(f, f.Preflight(), Command(1));
        await Completed(started.Coordinator);
        f.MergeResult(started.Coordinator.Address.Run, C);

        var (second, _) = await RunNode(f, A, 2);

        Assert.Equal([2, 1, 2, 1, 1], Launches(f, A, B, C, D, X));
        Assert.Equal(("A2\n", "B\n", "X\n", "Y\n"), (Copied(f, C, "out-a.txt"), Copied(f, C, "out-b.txt"), Copied(f, C, "out-x.txt"), Copied(f, C, "out-y.txt")));
        var record = f.Read(second);
        var approved = Assert.IsType<RunEvent.Approved>(record.Receipts.Values.Single(entry => entry.Sequence == 1).Event);
        Assert.Equal([D, X, B], approved.Carried!.Value.Select(item => item.Result.Task));
        var carried = record.CurrentResults[B];
        Assert.IsType<CodeSelection.Root>(record.Inputs[carried.Inputs].Code);
        Assert.All(approved.Carried!.Value, item => Assert.Null(item.Result.Code));
        Assert.Equal("", CarriedRefs(f));
    }

    [Fact]
    public async Task A_preview_whose_earlier_result_another_run_replaced_since_is_a_changed_preview()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        await f.Open();
        var (first, _) = await RunNode(f, B, 1);
        var preview = f.Runs.Preflight(f.Workflow, A);
        Assert.Equal(first, Assert.Single(preview.Carried).Run);
        var (second, _) = await RunNode(f, B, 2);

        var changed = Assert.IsType<WorkflowStart.Changed>(await f.Runs.StartWorkflow(f.Workflow, new(preview, Command(3))).WaitAsync(Bound));

        Assert.Equal(second, Assert.Single(changed.Current.Carried).Run);
        Assert.Equal(2, f.ApprovedRuns().Length);
    }

    [Fact]
    public async Task Run_Workflow_runs_every_task_again_and_carries_nothing()
    {
        await using var f = JoinAnswers(new ApprovalFixture(Join()));
        await f.Open();
        await RunNode(f, B, 1);

        var preview = f.Preflight();

        Assert.Empty(preview.Carried);
        var started = await Start(f, preview, Command(2));
        await Completed(started.Coordinator);
        Assert.Equal([1, 2, 1], Launches(f, A, B, C));
        var record = f.Read(started.Coordinator.Address.Run);
        Assert.DoesNotContain(record.Results, result => result.Origin is ResultOrigin.Carried);
        Assert.Equal("B2\n", Copied(f, C, "out-b.txt"));
    }
}
