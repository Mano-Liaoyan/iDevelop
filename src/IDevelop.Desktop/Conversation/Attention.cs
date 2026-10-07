using IDevelop.Desktop.Execution;
using IDevelop.Execution;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// What a task needs from the person, from its latest attempt alone. Opening the conversation does not clear it; only the
/// attempt moving on does. A permission that iDevelop denied on its own needs nothing from the person.
/// </summary>
public abstract record Attention(string Label)
{
    /// <summary>The oldest question that still takes an answer.</summary>
    public sealed record Question(RequestKey Request, string Label) : Attention(Label);

    /// <summary>The attempt waits for the person's reply, with no process and no lock.</summary>
    public sealed record Waiting(string Label) : Attention(Label);

    /// <summary>The attempt failed or was interrupted.</summary>
    public sealed record Problem(string Label) : Attention(Label);

    /// <summary>An open question first, then a durable wait, then a failure or an interruption.</summary>
    public static Attention? Of(AttemptRecord? latest)
    {
        if (latest is null)
        {
            return null;
        }

        var open = latest.Requests.Values.OfType<RequestRecord.Question>()
            .Where(question => question.State is QuestionState.Open)
            .OrderBy(question => question.At)
            .ThenBy(question => question.Key.Turn.Number)
            .ThenBy(question => question.Key.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (open is not null)
        {
            return new Question(open.Key, $"Question: {open.Questions.FirstOrDefault()?.Text ?? "The agent asks a question."}");
        }

        return latest.Status switch
        {
            AttemptStatus.WaitingForInput => new Waiting(latest.Pending is { } pending ? $"Waiting for you: {RunText.Waiting(pending)}" : "Waiting for you"),
            AttemptStatus.Failed => new Problem($"Failed: {latest.Detail ?? "The run failed."}"),
            AttemptStatus.Interrupted => new Problem($"Interrupted: {latest.Detail ?? "The run was interrupted."}"),
            AttemptStatus.Running or AttemptStatus.Succeeded or AttemptStatus.Cancelled or AttemptStatus.InReview => null,
        };
    }
}
