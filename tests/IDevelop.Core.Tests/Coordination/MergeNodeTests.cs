using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A run's merges (#148): a conflict between predecessors goes to a Merge node's agent, which resolves it by writing the
/// conflicting files, and the successor starts once the merge is committed.
/// </summary>
public sealed class MergeNodeTests
{
    /// <summary>The Merge node.</summary>
    internal static readonly TaskId M = new(Guid.Parse("00000000-0000-0000-0000-0000000000e5"));

    /// <summary>A second Merge node.</summary>
    internal static readonly TaskId N = new(Guid.Parse("00000000-0000-0000-0000-0000000000e6"));

    /// <summary>A Merge node whose prompt starts with <c>"Build &lt;name&gt;."</c>, so the fake client answers it from its own folder.</summary>
    internal static TaskDefinition Merger(TaskId task, string name = "M") => new(task, new Blueprint(new($"example.merge.{name}", 1), "Merge",
        new WorkSpec.Merge(PromptTemplate.Parse($"Build {name}.\n\n{{{{conflict}}}}")), [], new(null, ConversationMode.Autonomous)))
    {
        Title = name,
        Execution = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" },
    };

    /// <summary>A Merge node's turn that writes <paramref name="file"/> as the merge and reports it.</summary>
    internal static FakeRule Resolves(string file, string text, string name = "M") => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + name))
        .Write(file, text)
        .Print(FakeAgents.ReplyLines(ClientId.Codex, "Merged.\n"));

    [Fact]
    public async Task A_conflict_between_predecessors_goes_to_the_merge_nodes_agent_and_the_successor_starts_from_its_merge()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D, readOnly: true), Merger(M)], (B, D), (C, D)));
        f.Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n")).Answer(D, Reports(D))
            .Answer("M", Resolves("settings.txt", "B\nC\n"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal(1, f.Launches("M"));
        // The agent read the conflict, and its worktree held the merge with Git's markers.
        var prompt = f.Prompt("M");
        Assert.StartsWith("Build M.", prompt);
        Assert.Contains("D starts from the merge of the work of B and C.", prompt);
        Assert.Contains("- `settings.txt`", prompt);
        var record = f.Read();
        var merge = Assert.Single(record.Attempts.Values, attempt => attempt.Task == M);
        var conflict = Assert.IsType<AttemptCause.Merge>(merge.Cause).Conflict;
        Assert.Equal(new MergeTarget.Join(D), conflict.Target);
        Assert.Contains("<<<<<<<", f.Preparation.Git.Git("show", conflict.Conflicted.Hex + ":settings.txt"));

        // D started once the merge was committed: its worktree is at the merge of B and C with the resolved file.
        Assert.Equal(1, f.Launches(D));
        Assert.Equal("B\nC\n", File.ReadAllText(Path.Combine(f.Checkout(D), "settings.txt")));
        var joined = Assert.IsType<CodeSelection.Joined>(record.Inputs[record.CurrentResults[D].Inputs].Code).Join;
        var resolved = RunReducer.Resolution(record, conflict.Ours, conflict.Theirs)!.Value;
        Assert.Equal(f.Preparation.Git.Git("rev-parse", resolved.Hex + "^{tree}").Trim(), joined.Tree.Hex);
        Assert.Equal($"{conflict.Ours.Hex} {conflict.Theirs.Hex}", f.Preparation.Git.Git("show", "-s", "--format=%P", resolved.Hex).Trim());
        // The block that held D is resolved, and the Merge node has no result of its own.
        Assert.All(record.Blocks.Values, state => Assert.True(state.Resolved));
        Assert.False(record.CurrentResults.ContainsKey(M));
    }

    [Fact]
    public async Task A_merge_that_leaves_conflict_markers_fails_the_merge_node_until_the_person_retries_it()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D, readOnly: true), Merger(M)], (B, D), (C, D)));
        var careless = FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-M"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "Done.\n"));
        f.Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n")).Answer(D, Reports(D))
            .Answer("M", careless, Resolves("settings.txt", "B and C\n"));
        await f.Open();
        await f.Resume();
        var failed = await f.Until(view => view.Tasks[M].State == TaskState.Failed);
        Assert.Equal(RunStatus.NeedsAttention, failed.Status);
        Assert.Equal(TaskState.Blocked, failed.Tasks[D].State);
        Assert.Equal(M, failed.Tasks[D].MergedBy);
        Assert.NotNull(failed.Tasks[M].Merge);
        Assert.True(WorkflowRunCoordinator.OffersRetry(f.Read().Revision.Snapshot, failed.Tasks[M]));
        Assert.Equal(0, f.Launches(D));

        Assert.IsType<RetryReply.Reserved>(await f.Coordinator.Retry(f.Address, M, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(2, f.Launches("M"));
        Assert.Equal("B and C\n", File.ReadAllText(Path.Combine(f.Checkout(D), "settings.txt")));
    }

    [Fact]
    public async Task Without_a_merge_node_the_conflict_waits_for_the_person()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D, readOnly: true)], (B, D), (C, D)));
        f.Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n")).Answer(D, Reports(D));
        await f.Open();
        await f.Resume();
        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        Assert.Equal(TaskState.Blocked, stuck.Tasks[D].State);
        Assert.Null(stuck.Tasks[D].MergedBy);
        Assert.Equal(["settings.txt"], stuck.Tasks[D].Block!.Conflict!.Merge!.Paths.ToArray());
    }

    [Fact]
    public async Task A_merge_node_the_person_adds_and_runs_takes_the_conflict_that_waited_for_them()
    {
        var workflow = Graph([Agent(B), Agent(C), Agent(D, readOnly: true)], (B, D), (C, D));
        await using var f = new CoordinatorFixture(workflow);
        f.Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n")).Answer(D, Reports(D))
            .Answer("M", Resolves("settings.txt", "B + C\n"));
        await f.Open();
        await f.Resume();
        var stuck = await f.Until(view => view.Tasks[D].State == TaskState.Blocked);
        Assert.True(WorkflowRunCoordinator.WaitsForMergeNode(stuck));

        var document = Runs.RunFixtures.Edit(workflow, TestNodes.Place(Merger(M), new(0, 0)));
        var confirmation = f.Preparation.Op();
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.AddMerge(f.Address, document, M, confirmation).WaitAsync(Bound));
        // A repeat adds it once.
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.AddMerge(f.Address, document, M, confirmation).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal(1, f.Launches("M"));
        Assert.Single(f.Read().Receipts.Values, entry => entry.Event is RunEvent.Amended { Origin: AmendmentOrigin.Person });
        Assert.Equal("B + C\n", File.ReadAllText(Path.Combine(f.Checkout(D), "settings.txt")));
    }

    [Fact]
    public async Task A_merge_node_between_predecessors_and_a_successor_resolves_its_own_conflict_and_hands_on_the_merge()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D, readOnly: true), Merger(M)], (B, M), (C, M), (M, D)));
        f.Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n")).Answer(D, Reports(D))
            .Answer("M", Resolves("settings.txt", "B, C\n"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        var record = f.Read();
        Assert.Equal(1, f.Launches("M"));
        // The Merge node's own step handed on its predecessors' merged code and reports, as an approval that needs no person.
        var result = record.CurrentResults[M];
        Assert.IsType<ResultOrigin.Merged>(result.Origin);
        Assert.Contains("B ready.", result.Report);
        Assert.Contains("C ready.", result.Report);
        Assert.Equal("B, C\n", File.ReadAllText(Path.Combine(f.Checkout(D), "settings.txt")));
    }

    [Fact]
    public async Task A_merge_node_whose_predecessors_merge_cleanly_hands_on_their_merge_without_its_agent()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D, readOnly: true), Merger(M)], (B, M), (C, M), (M, D)));
        f.Answer(B, Writes(B, "b.txt", "B\n")).Answer(C, Writes(C, "c.txt", "C\n")).Answer(D, Reports(D));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        Assert.Equal(0, f.Launches("M"));
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(f.Checkout(D), "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(f.Checkout(D), "c.txt")));
    }

    [Fact]
    public async Task With_several_merge_nodes_a_conflict_goes_to_the_one_the_conflicting_branches_connect_into()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D, readOnly: true), Merger(M), Merger(N, "N")],
            (B, D), (C, D), (B, N), (C, N)));
        f.Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n")).Answer(D, Reports(D))
            .Answer("N", Resolves("settings.txt", "N's merge\n", "N"));
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[D].State == TaskState.Done);

        Assert.Equal(0, f.Launches("M"));
        Assert.Equal(1, f.Launches("N"));
        Assert.Equal("N's merge\n", File.ReadAllText(Path.Combine(f.Checkout(D), "settings.txt")));
    }

    /// <summary>The fixture's <c>main</c>, one commit past <see cref="PlanCommit"/>: it adds <c>a.txt</c>.</summary>
    private static readonly CommitId Main = new("adfe40b30c176fb407933286f51d15ea9b54cdc3");

    private const string Branch = "refs/heads/main";

    [Fact]
    public async Task A_finished_runs_work_merges_into_the_branch_it_started_from_and_the_persons_folder_follows()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]), runBase: Main, branch: Branch);
        f.Answer(A, Writes(A, "b.txt", "B\n"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        var git = f.Preparation.Git;
        var produced = Assert.IsType<CodeOutput.Produced>(f.Result(A).Code).Code.Commit;
        // main had not moved, so it fast-forwards to the run's work, and the folder that has it checked out follows.
        Assert.Equal(produced.Hex, git.Git("rev-parse", "main").Trim());
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(git.Folder, "b.txt")));
        Assert.Equal("", git.Git("status", "--porcelain", "--untracked-files=no").Trim());
    }

    [Fact]
    public async Task The_final_merge_waits_while_the_persons_folder_has_uncommitted_changes_and_never_touches_them()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]), runBase: Main, branch: Branch);
        f.Answer(A, Writes(A, "b.txt", "B\n"));
        var git = f.Preparation.Git;
        git.Write("root.txt", "root\nmine\n");
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Merge is BranchMergeWait.Uncommitted);
        Assert.Equal(RunStatus.Waiting, waiting.Status);
        Assert.Equal(RunPhase.Approved, waiting.Phase);
        var wait = Assert.IsType<BranchMergeWait.Uncommitted>(waiting.Merge);
        Assert.Equal(["root.txt"], wait.Paths.ToArray());
        Assert.Equal(Branch, wait.Branch);
        Assert.Equal(Main.Hex, git.Git("rev-parse", "main").Trim());
        Assert.Equal("root\nmine\n", File.ReadAllText(Path.Combine(git.Folder, "root.txt")));
        Assert.False(File.Exists(Path.Combine(git.Folder, "b.txt")));

        // The person commits, and the run's work merges beside their commit.
        git.Git("commit", "-q", "-am", "Mine");
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("root\nmine\n", File.ReadAllText(Path.Combine(git.Folder, "root.txt")));
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(git.Folder, "b.txt")));
        Assert.Equal(2, git.Git("show", "-s", "--format=%P", "main").Trim().Split(' ').Length);
    }

    [Fact]
    public async Task A_conflict_with_the_branch_goes_to_the_merge_node_with_no_successors()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Merger(M)]), branch: Branch);
        f.Answer(A, Writes(A, "a.txt", "Run\n")).Answer("M", Resolves("a.txt", "A and Run\n"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        var git = f.Preparation.Git;
        Assert.Equal(1, f.Launches("M"));
        Assert.Contains("merges into main, the branch it started from", f.Prompt("M"));
        Assert.Contains("- `a.txt`", f.Prompt("M"));
        var produced = Assert.IsType<CodeOutput.Produced>(f.Result(A).Code).Code.Commit;
        Assert.Equal($"{Main.Hex} {produced.Hex}", git.Git("show", "-s", "--format=%P", "main").Trim());
        Assert.Equal("A and Run\n", File.ReadAllText(Path.Combine(git.Folder, "a.txt")));
        Assert.False(f.Read().CurrentResults.ContainsKey(M));
    }

    [Fact]
    public async Task A_conflict_with_the_branch_and_no_merge_node_waits_until_the_person_merges_the_work()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A)]), branch: Branch);
        f.Answer(A, Writes(A, "a.txt", "Run\n"));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Merge is BranchMergeWait.Conflict);
        var wait = Assert.IsType<BranchMergeWait.Conflict>(waiting.Merge);
        Assert.Null(wait.Node);
        Assert.Equal(["a.txt"], wait.Paths.ToArray());
        Assert.Equal(RunStatus.Waiting, waiting.Status);
        var git = f.Preparation.Git;
        Assert.Equal(Main.Hex, git.Git("rev-parse", "main").Trim());

        // The person merges the run's work themselves, keeping their side, and the run completes.
        var produced = Assert.IsType<CodeOutput.Produced>(f.Result(A).Code).Code.Commit;
        git.Git("merge", "-q", "-s", "ours", "--no-edit", produced.Hex);
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(git.Folder, "a.txt")));
    }

    [Fact]
    public async Task A_conflict_with_the_branch_goes_to_a_merge_node_the_person_adds_and_runs()
    {
        var workflow = Graph([Agent(A)]);
        await using var f = new CoordinatorFixture(workflow, branch: Branch);
        f.Answer(A, Writes(A, "a.txt", "Run\n")).Answer("M", Resolves("a.txt", "Both\n"));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Merge is BranchMergeWait.Conflict);
        Assert.True(WorkflowRunCoordinator.WaitsForMergeNode(waiting));

        var document = Runs.RunFixtures.Edit(workflow, TestNodes.Place(Merger(M), new(0, 0)));
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.AddMerge(f.Address, document, M, f.Preparation.Op()).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches("M"));
        Assert.Equal("Both\n", File.ReadAllText(Path.Combine(f.Preparation.Git.Folder, "a.txt")));
    }

    [Fact]
    public void The_end_of_run_merge_goes_to_the_merge_node_with_no_successors_and_one_merge_node_takes_every_conflict()
    {
        var workflow = Graph([Agent(B), Agent(C), Agent(D), Merger(M), Merger(N, "N")], (B, M), (C, M), (M, D));
        Assert.Equal(N, MergeRouting.Node(workflow, new MergeTarget.Branch("refs/heads/main"), []));
        // Conflicting branches that connect into no Merge node, with two of them, wait for the person.
        Assert.Null(MergeRouting.Node(workflow, new MergeTarget.Join(D), [B, X]));
        Assert.Equal(M, MergeRouting.Node(workflow, new MergeTarget.Join(M), [B, C]));
        Assert.Equal(M, MergeRouting.Node(workflow, new MergeTarget.Join(D), [B, C]));

        var one = Graph([Agent(B), Agent(C), Agent(D), Merger(M)], (B, D), (C, D), (M, D));
        Assert.Equal(M, MergeRouting.Node(one, new MergeTarget.Join(D), [B, C]));
        Assert.Equal(M, MergeRouting.Node(one, new MergeTarget.Branch("refs/heads/main"), []));
        Assert.Null(MergeRouting.Node(Graph([Agent(B), Agent(C), Agent(D)], (B, D), (C, D)), new MergeTarget.Join(D), [B, C]));
    }
}
