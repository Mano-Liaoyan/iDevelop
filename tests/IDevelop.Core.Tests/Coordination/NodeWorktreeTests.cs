using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Coordination.RunReviewTests;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// Each node of a run works in its own worktree (ADR 0032): an editing node on its own branch with one commit per turn,
/// and a read-only node detached at the merge of its predecessors, committing nothing.
/// </summary>
public sealed class NodeWorktreeTests
{
    /// <summary>The review node, as in <see cref="RunReviewTests"/>.</summary>
    private static readonly TaskId R = C;

    /// <summary>The approval node.</summary>
    private static readonly TaskId G = D;

    private const string Approve = """{"status": "verdict", "verdict": "approve", "findings": []}""";

    [Fact]
    public async Task Plan_implement_review_and_approval_give_only_the_editing_node_a_branch_and_one_commit()
    {
        await using var f = new RunConversationFixture(Graph([
            RunConversationFixture.Agent(X, ConversationMode.Autonomous, readOnly: true),
            RunConversationFixture.Agent(A, ConversationMode.Autonomous), Reviewer(R),
            new TaskDefinition(G, BuiltInBlueprints.Approval) { Title = "Gate" }], (X, A), (A, R), (R, G)));
        Routed(f).Answer(X, f.Says(X, 1, "Plan ready.\n", "session-x"))
            .Answer(A, Writes(f, A, 1, "calc.txt", "a + b\n", "A ready.\n"))
            .Answer(R, Reads(f, 1, Approve));
        await f.Open();
        await f.Resume();
        var waiting = await f.Until(view => view.Tasks[G].Gate is { Status: GateStatus.Waiting });
        var gate = waiting.Tasks[G].Gate!;
        Assert.IsType<GateReply.Recorded>(await f.Coordinator.Approve(f.Address, new(gate.Request.Id, gate.Request.Inputs), new(Guid.NewGuid())).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);

        var record = f.Read();
        var git = f.Preparation.Git;
        WorktreeOwner Owner(TaskId task) => record.Preparations.Values.Single(prepared => prepared.Location.Owner.Task == task).Location.Owner;
        var implement = Owner(A);
        Assert.Equal(RunLayout.TaskBranch(record.RunKey!, record.TaskKeys[A]), implement.Branch);
        Assert.Null(Owner(X).Branch);
        Assert.Null(Owner(R).Branch);
        Assert.Equal(implement.Branch, git.Git("for-each-ref", "--format=%(refname)", $"refs/heads/idp/{record.RunKey}/task/").Trim());

        // Implement's turn ended with one commit on its branch, and that commit is its result.
        var produced = Assert.IsType<CodeOutput.Produced>(record.CurrentResults[A].Code).Code.Commit;
        Assert.Equal(produced.Hex, git.Git("rev-parse", implement.Branch!).Trim());
        Assert.Equal("1", git.Git("rev-list", "--count", $"{PlanCommit.Hex}..{implement.Branch}").Trim());
        Assert.Equal("a + b\n", git.Git("show", produced.Hex + ":calc.txt"));
        var implementCapture = record.Captures[record.Settlements.Single(pair => record.Attempts[pair.Key.Attempt].Task == A).Value];
        Assert.Equal(produced, Assert.Single(implementCapture).Candidate);

        // Review worked detached at Implement's commit, saw its edit, and committed nothing.
        var review = Path.Combine(git.Folder, Owner(R).RelativePath);
        Assert.Equal("a + b\n", File.ReadAllText(Path.Combine(f.Evidence, "review-1.calc.txt")));
        Assert.Equal(produced.Hex, git.Run(review, "rev-parse", "HEAD").Text.Trim());
        Assert.NotEqual(0, git.Run(review, "symbolic-ref", "-q", "HEAD").ExitCode);
        foreach (var task in new[] { X, R })
        {
            var capture = record.Captures[record.Settlements.Single(pair => record.Attempts[pair.Key.Attempt].Task == task).Value];
            Assert.Null(Assert.Single(capture).Candidate);
        }
        Assert.Equal(PlanCommit.Hex, git.Run(Path.Combine(git.Folder, Owner(X).RelativePath), "rev-parse", "HEAD").Text.Trim());
    }

    [Fact]
    public async Task Conflicting_editing_predecessors_block_their_read_only_successor_with_the_conflicting_paths()
    {
        await using var f = new CoordinatorFixture(Graph([Agent(B), Agent(C), Agent(D, readOnly: true)], (B, D), (C, D)));
        f.Answer(B, Writes(B, "settings.txt", "B\n")).Answer(C, Writes(C, "settings.txt", "C\n")).Answer(D, Reports(D));
        await f.Open();
        await f.Resume();
        var stuck = await f.UntilStatus(RunStatus.NeedsAttention);
        var block = stuck.Tasks[D].Block!;
        Assert.Equal(TaskState.Blocked, stuck.Tasks[D].State);
        Assert.Equal(MaterializationProblem.FanInConflict, block.Problem);
        Assert.Equal(["settings.txt"], block.Conflict!.Paths.ToArray());
        Assert.Equal(0, f.Launches(D));
    }
}
