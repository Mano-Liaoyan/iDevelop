using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.AttemptEvents;

namespace IDevelop.Core.Tests.Runs;

public sealed class AttemptCompatibilityTests
{
    [Fact]
    public void Synthetic_waiting_continuation_cancellation_and_review_records_preserve_their_outcomes()
    {
        var waiting = AttemptReducer.Replay([BuildRequested(First) with { Conversation = ConversationMode.Chat },
            Said(1, new AgentEvent.SessionStarted("session-1")), Said(2, new AgentEvent.Succeeded("Question?")), Exit(3, 0)])!;
        var continued = AttemptReducer.Replay([BuildRequested(new(RunFixtures.Id(200))) with { Continues = new(First, "session-1") },
            Said(1, new AgentEvent.Succeeded("Continued.")), Exit(2, 0)])!;
        var cancelled = AttemptReducer.Replay([BuildRequested(First), new AttemptEvent.CancelRequested(T0), Exit(1, 137)])!;
        var reviewing = AttemptReducer.Replay([BuildRequested(First) with { Subject = RunFixtures.T },
            Said(1, new AgentEvent.SessionStarted("session-1")), Said(2, new AgentEvent.Succeeded("Findings.")), Exit(3, 0)])!;
        Assert.Equal((AttemptStatus.WaitingForInput, "Question?"), (waiting.Status, waiting.Result));
        Assert.Equal(((AttemptId?)First, AttemptStatus.Succeeded, "Continued."), (continued.Continues, continued.Status, continued.Result));
        Assert.Equal(AttemptStatus.Cancelled, cancelled.Status);
        Assert.Equal((AttemptStatus.InReview, "Findings."), (reviewing.Status, reviewing.Result));
    }
}
