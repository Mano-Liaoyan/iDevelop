using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Workflows;
using AsyncTask = System.Threading.Tasks.Task;

namespace IDevelop.TestSupport;

internal sealed class FakeConversationSession(TaskId task) : IConversationSession
{
    private bool _disposed;

    public TaskId Task { get; } = task;

    public ConversationSnapshot Snapshot { get; set; } = new(0, 0, null, null,
        new ClientCapabilities(false, false, "No client selected."),
        new ConversationActions(Disabled, Disabled, Disabled, Disabled, Disabled));

    private static ActionAvailability Disabled => new(false, "Unavailable.");

    public event Action<long>? Changed;

    public List<Call> Calls { get; } = [];

    public ImmutableArray<AttemptSummary> Attempts { get; set; } = [];

    public Dictionary<(AttemptId Head, HistoryQuery Query, int Count), HistoryResult> Pages { get; } = [];

    public Dictionary<RequestKey, RequestRecord> Requests { get; } = [];

    public Func<Call.Page, HistoryResult>? ReadPage { get; set; }

    public Func<Call.Answer, AnswerResult> Answer { get; set; } = _ => new(AnswerOutcome.Recorded, "Recorded.");

    public Func<Call.Send, SendResult> Send { get; set; } = _ => new SendResult.Queued();

    public Func<Call.Cancel, ConversationCommandResult> Cancel { get; set; } = _ => new(CommandOutcome.Applied, "Cancelled.");

    public Func<Call.MarkDone, ConversationCommandResult> MarkDone { get; set; } = _ => new(CommandOutcome.Applied, "Done.");

    public Func<Call.Terminal, TerminalResult> Terminal { get; set; } = _ => new TerminalResult.Refused(new TerminalProblem.NeverRan());

    public void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke(Snapshot.Revision);
        }
    }

    public Task<ImmutableArray<AttemptSummary>> ListAttemptsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(new Call.ListAttempts(ct));
        return AsyncTask.FromResult(_disposed ? ImmutableArray<AttemptSummary>.Empty : Attempts);
    }

    public Task<HistoryResult> ReadPageAsync(AttemptId head, HistoryQuery query, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = new Call.Page(head, query, count, ct);
        Calls.Add(call);
        return AsyncTask.FromResult(_disposed ? new HistoryResult.Unavailable("Closed.")
            : ReadPage?.Invoke(call) ?? Pages.GetValueOrDefault((head, query, count)) ?? new HistoryResult.Unavailable("No scripted page."));
    }

    public Task<RequestRecord?> ReadRequestAsync(RequestKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(new Call.Request(key, ct));
        return AsyncTask.FromResult(_disposed ? null : Requests.GetValueOrDefault(key));
    }

    public Task<SendResult> SendAsync(TurnKey expected, string text, bool stopTurn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = new Call.Send(expected, text, stopTurn, ct);
        Calls.Add(call);
        return AsyncTask.FromResult(_disposed ? new SendResult.Refused(new SendProblem.ClosedOwner()) : Send(call));
    }

    public Task<AnswerResult> AnswerAsync(RequestKey key, QuestionsReply reply, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = new Call.Answer(key, reply, ct);
        Calls.Add(call);
        return AsyncTask.FromResult(_disposed ? new AnswerResult(AnswerOutcome.Unavailable, "Closed.") : Answer(call));
    }

    public Task<ConversationCommandResult> CancelAsync(TurnKey expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = new Call.Cancel(expected, ct);
        Calls.Add(call);
        return AsyncTask.FromResult(_disposed ? new ConversationCommandResult(CommandOutcome.Unavailable, "Closed.") : Cancel(call));
    }

    public Task<ConversationCommandResult> MarkDoneAsync(TurnKey expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = new Call.MarkDone(expected, ct);
        Calls.Add(call);
        return AsyncTask.FromResult(_disposed ? new ConversationCommandResult(CommandOutcome.Unavailable, "Closed.") : MarkDone(call));
    }

    public Task<TerminalResult> OpenInTerminalAsync(TurnKey expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = new Call.Terminal(expected, ct);
        Calls.Add(call);
        return AsyncTask.FromResult(_disposed ? new TerminalResult.Refused(new TerminalProblem.ClosedOwner()) : Terminal(call));
    }

    public void Dispose() => _disposed = true;

    public abstract record Call
    {
        private Call() { }

        public sealed record ListAttempts(CancellationToken Ct) : Call;

        public sealed record Page(AttemptId Head, HistoryQuery Query, int Count, CancellationToken Ct) : Call;

        public sealed record Request(RequestKey Key, CancellationToken Ct) : Call;

        public sealed record Send(TurnKey Expected, string Text, bool StopTurn, CancellationToken Ct) : Call;

        public sealed record Answer(RequestKey Key, QuestionsReply Reply, CancellationToken Ct) : Call;

        public sealed record Cancel(TurnKey Expected, CancellationToken Ct) : Call;

        public sealed record MarkDone(TurnKey Expected, CancellationToken Ct) : Call;

        public sealed record Terminal(TurnKey Expected, CancellationToken Ct) : Call;
    }
}
