using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>An expanded card's first section and its peek's (ADR 0024): what each state shows, from what the app records.</summary>
public sealed class CardNowTests
{
    private static CardNow Of(NodeState state, AttemptRecord? latest = null, string status = "", string? detail = null, bool waits = false,
        NodeRole role = NodeRole.None, bool hasAgent = true, string? proposal = null, string? review = null, string? field = "Draft the format.") =>
        CardNow.Of(state, role, hasAgent, latest, status, detail, waits, proposal, review, "Instructions", field);

    [Fact]
    public void A_running_task_shows_the_agents_latest_message_and_counts_its_tool_calls()
    {
        var running = Record(A, At(1, new AgentEvent.Message("Reading the format.")), At(2, new AgentEvent.ToolStarted("command", "dotnet test")),
            At(3, new AgentEvent.Message("Three of five tests pass.")));

        var now = Of(NodeState.Running, running);

        Assert.Equal((CardNowKind.Message, "Latest message", "Three of five tests pass."), (now.Kind, now.Heading, now.Text));
        Assert.StartsWith("1 tool call · since ", now.Meta);
        Assert.Equal("No message yet.", Of(NodeState.Running, Record(A)).Text);
    }

    [Fact]
    public void A_finished_task_shows_its_results_first_paragraph_and_a_stopped_one_why()
    {
        var done = Record(A, At(1, new AgentEvent.Succeeded("# Report\n\nOne JSON file per workflow.\n\n## Decisions\n\nStable keys.")), Exited(5));
        var result = Of(NodeState.Succeeded, done);
        var failed = Of(NodeState.Failed, status: "Failed", detail: "The test host could not start.");

        Assert.Equal((CardNowKind.Result, "Result", "One JSON file per workflow."), (result.Kind, result.Heading, result.Text));
        Assert.Equal((CardNowKind.Stopped, "Failed", "The test host could not start."), (failed.Kind, failed.Heading, failed.Text));
    }

    [Fact]
    public void A_task_that_has_not_run_shows_its_first_field_or_what_a_run_holds_it_for()
    {
        var idle = Of(NodeState.Idle);
        var held = Of(NodeState.Idle, waits: true);

        Assert.Equal((CardNowKind.Task, "Instructions", "Draft the format."), (idle.Kind, idle.Heading, idle.Text));
        Assert.Equal("Nothing written yet.", Of(NodeState.Idle, field: " ").Text);
        Assert.Equal((CardNowKind.Waits, "Starts after", null), (held.Kind, held.Heading, held.Text));
    }

    [Fact]
    public void An_approval_and_an_open_proposal_point_to_the_inspector_where_the_person_answers_them()
    {
        var proposal = Of(NodeState.Succeeded, role: NodeRole.Proposing, proposal: "Proposal · 3 tasks");

        Assert.Equal(CardNowKind.Approval, Of(NodeState.Waiting, hasAgent: false).Kind);
        Assert.Equal((CardNowKind.Proposal, "Proposal · 3 tasks. Review the tasks in the inspector, then accept or dismiss them."),
            (proposal.Kind, proposal.Text));
    }

    [Fact]
    public void A_reply_shows_its_last_paragraph_where_the_agent_asks_and_a_result_its_first()
    {
        Assert.Equal("Which file?", CardNow.LastParagraph("I read the code.\n\nWhich file?"));
        Assert.Equal("I read the code.", CardNow.FirstParagraph("# Title\n\nI read the code.\n\nWhich file?"));
        Assert.Null(CardNow.FirstParagraph("  "));
    }
}
