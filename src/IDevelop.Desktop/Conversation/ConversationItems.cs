using System.Collections.Immutable;
using System.Windows.Input;
using IDevelop.Desktop.Mvvm;
using IDevelop.Execution;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Conversation;

/// <summary>One transcript row. A row keeps its entry id, so a refresh updates it in place and its control stays.</summary>
public abstract class ConversationItemViewModel(ConversationEntry entry) : ObservableObject
{
    public EntryId Id { get; } = entry.Id;

    public TurnKey Turn { get; } = entry.Turn;

    public DateTimeOffset At { get; } = entry.At;

    internal static ConversationItemViewModel Create(ConversationEntry entry, ConversationViewModel owner) => entry.Content switch
    {
        ConversationContent.Message => new MessageItemViewModel(entry, owner),
        ConversationContent.Activity => new ActivityItemViewModel(entry),
        ConversationContent.Marker => new MarkerItemViewModel(entry),
        ConversationContent.Request request => new RequestItemViewModel(entry, request.Key, owner),
        _ => throw new InvalidOperationException("Unknown conversation content."),
    };

    /// <summary>Shows the entry's new content. False when the content changed kind and the row needs a new item.</summary>
    internal abstract bool Update(ConversationEntry entry);
}

public sealed class MessageItemViewModel : ConversationItemViewModel
{
    private readonly ConversationViewModel _owner;
    private ConversationContent.Message _message;

    internal MessageItemViewModel(ConversationEntry entry, ConversationViewModel owner) : base(entry)
    {
        _owner = owner;
        _message = (ConversationContent.Message)entry.Content;
        CopyCommand = new RelayCommand(() => _ = _owner.CopyAsync(_message.Text));
    }

    public MessageAuthor Author => _message.Author;

    public bool IsPerson => _message.Author == MessageAuthor.Person;

    public bool IsAgent => _message.Author == MessageAuthor.Agent;

    public bool IsApplication => _message.Author == MessageAuthor.Application;

    public string AuthorLabel => _message.Author switch
    {
        MessageAuthor.Person => "You",
        MessageAuthor.Agent => _owner.AgentName,
        MessageAuthor.Application => "iDevelop",
    };

    /// <summary>The source the message shows. An agent's result block leaves the prose, and <see cref="ResultNote"/> says what it held.</summary>
    public string Text => IsAgent && _message.State is MessageState.Complete ? ResultBlock.Prose(_message.Text) : _message.Text;

    /// <summary>What an agent's readable result block said, such as its question, or why iDevelop could not read it.</summary>
    public string? ResultNote => !IsAgent || _message.State is not MessageState.Complete ? null : ResultBlock.Read(_message.Text) switch
    {
        ResultBlock.Readable { Status: "asking" } asking => asking.Text("question") is { } question ? $"Asks: {question}" : "Asks a question.",
        ResultBlock.Readable readable => readable.Status is { } status ? $"Reported {status}. The inspector shows the result." : "Reported a result. The inspector shows it.",
        ResultBlock.Unreadable unreadable => $"iDevelop could not read the result block. {unreadable.Problem}",
        _ => null,
    };

    public MessageState State => _message.State;

    public bool IsStreaming => _message.State == MessageState.Streaming;

    /// <summary>Where a message stands when that is not plainly sent or complete.</summary>
    public string? StateLabel => _message.State switch
    {
        MessageState.Queued => "Queued for the next turn",
        MessageState.NotDelivered => "Not delivered",
        MessageState.Streaming => "Writing…",
        MessageState.Partial => "Stopped before it finished",
        MessageState.Submitted or MessageState.Complete => null,
    };

    public bool IsUndelivered => _message.State == MessageState.NotDelivered;

    /// <summary>Copies the whole message source, result block included.</summary>
    public ICommand CopyCommand { get; }

    internal override bool Update(ConversationEntry entry)
    {
        if (entry.Content is not ConversationContent.Message message)
        {
            return false;
        }

        if (message != _message)
        {
            _message = message;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(ResultNote));
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(IsUndelivered));
        }

        return true;
    }
}

public sealed class ActivityItemViewModel(ConversationEntry entry) : ConversationItemViewModel(entry)
{
    private ConversationContent.Activity _activity = (ConversationContent.Activity)entry.Content;

    public string Text => _activity.Text;

    /// <summary>The first line, which the row shows. The tooltip holds all of it.</summary>
    public string Line => _activity.Text.Split('\n', 2)[0];

    public bool IsTool => _activity.IsTool;

    internal override bool Update(ConversationEntry entry)
    {
        if (entry.Content is not ConversationContent.Activity activity)
        {
            return false;
        }

        if (activity != _activity)
        {
            _activity = activity;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(Line));
            OnPropertyChanged(nameof(IsTool));
        }

        return true;
    }
}

public sealed class MarkerItemViewModel(ConversationEntry entry) : ConversationItemViewModel(entry)
{
    private ConversationContent.Marker _marker = (ConversationContent.Marker)entry.Content;

    public string Kind => _marker.Kind;

    /// <summary>A short heading for the boundary or event, such as "New attempt" or "Failed".</summary>
    public string Title => _marker.Kind switch
    {
        "attempt" => "New attempt",
        "configuration" => "Settings changed",
        "failure" => "Failed",
        "interruption" => "Interrupted",
        "stopped" => "Stopped",
        "terminal" => "Opened in terminal",
        "deferred" => "Question moved to your next message",
        _ => _marker.Kind,
    };

    /// <summary>The marker's text, without an attempt marker's id line, which the attempt picker already names.</summary>
    public string Detail => _marker.Kind == "attempt" && _marker.Text.Split('\n', 2) is [_, var rest] ? rest : _marker.Text;

    public bool IsProblem => _marker.Kind is "failure" or "interruption";

    internal override bool Update(ConversationEntry entry)
    {
        if (entry.Content is not ConversationContent.Marker marker)
        {
            return false;
        }

        if (marker != _marker)
        {
            _marker = marker;
            OnPropertyChanged(nameof(Detail));
        }

        return true;
    }
}

/// <summary>
/// A question or a permission request at its place in the transcript. A question takes an answer while it is open in the
/// current turn. A permission is never allowed from here: iDevelop declined it on its own.
/// </summary>
public sealed class RequestItemViewModel : ConversationItemViewModel
{
    private readonly ConversationViewModel _owner;
    private readonly RelayCommand _submit;
    private RequestRecord? _record;
    private bool _answerable;
    private bool _submitting;
    private string? _outcome;

    internal RequestItemViewModel(ConversationEntry entry, RequestKey key, ConversationViewModel owner) : base(entry)
    {
        Key = key;
        _owner = owner;
        _submit = new RelayCommand(() => _ = SubmitAsync(), () => CanAnswer && !_submitting && Questions.All(question => question.HasAnswer));
    }

    public RequestKey Key { get; }

    public RequestRecord? Record => _record;

    public bool IsQuestion => _record is RequestRecord.Question;

    public bool IsPermission => _record is RequestRecord.Permission;

    public ImmutableArray<QuestionViewModel> Questions { get; private set; } = [];

    /// <summary>The question is open in the turn the conversation shows, so its controls take an answer.</summary>
    public bool CanAnswer => _answerable && _record is RequestRecord.Question { State: QuestionState.Open };

    public ICommand SubmitCommand => _submit;

    public string Heading => _record switch
    {
        RequestRecord.Question => $"{_owner.AgentName} asks",
        RequestRecord.Permission permission => $"{_owner.AgentName} asked to use {permission.Action.Tool}",
        _ => "Request",
    };

    /// <summary>The exact action a permission request named, as its client sent it.</summary>
    public string? Action => _record is RequestRecord.Permission permission ? permission.Action.InputJson : null;

    public string? Scope => _record is RequestRecord.Permission { Action.Scope: { Length: > 0 } scope } ? $"Scope: {scope}" : null;

    /// <summary>Where the request stands: open until when, the recorded answer, or why it closed.</summary>
    public string Status => _outcome ?? _record switch
    {
        RequestRecord.Question { State: QuestionState.Open open } => _answerable
            ? $"Answer by {open.Deadline.AnswerBy.ToLocalTime():t}. After that, the question moves to your next message."
            : "This question belongs to an earlier turn.",
        RequestRecord.Question { State: QuestionState.AnswerRecorded recorded } => $"You answered: {Describe(recorded.Reply)}",
        RequestRecord.Question { State: QuestionState.Closed closed } => Closed(closed),
        RequestRecord.Permission { State: PermissionState.Declining } => "iDevelop is declining this request.",
        RequestRecord.Permission { State: PermissionState.Denied } => "iDevelop denied it automatically. Nothing ran.",
        RequestRecord.Permission { State: PermissionState.DeliveryUnknown } =>
            "iDevelop denied it automatically, but could not confirm that the client received the denial.",
        _ => "Loading…",
    };

    internal QuestionDraft Draft(string question) => _owner.State.AnswerDraft(Key, question);

    internal void SetDraft(string question, QuestionDraft draft)
    {
        _owner.State.SetAnswerDraft(Key, question, draft);
        _submit.NotifyCanExecuteChanged();
    }

    /// <summary>Shows the request's latest fold. <paramref name="current"/> says whether its turn is the conversation's current one.</summary>
    internal void Show(RequestRecord? record, bool current)
    {
        var changed = !Equals(record, _record);
        _record = record;
        _answerable = current;
        if (changed)
        {
            _outcome = null;
            Questions = record is RequestRecord.Question question ? [.. question.Questions.Select(asked => new QuestionViewModel(asked, this))] : [];
            OnPropertyChanged(nameof(Questions));
            OnPropertyChanged(nameof(Record));
            OnPropertyChanged(nameof(IsQuestion));
            OnPropertyChanged(nameof(IsPermission));
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(Action));
            OnPropertyChanged(nameof(Scope));
        }

        foreach (var asked in Questions)
        {
            asked.OnAnswerableChanged();
        }

        OnPropertyChanged(nameof(CanAnswer));
        OnPropertyChanged(nameof(Status));
        _submit.NotifyCanExecuteChanged();
    }

    internal override bool Update(ConversationEntry entry) => entry.Content is ConversationContent.Request request && request.Key == Key;

    private async Task SubmitAsync()
    {
        _submitting = true;
        _submit.NotifyCanExecuteChanged();
        var reply = new QuestionsReply([.. Questions.Select(question => question.Answer())]);
        var result = await _owner.AnswerAsync(Key, reply);
        _submitting = false;
        _outcome = result.Outcome switch
        {
            AnswerOutcome.Recorded or AnswerOutcome.AlreadyRecorded => $"You answered: {Describe(reply)}",
            AnswerOutcome.Stale => "This question closed before your answer arrived. Your answer stays here and moves to your next message.",
            AnswerOutcome.DeliveryUnknown => "iDevelop recorded your answer but could not confirm that the agent received it.",
            AnswerOutcome.Invalid or AnswerOutcome.Unavailable => result.Detail,
        };
        OnPropertyChanged(nameof(Status));
        _submit.NotifyCanExecuteChanged();
    }

    private string Describe(QuestionsReply reply)
    {
        var asked = _record is RequestRecord.Question question ? question.Questions : [];
        return string.Join("; ", reply.Answers.Select(answer =>
        {
            var options = asked.FirstOrDefault(item => item.Id == answer.QuestionId)?.Options ?? [];
            var labels = options.Where(option => answer.OptionIds.Contains(option.Id)).Select(option => option.Label);
            return string.Join(", ", [.. labels, .. string.IsNullOrWhiteSpace(answer.Text) ? Array.Empty<string>() : [answer.Text]]);
        }));
    }

    private string Closed(QuestionState.Closed closed) => closed.Reason switch
    {
        RequestCloseReason.Deferred => "No live answer was possible, so the question moved to your next message. Reply below to continue.",
        RequestCloseReason.Resolved when closed.RecordedReply is { } reply => $"You answered: {Describe(reply)}",
        RequestCloseReason.Resolved => "The agent closed this question.",
        RequestCloseReason.TurnEnded => "The turn ended before an answer.",
        RequestCloseReason.Stopped => "The turn stopped before an answer.",
        RequestCloseReason.Cancelled => "The attempt was cancelled.",
        RequestCloseReason.Unsupported => "iDevelop cannot answer this kind of question.",
        RequestCloseReason.PolicyDenied => "This task does not take live questions, so iDevelop declined it.",
        RequestCloseReason.DeliveryUnknown => "iDevelop could not confirm that the answer reached the agent.",
        RequestCloseReason.Interrupted => "iDevelop stopped before the question was answered.",
    };
}

public sealed class QuestionViewModel
{
    private readonly AskedQuestion _question;
    private readonly RequestItemViewModel _request;

    internal QuestionViewModel(AskedQuestion question, RequestItemViewModel request)
    {
        _question = question;
        _request = request;
        Options = [.. question.Options.Select(option => new OptionViewModel(option, this))];
        Other = new OtherAnswerViewModel(this);
    }

    public string Header => _question.Header;

    public string Text => _question.Text;

    public bool MultiSelect => _question.MultiSelect;

    public bool AllowsOther => _question.AllowsOther;

    public ImmutableArray<OptionViewModel> Options { get; }

    public OtherAnswerViewModel Other { get; }

    /// <summary>Radio buttons of one question share a group, so two questions never clear each other's choice.</summary>
    public string Group => $"{_request.Key.Turn.Attempt}/{_request.Key.Turn.Number}/{_request.Key.Id}/{_question.Id}";

    public bool CanAnswer => _request.CanAnswer;

    internal QuestionDraft Draft => _request.Draft(_question.Id);

    internal bool HasAnswer => !Draft.IsEmpty;

    internal void Choose(string option, bool chosen)
    {
        var draft = Draft;
        var options = MultiSelect ? draft.Options : [];
        _request.SetDraft(_question.Id, draft with { Options = chosen ? options.Add(option) : options.Remove(option) });
        foreach (var item in Options)
        {
            item.OnChanged();
        }
    }

    internal void Write(string text) => _request.SetDraft(_question.Id, Draft with { Text = text });

    internal QuestionAnswer Answer()
    {
        var draft = Draft;
        return new QuestionAnswer(_question.Id, [.. _question.Options.Where(option => draft.Options.Contains(option.Id)).Select(option => option.Id)],
            string.IsNullOrWhiteSpace(draft.Text) ? null : draft.Text.Trim());
    }

    internal void OnAnswerableChanged()
    {
        foreach (var option in Options)
        {
            option.OnChanged();
        }

        Other.OnChanged();
    }
}

public sealed class OptionViewModel(QuestionOption option, QuestionViewModel question) : ObservableObject
{
    public string Label => option.Label;

    public string? Detail => option.Detail;

    public string Group => question.Group;

    public bool MultiSelect => question.MultiSelect;

    public bool CanAnswer => question.CanAnswer;

    public bool IsChosen
    {
        get => question.Draft.Options.Contains(option.Id);
        set
        {
            if (value != IsChosen)
            {
                question.Choose(option.Id, value);
            }
        }
    }

    internal void OnChanged()
    {
        OnPropertyChanged(nameof(IsChosen));
        OnPropertyChanged(nameof(CanAnswer));
    }
}

public sealed class OtherAnswerViewModel(QuestionViewModel question) : ObservableObject
{
    /// <summary>A closed question keeps a written answer in view and drops an empty box.</summary>
    public bool IsShown => (question.AllowsOther || question.Options.IsEmpty) && (question.CanAnswer || !string.IsNullOrWhiteSpace(Text));

    public bool CanAnswer => question.CanAnswer;

    public string Text
    {
        get => question.Draft.Text;
        set
        {
            if (value != Text)
            {
                question.Write(value ?? "");
                OnPropertyChanged();
            }
        }
    }

    internal void OnChanged()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(CanAnswer));
        OnPropertyChanged(nameof(IsShown));
    }
}
