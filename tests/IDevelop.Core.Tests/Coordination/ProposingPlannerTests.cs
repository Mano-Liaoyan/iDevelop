using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.AmendmentTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A run's planner whose turn ends with a proposal waits for the person, who accepts it from the app (#84).</summary>
public sealed class ProposingPlannerTests
{
    private static readonly OperationId Accept = new(Guid.Parse("00000000-0000-0000-0000-000000084001"));

    /// <summary>The planner A, then B after it.</summary>
    private static CoordinatorFixture Fixture() => new CoordinatorFixture(Graph([Planner(A), Agent(B)], (A, B)))
        .Answer(A, Proposes(A, AddsX)).Answer(B, Writes(B, "b.txt", "B\n"))
        .Answer("X again", FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-X")).Write("x.txt", "X\n")
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "X ready.\n")));

    [Fact]
    public async Task A_planner_whose_turn_ends_with_a_proposal_waits_and_the_task_after_it_starts_once_it_is_marked_done()
    {
        await using var f = Fixture();
        await f.Open();
        await f.Resume();

        var waiting = await f.Until(view => view.Tasks[A].State == TaskState.Waiting);

        Assert.Equal(AttemptStatus.WaitingForInput, waiting.Tasks[A].Status);
        Assert.Equal(TaskState.Pending, waiting.Tasks[B].State);
        var (_, log, proposal) = Proposed(f);
        Assert.IsType<Pending.Proposal>(log.Pending);
        var done = await f.Coordinator.MarkDone(f.Address, A, new(proposal.Attempt, proposal.Turn)).WaitAsync(Bound);
        Assert.Equal(CommandOutcome.Applied, done.Outcome);
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches(B));
    }

    [Fact]
    public async Task The_planners_proposal_reads_against_the_runs_revision_and_says_once_it_is_accepted()
    {
        await using var f = Fixture();
        await f.Open();
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Waiting);
        var original = f.Read().Revision.Id;

        var open = f.Coordinator.PlannerProposal(A);

        Assert.NotNull(open);
        var proposal = Assert.IsType<ProposalRead.Ready>(open.Read).Proposal;
        Assert.Equal(RunJournal.Canonical(Proposed(f).Proposal), RunJournal.Canonical(proposal));
        Assert.Equal(original, open.Revision);
        Assert.False(open.Accepted);
        Assert.Null(f.Coordinator.PlannerProposal(B));
        var added = proposal.Nodes.Single(node => node.Name == "new-1").Id;
        Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Amend(f.Address, new(original, proposal, [added], f.Read().Revision.Snapshot.Tasks[A].Execution, Accept)).WaitAsync(Bound));

        var accepted = f.Coordinator.PlannerProposal(A);

        Assert.True(accepted!.Accepted);
        Assert.Equal(f.Read().Revision.Id, accepted.Revision);
        await f.Coordinator.MarkDone(f.Address, A, new(proposal.Attempt, proposal.Turn)).WaitAsync(Bound);
        await f.UntilStatus(RunStatus.Completed);
    }

    [Fact]
    public async Task The_document_follows_an_accepted_amendment_with_one_edit()
    {
        await using var f = Fixture();
        await f.Open();
        var document = f.Read().Revision.Snapshot;
        await f.Resume();
        await f.Until(view => view.Tasks[A].State == TaskState.Waiting);
        Assert.IsType<AmendmentProjection.Current>(f.Coordinator.Follow(document));
        var proposal = Proposed(f).Proposal;
        var added = proposal.Nodes.Single(node => node.Name == "new-1").Id;
        await f.Coordinator.Amend(f.Address, new(f.Read().Revision.Id, proposal, [added], f.Read().Revision.Snapshot.Tasks[A].Execution, Accept)).WaitAsync(Bound);

        var follow = Assert.IsType<AmendmentProjection.Apply>(f.Coordinator.Follow(document));

        var applied = Assert.IsType<EditResult.Applied>(document.Apply(follow.Edit)).Workflow;
        Assert.Equal("Build X again.", applied.Tasks[added].Field("brief"));
        await f.Coordinator.MarkDone(f.Address, A, new(proposal.Attempt, proposal.Turn)).WaitAsync(Bound);
        await f.UntilStatus(RunStatus.Completed);
    }

    [Fact]
    public async Task A_proposal_still_amends_the_run_after_the_person_marked_its_planner_done()
    {
        await using var f = Fixture();
        await f.Open();
        // One root at a time, and B's preparation held, so the run stays open until the amendment is recorded.
        f.Runs.ClientRoots = 1;
        var original = f.Read().Revision.Id;
        var (reached, release) = HoldSecond(f, "journal.prepared.before");
        await f.Resume();
        await HandOn(f, A);
        await reached.Task.WaitAsync(Bound);
        var proposal = Proposed(f).Proposal;
        var added = proposal.Nodes.Single(node => node.Name == "new-1").Id;

        var amended = await f.Coordinator.Amend(f.Address, new(original, proposal, [added], f.Read().Revision.Snapshot.Tasks[A].Execution, Accept))
            .WaitAsync(Bound);

        Assert.IsType<RunCommand.Accepted>(amended);
        release.TrySetResult();
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(1, f.Launches("X again"));
    }
}
