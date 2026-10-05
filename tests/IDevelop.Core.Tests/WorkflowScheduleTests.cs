using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Workflows;
using static IDevelop.TestSupport.TestNodes;

namespace IDevelop.Core.Tests;

/// <summary>Readiness along dependency connections, from each node's latest attempt.</summary>
public class WorkflowScheduleTests
{
    private static readonly string[] Names = ["A", "B", "C", "D", "E", "F"];

    private static TaskId Id(string name) => new(Guid.Parse($"019b0000-0000-7000-8000-00000000000{Array.IndexOf(Names, name) + 1}"));

    private static string Name(TaskId id) => Names.Single(name => Id(name) == id);

    /// <summary>A workflow of the named nodes, with "A>B" for a dependency and "A~B" for a context connection.</summary>
    private static Workflow Graph(params string[] connections) => Graph(new Dictionary<string, Blueprint>(), connections);

    /// <summary>The same, with each node in <paramref name="types"/> placed from that blueprint instead of Implement.</summary>
    private static Workflow Graph(Dictionary<string, Blueprint> types, params string[] connections)
    {
        var workflow = Workflow.Empty(new WorkflowId(Guid.Parse("019b0000-0000-7000-8000-0000000000ff")));
        foreach (var name in Names)
        {
            workflow = workflow.Must(types.GetValueOrDefault(name) is { } type
                ? new WorkflowEdit.PlaceNode(Id(name), type, new CanvasPoint(0, 0)) { Title = name }
                : Place(Implement(Id(name), name), new CanvasPoint(0, 0)));
        }

        foreach (var connection in connections)
        {
            var kind = connection[1] == '>' ? ConnectionKind.Dependency : ConnectionKind.Context;
            workflow = workflow.Must(new WorkflowEdit.Connect(new ConnectionKey(Id(connection[..1]), Id(connection[2..])), kind));
        }

        return workflow;
    }

    private const string Findings =
        """{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add ignores b", "change": "return a + b"}], "withdrawn": []}""";

    /// <summary>An attempt of the named node that ends in <paramref name="status"/>. One in review reviews A and found a problem.</summary>
    private static AttemptRecord Attempt(string name, AttemptStatus status)
    {
        var at = AttemptEvents.T0;
        var requested = new AttemptEvent.Requested(at, AttemptId.New(), Id(name), name, AttemptEvents.CodexHigh, "p", "codex", [])
        {
            Conversation = status == AttemptStatus.WaitingForInput ? ConversationMode.Chat : ConversationMode.Autonomous,
        };
        AttemptEvent[] events = status switch
        {
            AttemptStatus.Running => [requested, AttemptEvents.LaunchedAt1s],
            AttemptStatus.Succeeded => [requested, Said(new AgentEvent.Succeeded("Done.")), new AttemptEvent.Exited(at, 0, "")],
            AttemptStatus.Failed => [requested, Said(new AgentEvent.Failed("No quota.")), new AttemptEvent.Exited(at, 1, "")],
            AttemptStatus.Cancelled => [requested, AttemptEvents.LaunchedAt1s, new AttemptEvent.CancelRequested(at), new AttemptEvent.Exited(at, 1, "")],
            AttemptStatus.Interrupted => [requested, AttemptEvents.LaunchedAt1s, new AttemptEvent.Reconciled(at, null)],
            AttemptStatus.WaitingForInput =>
                [requested, Said(new AgentEvent.SessionStarted("thread-1")), Said(new AgentEvent.Succeeded("Which fruit?")), new AttemptEvent.Exited(at, 0, "")],
            AttemptStatus.InReview => Reviewing(name, Findings),
        };
        return Replay(status, events);

        AttemptEvent Said(AgentEvent e) => new AttemptEvent.Agent(at, e);
    }

    /// <summary>A review by the named node of A whose first reviewer turn ended with this verdict block.</summary>
    private static AttemptEvent[] Reviewing(string name, string verdict)
    {
        var at = AttemptEvents.T0;
        return
        [
            new AttemptEvent.Requested(at, AttemptId.New(), Id(name), name, AttemptEvents.CodexHigh, "p", "codex", []) { Subject = Id("A") },
            new AttemptEvent.Agent(at, new AgentEvent.SessionStarted("thread-1")),
            new AttemptEvent.Agent(at, new AgentEvent.Succeeded($"I read the change.\n\n```idevelop\n{verdict}\n```")),
            new AttemptEvent.Exited(at, 0, ""),
        ];
    }

    private static AttemptRecord Replay(AttemptStatus status, AttemptEvent[] events)
    {
        var record = AttemptReducer.Replay(events)!;
        Assert.Equal(status, record.Status);
        return record;
    }

    /// <summary>The ready nodes, then each blocked node with its holders, a holder that has not started as "ready".</summary>
    private static (string[] Ready, Dictionary<string, string> Blocked) Schedule(Workflow workflow, params (string Name, AttemptStatus Status)[] attempts)
    {
        var schedule = WorkflowSchedule.Of(workflow, attempts.ToDictionary(a => Id(a.Name), a => Attempt(a.Name, a.Status)));
        return (
            [.. schedule.Ready.Select(Name)],
            schedule.Blocked.ToDictionary(
                b => Name(b.Key),
                b => string.Join(", ", b.Value.Select(holder => $"{Name(holder.Key)} {holder.Value?.ToString() ?? "ready"}"))));
    }

    [Fact]
    public void A_diamond_waits_for_both_predecessors()
    {
        var diamond = Graph("A>B", "A>C", "B>D", "C>D");

        var fresh = Schedule(diamond);
        var oneSide = Schedule(diamond, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.Succeeded), ("C", AttemptStatus.Running));
        var bothSides = Schedule(diamond, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.Succeeded), ("C", AttemptStatus.Succeeded));

        Assert.Equal(["A", "E", "F"], fresh.Ready);
        Assert.Equal(new Dictionary<string, string> { ["B"] = "A ready", ["C"] = "A ready", ["D"] = "A ready" }, fresh.Blocked);
        Assert.Equal(["E", "F"], oneSide.Ready);
        Assert.Equal(new Dictionary<string, string> { ["D"] = "C Running" }, oneSide.Blocked);
        Assert.Equal(["D", "E", "F"], bothSides.Ready);
        Assert.Empty(bothSides.Blocked);
    }

    [Fact]
    public void A_context_connection_does_not_hold_a_node_back()
    {
        var workflow = Graph("A~B", "A>C", "C~D");

        var fresh = Schedule(workflow);
        var running = Schedule(workflow, ("A", AttemptStatus.Running), ("C", AttemptStatus.Failed));

        Assert.Equal(["A", "B", "D", "E", "F"], fresh.Ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "A ready" }, fresh.Blocked);
        Assert.Equal(["B", "D", "E", "F"], running.Ready);
        Assert.Empty(running.Blocked);
    }

    [Fact]
    public void A_failure_blocks_only_the_nodes_below_it_and_each_names_the_failed_node()
    {
        var workflow = Graph("A>B", "B>C", "D>E", "B>F", "E>F");

        var (ready, blocked) = Schedule(workflow, ("A", AttemptStatus.Failed), ("D", AttemptStatus.Succeeded));

        Assert.Equal(["E"], ready);
        Assert.Equal(new Dictionary<string, string> { ["B"] = "A Failed", ["C"] = "A Failed", ["F"] = "A Failed, E ready" }, blocked);
    }

    [Fact]
    public void A_node_below_two_failures_names_both()
    {
        var (ready, blocked) = Schedule(Graph("A>C", "B>C", "C>D"), ("A", AttemptStatus.Failed), ("B", AttemptStatus.Failed));

        Assert.Equal(["E", "F"], ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "A Failed, B Failed", ["D"] = "A Failed, B Failed" }, blocked);
    }

    [Fact]
    public void A_node_that_waits_for_the_person_holds_back_its_dependents()
    {
        var (ready, blocked) = Schedule(Graph("A>B", "B>C"), ("A", AttemptStatus.WaitingForInput));

        Assert.Equal(["D", "E", "F"], ready);
        Assert.Equal(new Dictionary<string, string> { ["B"] = "A WaitingForInput", ["C"] = "A WaitingForInput" }, blocked);
    }

    [Theory]
    [InlineData(AttemptStatus.Cancelled, "A Cancelled")]
    [InlineData(AttemptStatus.Interrupted, "A Interrupted")]
    public void A_node_that_ended_without_a_handoff_does_not_release_its_dependents(AttemptStatus status, string holder)
    {
        var (ready, blocked) = Schedule(Graph("A>B"), ("A", status));

        Assert.Equal(["C", "D", "E", "F"], ready);
        Assert.Equal(new Dictionary<string, string> { ["B"] = holder }, blocked);
    }

    [Fact]
    public void A_node_with_an_attempt_is_neither_ready_nor_blocked_whatever_its_predecessors_did()
    {
        var (ready, blocked) = Schedule(Graph("A>B", "C>D"), ("B", AttemptStatus.Succeeded), ("D", AttemptStatus.Running));

        Assert.Equal(["A", "C", "E", "F"], ready);
        Assert.Empty(blocked);
    }

    [Theory]
    [InlineData(AttemptStatus.Succeeded, true)]
    [InlineData(AttemptStatus.Running, false)]
    [InlineData(AttemptStatus.WaitingForInput, false)]
    [InlineData(AttemptStatus.InReview, false)]
    [InlineData(AttemptStatus.Failed, false)]
    [InlineData(AttemptStatus.Cancelled, false)]
    [InlineData(AttemptStatus.Interrupted, false)]
    public void Only_a_succeeded_attempt_hands_on(AttemptStatus status, bool handedOn)
    {
        Assert.Equal(handedOn, WorkflowSchedule.HandedOn(Attempt("A", status)));
    }

    [Fact]
    public void A_node_without_an_attempt_has_not_handed_on()
    {
        Assert.False(WorkflowSchedule.HandedOn(null));
    }

    public static TheoryData<string, AttemptStatus> SettledOrResting
    {
        get
        {
            var data = new TheoryData<string, AttemptStatus>();
            foreach (var type in new[] { "Implement", "Review", "Approval" })
            {
                foreach (var status in Enum.GetValues<AttemptStatus>().Where(status => status != AttemptStatus.Running))
                {
                    data.Add(type, status);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SettledOrResting))]
    public void A_node_hands_on_exactly_when_its_work_finishes(string type, AttemptStatus status)
    {
        var node = new TaskDefinition(Id("B"), BuiltInBlueprints.All.Single(blueprint => blueprint.Name == type));
        var attempt = Attempt("B", status);
        var step = NodeWorks.For(node.Blueprint.Work).Next(new NodeContext(node, ""), attempt);

        Assert.Equal(step is NodeStep.Finish, WorkflowSchedule.HandedOn(attempt));
    }

    [Fact]
    public void A_review_whose_reviewer_approved_hands_on_once_the_approval_is_recorded()
    {
        var review = new TaskDefinition(Id("B"), BuiltInBlueprints.Review);
        var resting = Reviewing("B", """{"status": "verdict", "verdict": "approve", "findings": [], "withdrawn": []}""");
        var approved = Replay(AttemptStatus.InReview, resting);
        var concluded = Replay(AttemptStatus.Succeeded, [.. resting, new AttemptEvent.Concluded(AttemptEvents.T0, null)]);

        Assert.IsType<NodeStep.Finish>(ReviewWork.Instance.Next(new NodeContext(review, ""), approved));
        Assert.False(WorkflowSchedule.HandedOn(approved));
        Assert.True(WorkflowSchedule.HandedOn(concluded));
    }

    [Fact]
    public void A_review_holds_back_its_own_dependents_until_it_concludes_and_its_subject_releases_its_own()
    {
        var workflow = Graph(new Dictionary<string, Blueprint> { ["B"] = BuiltInBlueprints.Review }, "A>B", "B>C", "A>D");

        var fresh = Schedule(workflow);
        var inReview = Schedule(workflow, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.InReview));
        var fixing = Schedule(workflow, ("A", AttemptStatus.Running), ("B", AttemptStatus.InReview));
        var approved = Schedule(workflow, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.Succeeded));
        var failed = Schedule(workflow, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.Failed));

        Assert.Equal(["A", "E", "F"], fresh.Ready);
        Assert.Equal(new Dictionary<string, string> { ["B"] = "A ready", ["C"] = "A ready", ["D"] = "A ready" }, fresh.Blocked);
        Assert.Equal(["D", "E", "F"], inReview.Ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "B InReview" }, inReview.Blocked);
        Assert.Equal(["E", "F"], fixing.Ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "B InReview", ["D"] = "A Running" }, fixing.Blocked);
        Assert.Equal(["C", "D", "E", "F"], approved.Ready);
        Assert.Empty(approved.Blocked);
        Assert.Equal(["D", "E", "F"], failed.Ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "B Failed" }, failed.Blocked);
    }

    [Fact]
    public void An_approval_holds_back_its_dependents_until_the_person_approves()
    {
        var workflow = Graph(new Dictionary<string, Blueprint> { ["B"] = BuiltInBlueprints.Approval }, "A>B", "B>C");

        var reached = Schedule(workflow, ("A", AttemptStatus.Succeeded));
        var waiting = Schedule(workflow, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.WaitingForInput));
        var approved = Schedule(workflow, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.Succeeded));
        var sentBack = Schedule(workflow, ("A", AttemptStatus.Succeeded), ("B", AttemptStatus.Failed));

        Assert.Equal(["B", "D", "E", "F"], reached.Ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "B ready" }, reached.Blocked);
        Assert.Equal(["D", "E", "F"], waiting.Ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "B WaitingForInput" }, waiting.Blocked);
        Assert.Equal(["C", "D", "E", "F"], approved.Ready);
        Assert.Empty(approved.Blocked);
        Assert.Equal(["D", "E", "F"], sentBack.Ready);
        Assert.Equal(new Dictionary<string, string> { ["C"] = "B Failed" }, sentBack.Blocked);
    }

    [Fact]
    public void An_attempt_of_a_task_outside_the_workflow_changes_nothing()
    {
        var workflow = Graph("A>B");
        var outside = new TaskId(Guid.Parse("019b0000-0000-7000-8000-0000000000ee"));
        var failed = Attempt("A", AttemptStatus.Failed);

        var schedule = WorkflowSchedule.Of(workflow, new Dictionary<TaskId, AttemptRecord> { [outside] = failed });

        Assert.Equal(["A", "C", "D", "E", "F"], schedule.Ready.Select(Name));
        Assert.Equal(["B"], schedule.Blocked.Keys.Select(Name));
    }
}
