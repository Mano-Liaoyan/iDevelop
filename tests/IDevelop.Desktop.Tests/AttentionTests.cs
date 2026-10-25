using IDevelop.Desktop.Conversation;
using EngineState = IDevelop.Engine.NodeState;

namespace IDevelop.Desktop.Tests;

public sealed class AttentionTests
{
    [Fact]
    public void A_wait_for_the_person_and_a_failure_need_attention_and_every_other_state_needs_none()
    {
        Assert.Equal(new Attention.Waiting("Waiting for you: Which fixture?"), Attention.Of(new EngineState.InputRequired("Which fixture?")));
        Assert.Equal(new Attention.Problem("Failed: The model is not available."), Attention.Of(new EngineState.Failed("The model is not available.")));
        Assert.Null(Attention.Of(new EngineState.Completed()));
        Assert.Null(Attention.Of(new EngineState.Running()));
        Assert.Null(Attention.Of(new EngineState.Stale()));
        Assert.Null(Attention.Of(new EngineState.Idle()));
    }
}
