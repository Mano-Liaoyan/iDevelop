using IDevelop.Desktop.Conversation;
using IDevelop.Execution;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

public sealed class AttentionTests
{
    private static AttemptEvent Asked(int seconds, string id, string text, int answerBy) => new AttemptEvent.QuestionRecorded(T0.AddSeconds(seconds), id,
        [new AskedQuestion("q", "", text, [], false, true)], new QuestionState.Open(new RequestDeadline(T0.AddSeconds(answerBy), T0.AddSeconds(answerBy + 5))));

    [Fact]
    public void The_oldest_open_question_comes_before_the_wait_and_a_failure_and_a_finished_attempt_needs_nothing()
    {
        var asking = Record(A, Launched(1), Asked(2, "s:b", "Which fixture?", 57), Asked(3, "s:a", "Which branch?", 57));
        var failed = Record(A, Launched(1), At(2, new AgentEvent.Failed("The model is not available.")), Exited(3, 1));
        var succeeded = Record(A, Launched(1), At(2, new AgentEvent.Succeeded("Done.")), Exited(3));

        Assert.Equal(new Attention.Question(new RequestKey(new TurnKey(A, 1), "s:b"), "Question: Which fixture?"), Attention.Of(asking));
        Assert.Equal(new Attention.Problem("Failed: The model is not available."), Attention.Of(failed));
        Assert.Null(Attention.Of(succeeded));
        Assert.Null(Attention.Of(null));
    }
}
