using IDevelop.Desktop.Canvas;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Tests;

/// <summary>An expanded card's first section and its peek's (ADR 0024): what each engine state shows, from what the engine holds.</summary>
public sealed class CardNowTests
{
    private static CardNow Of(EngineState state, string status = "", string? reply = null, string? report = null, string? detail = null,
        NodeRole role = NodeRole.None, string? proposal = null, string? review = null, string? field = "Draft the format.") =>
        CardNow.Of(new CardFacts(state, status)
        {
            LastReply = reply, Report = report, Detail = detail, Role = role, Proposal = proposal, Review = review,
            FieldLabel = "Instructions", FieldText = field,
        });

    [Fact]
    public void A_running_node_shows_the_agents_latest_message()
    {
        var now = Of(new EngineState.Running(), reply: "Reading the format.\n\nThree of five tests pass.");

        Assert.Equal((CardNowKind.Message, "Latest message", "Three of five tests pass."), (now.Kind, now.Heading, now.Text));
        Assert.Equal("No message yet.", Of(new EngineState.Running()).Text);
    }

    [Fact]
    public void A_completed_node_shows_its_results_first_paragraph_and_a_failed_one_why()
    {
        var result = Of(new EngineState.Completed(), report: "# Report\n\nOne JSON file per workflow.\n\n## Decisions\n\nStable keys.");
        var failed = Of(new EngineState.Failed("The test host could not start."), status: "Failed", detail: "The test host could not start.");

        Assert.Equal((CardNowKind.Result, "Result", "One JSON file per workflow."), (result.Kind, result.Heading, result.Text));
        Assert.Equal((CardNowKind.Stopped, "Failed", "The test host could not start."), (failed.Kind, failed.Heading, failed.Text));
        Assert.Equal("Approved", Of(new EngineState.Completed(), report: "Fine.", review: "Approved").Text);
    }

    [Fact]
    public void A_node_that_has_not_run_shows_its_first_field()
    {
        var idle = Of(new EngineState.Idle());

        Assert.Equal((CardNowKind.Task, "Instructions", "Draft the format."), (idle.Kind, idle.Heading, idle.Text));
        Assert.Equal("Nothing written yet.", Of(new EngineState.Idle(), field: " ").Text);
    }

    [Fact]
    public void An_approval_and_a_reviewer_at_its_round_limit_wait_for_approval_and_an_open_proposal_points_to_the_inspector()
    {
        var proposal = Of(new EngineState.InputRequired(EngineState.Reply), role: NodeRole.Proposing, proposal: "Proposal · 3 tasks");

        Assert.Equal(CardNowKind.Approval, Of(new EngineState.InputRequired(EngineState.Approval)).Kind);
        Assert.Equal(CardNowKind.Approval, Of(new EngineState.InputRequired(EngineState.RoundLimit)).Kind);
        Assert.Equal((CardNowKind.Proposal, "Proposal · 3 tasks. Review the tasks in the inspector, then accept or dismiss them."),
            (proposal.Kind, proposal.Text));
    }

    [Fact]
    public void A_reply_and_a_question_take_an_answer_and_an_interruption_or_a_conflict_says_what_to_do()
    {
        var reply = Of(new EngineState.InputRequired(EngineState.Reply), reply: "I read the code.\n\nWhich file?");
        var question = Of(new EngineState.InputRequired("Which branch?"));
        var interrupted = Of(new EngineState.InputRequired(EngineState.Interrupted), status: "Input required · interrupted", detail: "Continue or retry.");

        Assert.Equal((CardNowKind.Reply, "The agent replied", "Which file?"), (reply.Kind, reply.Heading, reply.Text));
        Assert.Equal((CardNowKind.Question, "The agent asks", "Which branch?"), (question.Kind, question.Heading, question.Text));
        Assert.Equal((CardNowKind.Stopped, "Input required · interrupted", "Continue or retry."), (interrupted.Kind, interrupted.Heading, interrupted.Text));
    }

    [Fact]
    public void A_stale_result_says_why_it_no_longer_counts()
    {
        var stale = Of(new EngineState.Stale(), detail: "What it ran on changed.");

        Assert.Equal((CardNowKind.Stale, "Stale", "What it ran on changed."), (stale.Kind, stale.Heading, stale.Text));
    }

    [Fact]
    public void A_reply_shows_its_last_paragraph_where_the_agent_asks_and_a_result_its_first()
    {
        Assert.Equal("Which file?", CardNow.LastParagraph("I read the code.\n\nWhich file?"));
        Assert.Equal("I read the code.", CardNow.FirstParagraph("# Title\n\nI read the code.\n\nWhich file?"));
        Assert.Null(CardNow.FirstParagraph("  "));
    }
}
