using IDevelop.Clients;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Tests;

/// <summary>What a card shows of each engine state (ADR 0017): its state class, its status, its tone, and what it needs.</summary>
public sealed class NodeStateTests
{
    public static TheoryData<EngineState, NodeState, string, StatusTone> States() => new()
    {
        { new EngineState.Idle(), NodeState.Idle, "Idle", StatusTone.Neutral },
        { new EngineState.Running(), NodeState.Running, "Running", StatusTone.Running },
        { new EngineState.InputRequired(EngineState.Approval), NodeState.InputRequired, "Input required · approval", StatusTone.Waiting },
        { new EngineState.InputRequired(EngineState.Reply), NodeState.InputRequired, "Input required · reply", StatusTone.Waiting },
        { new EngineState.InputRequired(EngineState.Conflict), NodeState.InputRequired, "Input required · conflict", StatusTone.Waiting },
        { new EngineState.InputRequired(EngineState.Interrupted), NodeState.InputRequired, "Input required · interrupted", StatusTone.Waiting },
        { new EngineState.InputRequired(EngineState.RoundLimit), NodeState.InputRequired, "Input required · round limit", StatusTone.Waiting },
        { new EngineState.InputRequired("Which file?"), NodeState.InputRequired, "Input required · question", StatusTone.Waiting },
        { new EngineState.Completed(), NodeState.Completed, "Completed", StatusTone.Complete },
        { new EngineState.Failed("Exited with code 2."), NodeState.Failed, "Failed", StatusTone.Problem },
        { new EngineState.Stale(), NodeState.Stale, "Stale", StatusTone.Warning },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Each_engine_state_maps_onto_one_card_state_whose_status_names_why_it_needs_the_person(
        EngineState engine, NodeState state, string label, StatusTone tone)
    {
        Assert.Equal(state, NodeStates.Of(engine));
        Assert.Equal(label, NodeStates.Label(engine));
        Assert.Equal(tone, NodeStates.Tone(state));
    }

    [Fact]
    public void A_running_turn_says_what_background_work_it_waits_for()
    {
        var running = new EngineState.Running();

        Assert.Equal("Running", NodeStates.Label(running, []));
        Assert.Equal("Waiting for a background subagent", NodeStates.Label(running,
            [new BackgroundTask("Run the tests", IsSubagent: false), new BackgroundTask("Survey the files", IsSubagent: true)]));
        Assert.Equal("Waiting for background work", NodeStates.Label(running, [new BackgroundTask("Run the tests", IsSubagent: false)]));
        Assert.Equal("Completed", NodeStates.Label(new EngineState.Completed(), [new BackgroundTask("Survey the files", IsSubagent: true)]));
    }

    [Fact]
    public void The_detail_says_what_the_person_does_next_and_a_question_or_a_failure_in_its_own_words()
    {
        Assert.Null(NodeStates.Detail(new EngineState.Idle()));
        Assert.Null(NodeStates.Detail(new EngineState.Completed()));
        Assert.Equal("Which file?", NodeStates.Detail(new EngineState.InputRequired("Which file?")));
        Assert.Equal("Exited with code 2.", NodeStates.Detail(new EngineState.Failed("Exited with code 2.")));
        Assert.StartsWith("Approve what the nodes before it handed on", NodeStates.Detail(new EngineState.InputRequired(EngineState.Approval)));
        Assert.Contains("mark it resolved", NodeStates.Detail(new EngineState.InputRequired(EngineState.Conflict)));
        Assert.Contains("Continue the session, or retry", NodeStates.Detail(new EngineState.InputRequired(EngineState.Interrupted)));
        Assert.Contains("Run it again", NodeStates.Detail(new EngineState.Stale()));
    }
}
