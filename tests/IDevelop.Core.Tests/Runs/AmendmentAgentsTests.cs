using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>
/// An amendment gives each task it adds or fills the agent the person's review shows, in the order Generate Workflow
/// uses: the person's choice, then the task's own agent, then the planner's choice, then the fallback (#99).
/// </summary>
public sealed class AmendmentAgentsTests
{
    private static readonly ExecutionSettings Planned = new(ClientId.Codex) { Model = "gpt-6.1-sol", Reasoning = "xhigh" };
    private static readonly ExecutionSettings Mine = new(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "high" };
    private static readonly ExecutionSettings Fallback = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" };
    private static readonly ExecutionSettings Own = new(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "low" };

    private static readonly Blueprint Planner = new(new("example.planner", 1), "Planner",
        new WorkSpec.Agent(AgentAccess.ReadOnly, true, PromptTemplate.Parse("{{brief}}")),
        [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(Task().Execution, ConversationMode.Autonomous));

    /// <summary>A type with an agent of its own, which a task of the workflow uses so the run's revision holds it.</summary>
    private static readonly Blueprint Careful = new(new("team.careful", 1), "Careful", BuiltInBlueprints.Implement.Work,
        BuiltInBlueprints.Implement.Fields, new(Own, ConversationMode.Autonomous));

    /// <summary>
    /// Fills U, which has no agent, and adds an Implement the planner chose for, a Careful it chose for, and an Implement
    /// without a choice.
    /// </summary>
    private const string Proposed = """
        ```idevelop
        {"status": "proposal",
         "fill": [{"slot": "slot-1", "fields": {"brief": "Check it."}, "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "xhigh"}}],
         "add": [{"id": "chosen", "type": "type-1", "title": "Chosen", "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "xhigh"}},
                 {"id": "careful", "type": "type-2", "title": "Careful", "agent": {"client": "codex", "model": "gpt-6.1-sol", "reasoning": "xhigh"}},
                 {"id": "none", "type": "type-1", "title": "None"}]}
        ```
        """;

    [Fact]
    public void Each_task_takes_the_persons_choice_else_its_own_agent_else_the_planners_choice_else_the_fallback()
    {
        using var f = Proposing(out var proposal, out var previous);
        var (chosen, careful, none) = (New(proposal, "chosen"), New(proposal, "careful"), New(proposal, "none"));
        var planned = new Dictionary<TaskId, ExecutionSettings> { [U] = Planned, [chosen] = Planned, [careful] = Planned };

        var amended = Assert.IsType<RunDecision.Recorded>(f.Store.AmendFromProposal(f.Permit, f.Op(), previous, proposal, proposal.Items.ToHashSet(),
            f.Op(), Fallback, planned, new Dictionary<TaskId, ExecutionSettings> { [none] = Mine })).Record.Revision.Snapshot;

        Assert.Equal([Planned, Planned, Own, Mine], new[] { U, chosen, careful, none }.Select(task => amended.Tasks[task].Execution));
    }

    [Fact]
    public void Without_choices_each_task_without_an_agent_of_its_own_takes_the_fallback_and_a_fill_too()
    {
        using var f = Proposing(out var proposal, out var previous);

        var amended = Assert.IsType<RunDecision.Recorded>(f.Store.AmendFromProposal(f.Permit, f.Op(), previous, proposal, proposal.Items.ToHashSet(),
            f.Op(), Fallback)).Record.Revision.Snapshot;

        Assert.Equal([Fallback, Fallback, Own, Fallback],
            new[] { U, New(proposal, "chosen"), New(proposal, "careful"), New(proposal, "none") }.Select(task => amended.Tasks[task].Execution));
    }

    [Fact]
    public void Without_a_fallback_a_fill_without_a_choice_keeps_its_task_as_it_is()
    {
        using var f = Proposing(out var proposal, out var previous);

        var amended = Assert.IsType<RunDecision.Recorded>(f.Store.AmendFromProposal(f.Permit, f.Op(), previous, proposal, proposal.Items.ToHashSet(),
            f.Op())).Record.Revision.Snapshot;

        Assert.Equal([null, null, Own, null],
            new[] { U, New(proposal, "chosen"), New(proposal, "careful"), New(proposal, "none") }.Select(task => amended.Tasks[task].Execution));
    }

    [Fact]
    public void The_amendment_records_each_choice_so_a_repeat_converges_and_other_choices_are_another_acceptance()
    {
        using var f = Proposing(out var proposal, out var previous);
        var planned = new Dictionary<TaskId, ExecutionSettings> { [New(proposal, "chosen")] = Planned, [U] = Planned };
        var changed = new Dictionary<TaskId, ExecutionSettings> { [New(proposal, "none")] = Mine };
        var items = proposal.Items.ToHashSet();

        Assert.IsType<RunDecision.Recorded>(f.Store.AmendFromProposal(f.Permit, f.Op(), previous, proposal, items, f.Op(), null, planned, changed));
        // The same choices in another order are the same acceptance.
        var repeated = f.Store.AmendFromProposal(f.Permit, f.Op(), previous, proposal, items, f.Op(), null,
            new Dictionary<TaskId, ExecutionSettings> { [U] = Planned, [New(proposal, "chosen")] = Planned }, changed);
        var other = f.Store.AmendFromProposal(f.Permit, f.Op(), previous, proposal, items, f.Op(), null, planned,
            new Dictionary<TaskId, ExecutionSettings> { [New(proposal, "none")] = Fallback });

        Assert.IsType<RunDecision.Existing>(repeated);
        Assert.Equal(RunProblem.RevisionConflict, Problem(other));
        var recorded = Assert.Single(f.Read().Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Amended>()).Choice!;
        Assert.Equal([U, New(proposal, "chosen")], recorded.Planned!.Value.Select(agent => agent.Task).Order());
        Assert.All(recorded.Planned!.Value, agent => Assert.Equal(Planned, agent.Agent));
        Assert.Equal(new ChosenAgent(New(proposal, "none"), Mine), Assert.Single(recorded.Changed!.Value));
    }

    [Fact]
    public void An_amendment_without_agents_records_its_choice_as_before()
    {
        using var f = Proposing(out var proposal, out var previous);

        var (operation, confirmation) = (f.Op(), f.Op());
        Assert.IsType<RunDecision.Recorded>(f.Store.AmendFromProposal(f.Permit, operation, previous, proposal, proposal.Items.ToHashSet(), confirmation, Fallback));

        var choice = RunJournal.Canonical(Assert.Single(f.Read().Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Amended>()).Choice!);
        Assert.StartsWith("{\"chosen\":", choice);
        Assert.DoesNotContain("\"planned\"", choice);
        Assert.DoesNotContain("\"changed\"", choice);
        // The command is the one an earlier build recorded, so an acceptance it began repeats as the same command.
        var command = new
        {
            command = "amendProposal",
            content = new { previous, proposal, chosen = proposal.Items.Order().ToArray(), confirmation, fallback = (ExecutionSettings?)Fallback },
        };
        Assert.Equal(Revision.Hash(RunJournal.Canonical(command)), f.Read().Receipts[operation].Command);
    }

    /// <summary>A run whose planner T reaches U, which has no agent, and whose turn closed with <see cref="Proposed"/>.</summary>
    private static RunFixtures Proposing(out Proposal proposal, out RevisionId previous)
    {
        var workflow = Connect(FixtureWorkflow(
            new TaskDefinition(T, Planner) { Title = "Plan" },
            Task(U).WithField("brief", "")! with { Execution = null, Title = "U" },
            new TaskDefinition(D, Careful) { Title = "Careful" }.WithField("instructions", "Be careful.")!), T, U);
        var f = new RunFixtures(workflow);
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var folder = f.Store.AttemptFolder(W, Run, T, reservation.Attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(At, reservation.Attempt.Id, T,
            "Plan", Task().Execution!, "Inspect", "codex", [])
        {
            RunBinding = new(W, Run, reservation.Attempt.Revision, reservation.Inputs.Id),
            Conversation = ConversationMode.Autonomous,
            ReadOnly = true,
            Planning = new(Id(700), [U], [BuiltInBlueprints.Implement.Key, Careful.Key]),
        }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded(Proposed)));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }

        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), new(reservation.Attempt.Id, 1), Checkpoint(folder)));
        previous = reservation.Attempt.Revision;
        var snapshot = f.Read().Revision.Snapshot;
        proposal = Assert.IsType<ProposalRead.Ready>(Proposal.Read(AttemptEvidence.Read(folder).Record!, key => RunPlanning.Find(snapshot, key))).Proposal;
        return f;
    }

    private static TaskId New(Proposal proposal, string name) => proposal.Nodes.Single(node => node.Name == name).Id;
}
