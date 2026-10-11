using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>The inspector's Talk to the Agent list reads a continued session as one list, as the conversation view does (#92).</summary>
public sealed class AttemptExchangeTests
{
    private static readonly AttemptId D = new(Guid.Parse("019aa000-0000-7000-8000-00000000000d"));
    private static readonly AttemptId E = new(Guid.Parse("019aa000-0000-7000-8000-00000000000e"));

    private static AttemptRecord Continuing(AttemptId attempt, AttemptId continues, int seconds, params AttemptEvent[] events) =>
        AttemptReducer.Replay([Requested(attempt, TestTasks.Design, seconds, "Go on.", new Continuation(continues, "thread-1")), Launched(seconds + 1), .. events])!;

    [Fact]
    public void An_attempt_heads_its_exchange_only_when_it_started_a_new_session_or_failed()
    {
        var first = Record(A, Launched(1), At(2, new AgentEvent.SessionStarted("thread-1")), At(3, new AgentEvent.Succeeded("Which fruit?")), Exited(4));
        var resumed = Continuing(B, A, 100, At(102, new AgentEvent.SessionStarted("thread-1")), At(103, new AgentEvent.Succeeded("Noted.")), Exited(104));
        var failed = Continuing(C, B, 200, At(202, new AgentEvent.SessionStarted("thread-1")), At(203, new AgentEvent.Failed("The model is busy.")), Exited(204, 1));
        var restarted = Continuing(D, C, 300, At(302, new AgentEvent.SessionStarted("thread-9")), At(303, new AgentEvent.Succeeded("Which test?")), Exited(304));
        var starting = Continuing(E, D, 400);

        var view = new AttemptViewModel(starting, [first, resumed, failed, restarted], elsewhere: false, new ClientStatus.Checking());
        var single = new AttemptViewModel(first, [], elsewhere: false, new ClientStatus.Checking());

        Assert.Equal([true, false, true, true, false], view.Conversation.Select(exchange => exchange.StatusLine is not null));
        Assert.StartsWith("Failed · Started ", view.Conversation[2].StatusLine);
        Assert.Equal([5, 1], [view.Conversation.Count, single.Conversation.Count]);
        Assert.Null(single.Conversation[0].StatusLine);
    }
}
