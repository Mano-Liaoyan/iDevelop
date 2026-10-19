using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A Retry of a done task, the tasks after it running again by themselves, and an unstarted attempt whose inputs changed
/// starting again with the new ones (#125).
/// </summary>
public sealed class RerunTests
{
    private static FakeRule Fails(TaskId task) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task))).Write("half.txt", "half\n").Exit(1);

    /// <summary>A turn that copies <paramref name="files"/> as its worktree holds them when it starts into the evidence folder, then writes <paramref name="file"/>.</summary>
    private static FakeRule Sees(CoordinatorFixture f, TaskId task, string file, string text, params string[] files) => files
        .Aggregate(FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task))),
            (rule, seen) => rule.Copy(seen, Path.Combine(f.Evidence, $"{Name(task)}-saw-{seen}")))
        .Write(file, text).Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} again.\n"));

    private static string Saw(CoordinatorFixture f, TaskId task, string file) => File.ReadAllText(Path.Combine(f.Evidence, $"{Name(task)}-saw-{file}"));

    private static ResultId Done(RunView view, TaskId task) => view.Tasks[task] is { State: TaskState.Done, Result: { } result } ? result : default;

    /// <summary>
    /// Architect → Implement, and a separate task that fails, so the run stays active. Architect is done and the person
    /// retries it. Implement goes out of date and runs again by itself in its worktree, with its earlier commit and
    /// Architect's new design merged in before its agent starts.
    /// </summary>
    [Fact]
    public async Task A_done_task_s_retry_reruns_the_task_after_it_with_the_new_work_merged_into_its_earlier_change()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C)], (A, B)));
        f.Answer(A, Writes(A, "design.md", "v1\n"), Writes(A, "design.md", "v2\n"))
            .Answer(B, Writes(B, "parser.cs", "P1\n"), Sees(f, B, "parser.cs", "P2\n", "design.md", "parser.cs"))
            .Answer(C, Fails(C));
        await f.Open();
        await f.Resume();
        var settled = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[B].State == TaskState.Done);
        Assert.True(WorkflowRunCoordinator.OffersRetry(settled.Snapshot!, settled.Tasks[A]));
        Assert.False(WorkflowRunCoordinator.OffersRetry(settled.Snapshot!, settled.Tasks[B] with { State = TaskState.Running }));
        var earlier = f.Result(B);
        var previous = settled.Tasks[A].Attempt!.Value;

        var reply = Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, f.Preparation.Op()).WaitAsync(Bound));

        var done = await f.Until(view => Done(view, B) is { } result && result != earlier.Id && result != default);
        Assert.Equal([2, 2, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(C) });
        Assert.Equal(TaskState.Done, done.Tasks[A].State);
        Assert.Equal(new AttemptCause.Retry(previous, Assert.IsType<AttemptCause.Retry>(f.Read().Attempts[reply.Attempt].Cause).Confirmation),
            f.Read().Attempts[reply.Attempt].Cause);
        // Implement's agent started with its earlier change and Architect's new design.
        Assert.Equal(("v2\n", "P1\n"), (Saw(f, B, "design.md"), Saw(f, B, "parser.cs")));
        Assert.Equal(("v2\n", "P2\n"), (f.ResultFile(B, "design.md"), f.ResultFile(B, "parser.cs")));
        var record = f.Read();
        var first = Assert.IsType<ResultOrigin.Executed>(earlier.Origin).Attempt;
        var rerun = Assert.IsType<ResultOrigin.Executed>(f.Result(B).Origin).Attempt;
        Assert.Equal(new AttemptCause.Retry(first, RunOperations.Rerun(f.Preparation.RunId, first)), record.Attempts[rerun].Cause);
        Assert.Equal(record.Preparations[new(first, 1)].Location.Owner, record.Preparations[new(rerun, 1)].Location.Owner);
        // The earlier change stays in the branch's history.
        var commit = Assert.IsType<CodeOutput.Produced>(f.Result(B).Code).Code;
        Assert.Equal(0, f.Preparation.Git.Run(f.Preparation.Git.Folder, "merge-base", "--is-ancestor",
            Assert.IsType<CodeOutput.Produced>(earlier.Code).Code.Commit.Hex, commit.Commit.Hex).ExitCode);
        Assert.Equal(commit.Commit.Hex, f.Preparation.Git.Git("rev-parse", commit.ResultRef).Trim());
    }

    /// <summary>
    /// The merge of Architect's new design into Implement's earlier change conflicts, and no Merge node takes it, so the
    /// person resolves it as any conflict: Implement starts from their merge once it is committed.
    /// </summary>
    [Fact]
    public async Task A_conflict_in_the_merge_into_the_earlier_change_waits_for_the_person_like_any_conflict()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C)], (A, B)));
        f.Answer(A, Writes(A, "shared.txt", "A1\n"), Writes(A, "shared.txt", "A2\n"))
            .Answer(B, Writes(B, "shared.txt", "B1\n"), Sees(f, B, "b.txt", "B2\n", "shared.txt"))
            .Answer(C, Fails(C));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[B].State == TaskState.Done);
        var earlier = f.Result(B).Id;
        Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, f.Preparation.Op()).WaitAsync(Bound));

        var blocked = await f.Until(view => view.Tasks[B].State == TaskState.Blocked);
        var conflict = blocked.Tasks[B].Block!.Conflict!.Merge!;
        Assert.Null(blocked.Tasks[B].MergedBy);
        Assert.Equal(["shared.txt"], conflict.Paths.ToArray());
        Assert.Equal(1, f.Launches(B));
        var folder = Assert.IsType<ConflictReply.Opened>(await f.Coordinator.OpenConflict(f.Address, conflict).WaitAsync(Bound)).Folder;
        File.WriteAllText(Path.Combine(folder, "shared.txt"), "A2 B1\n");
        Assert.IsType<ConflictReply.Resolved>(await f.Coordinator.MarkResolved(f.Address, conflict, f.Preparation.Op()).WaitAsync(Bound));

        await f.Until(view => Done(view, B) is { } result && result != earlier && result != default);
        Assert.Equal(2, f.Launches(B));
        Assert.Equal("A2 B1\n", Saw(f, B, "shared.txt"));
        Assert.Equal(("A2 B1\n", "B2\n"), (f.ResultFile(B, "shared.txt"), f.ResultFile(B, "b.txt")));
    }

    /// <summary>The same conflict goes to the workflow's Merge node, whose agent resolves it before Implement starts.</summary>
    [Fact]
    public async Task A_conflict_in_the_merge_into_the_earlier_change_goes_to_the_merge_node()
    {
        var m = MergeNodeTests.M;
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B), Agent(C), MergeNodeTests.Merger(m)], (A, B)));
        f.Answer(A, Writes(A, "shared.txt", "A1\n"), Writes(A, "shared.txt", "A2\n"))
            .Answer(B, Writes(B, "shared.txt", "B1\n"), Sees(f, B, "b.txt", "B2\n", "shared.txt"))
            .Answer(C, Fails(C)).Answer("M", MergeNodeTests.Resolves("shared.txt", "A2 B1\n"));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[B].State == TaskState.Done);
        var earlier = f.Result(B).Id;
        Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, f.Preparation.Op()).WaitAsync(Bound));

        await f.Until(view => Done(view, B) is { } result && result != earlier && result != default);
        Assert.Equal((1, 2), (f.Launches("M"), f.Launches(B)));
        var conflict = Assert.IsType<AttemptCause.Merge>(Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == m).Cause).Conflict;
        Assert.Equal(new MergeTarget.Join(B), conflict.Target);
        Assert.Equal("A2 B1\n", Saw(f, B, "shared.txt"));
    }

    /// <summary>
    /// With one client root, the person retries A and then B, whose attempt is reserved while A's retry runs. A's new
    /// result changes B's inputs before B's attempt starts, so that attempt closes as not started and B starts again with
    /// the new inputs, with the files its failed attempt left.
    /// </summary>
    [Fact]
    public async Task An_unstarted_attempt_whose_inputs_changed_starts_again_with_the_new_ones()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        var gate = Path.Combine(f.Evidence, "gate");
        f.Answer(A, Writes(A, "a.txt", "old\n"), FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-A")).WaitForFile(gate)
                .Write("a.txt", "new\n").Print(FakeAgents.ReplyLines(ClientId.Codex, "A again.\n")))
            .Answer(B, Fails(B), Sees(f, B, "b.txt", "B\n", "a.txt", "half.txt"));
        await f.Open();
        f.Runs.ClientRoots = 1;
        await f.Resume();
        await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[B].State == TaskState.Failed);
        Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, A, f.Preparation.Op()).WaitAsync(Bound));
        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        var queued = Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, B, f.Preparation.Op()).WaitAsync(Bound)).Attempt;
        await f.Until(view => view.Tasks[B] is { State: TaskState.Ready, Reruns: false } ready && ready.Attempt == queued);

        File.WriteAllText(gate, "open");
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal([2, 2], new[] { f.Launches(A), f.Launches(B) });
        var record = f.Read();
        Assert.Equal(new AttemptEnd.Recovered(RecoveryOutcome.NotStarted, RunOperations.Rerun(f.Preparation.RunId, queued),
            WorkflowRunCoordinator.InputsChangedReason), record.Closures[queued]);
        Assert.DoesNotContain(record.Claims.Keys, key => key.Attempt == queued);
        Assert.Equal(("new\n", "half\n"), (Saw(f, B, "a.txt"), Saw(f, B, "half.txt")));
        Assert.Equal(("new\n", "B\n"), (f.ResultFile(B, "a.txt"), f.ResultFile(B, "b.txt")));
    }
}
