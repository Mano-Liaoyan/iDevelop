using System.Collections.Immutable;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Conversation;

/// <summary>Names one task's conversation in an open project. A task id never moves between workflows.</summary>
internal readonly record struct ConversationKey(WorkflowRef Workflow, TaskId Task);

/// <summary>What the person chose and wrote for one question before sending the answer.</summary>
public sealed record QuestionDraft(ImmutableHashSet<string> Options, string Text)
{
    public static QuestionDraft Empty { get; } = new([], "");

    public bool IsEmpty => Options.IsEmpty && string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// One task's unsent work in the conversation view: the composer text, the answers being written, which attempt the
/// person reads, and where. The project keeps it while it is open, across task, workflow, project, and layout switches,
/// and nothing keeps it across a restart.
/// </summary>
public sealed class ConversationState : ObservableObject
{
    private string _draft = "";

    public string Draft
    {
        get => _draft;
        set => SetProperty(ref _draft, value ?? "");
    }

    /// <summary>The attempt whose history the person reads, or null to follow the latest attempt.</summary>
    public AttemptId? SelectedAttempt { get; set; }

    /// <summary>The entry at the top of the transcript and how far into it the person scrolled, or null at the latest message.</summary>
    public (EntryId Entry, double Offset)? Anchor { get; set; }

    internal Dictionary<RequestKey, ImmutableDictionary<string, QuestionDraft>> Answers { get; } = [];

    /// <summary>The deferred questions whose unsent answers already moved to the composer.</summary>
    internal HashSet<RequestKey> Transferred { get; } = [];

    internal QuestionDraft AnswerDraft(RequestKey request, string question) =>
        Answers.GetValueOrDefault(request)?.GetValueOrDefault(question) ?? QuestionDraft.Empty;

    internal void SetAnswerDraft(RequestKey request, string question, QuestionDraft draft) =>
        Answers[request] = (Answers.GetValueOrDefault(request) ?? ImmutableDictionary<string, QuestionDraft>.Empty).SetItem(question, draft);

    /// <summary>
    /// Moves the unsent answer of a deferred question into the composer, once. Each answered question becomes its text,
    /// then its chosen labels in option order, then what the person wrote. Blank lines separate questions and earlier
    /// composer text. Nothing is sent.
    /// </summary>
    internal void TransferDeferred(RequestKey request, IEnumerable<AskedQuestion> questions)
    {
        if (!Transferred.Add(request))
        {
            return;
        }

        var blocks = questions.Select(question => (question, draft: AnswerDraft(request, question.Id)))
            .Where(item => !item.draft.IsEmpty)
            .Select(item => string.Join("\n", [
                item.question.Text,
                .. item.question.Options.Where(option => item.draft.Options.Contains(option.Id)).Select(option => option.Label),
                .. string.IsNullOrWhiteSpace(item.draft.Text) ? Array.Empty<string>() : [item.draft.Text.Trim()],
            ]))
            .ToList();
        if (blocks.Count > 0)
        {
            Draft = string.Join("\n\n", [.. string.IsNullOrWhiteSpace(Draft) ? Array.Empty<string>() : [Draft.TrimEnd()], .. blocks]);
        }
    }
}
