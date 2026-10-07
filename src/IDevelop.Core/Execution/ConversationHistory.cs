using System.Collections.Immutable;
using System.Text;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record PositionedAttemptEvent(long Position, AttemptEvent Event);
internal sealed record LiveMessageBuffer(string Text, long Order, DateTimeOffset? At = null);
internal sealed record ProjectedConversationEntry(ConversationEntry Entry, long Position, int Slot);
internal sealed record AttemptHistory(AttemptId Id, ImmutableArray<ProjectedConversationEntry> Rows)
{
    public ImmutableArray<ConversationEntry> Entries => [.. Rows.Select(row => row.Entry)];
}

internal static class ConversationHistory
{
    public static AttemptHistory? Project(IEnumerable<AttemptEvent> events,
        IReadOnlyDictionary<string, LiveMessageBuffer>? live = null) =>
        Project(events.Select((e, position) => new PositionedAttemptEvent(position, e)), live);

    public static AttemptHistory? Project(IEnumerable<PositionedAttemptEvent> events,
        IReadOnlyDictionary<string, LiveMessageBuffer>? live = null)
    {
        AttemptRecord? record = null;
        var rows = new Dictionary<EntryId, ProjectedConversationEntry>();
        var queue = new List<(string Id, EntryId Entry)>();
        var lastComplete = new Dictionary<int, string>();
        var failures = new Dictionary<int, string>();
        foreach (var line in events)
        {
            var e = line.Event;
            var before = record;
            record = record is null
                ? e is AttemptEvent.Requested start ? AttemptReducer.Start(start) : null
                : AttemptReducer.Apply(record, e);
            if (record is null)
            {
                continue;
            }

            var turn = new TurnKey(record.Id, record.Turns.Count);
            var order = e is AttemptEvent.Agent { Order: { } presented } ? 2 * presented : 2 * line.Position + 1;
            var slot = 0;
            EntryId Add(string kind, ConversationContent content, string? identity = null)
            {
                var id = Id(record.Id, turn.Number, kind, identity, line.Position);
                if (!rows.ContainsKey(id))
                {
                    rows.Add(id, new ProjectedConversationEntry(new ConversationEntry(id, turn, order, e.At, content), line.Position, slot++));
                }

                return id;
            }

            void Message(MessageAuthor author, string text, MessageState state, string kind, string? identity = null)
            {
                var content = new ConversationContent.Message(author, text, state);
                var id = Add(kind, content, identity);
                if (kind == "message")
                {
                    rows[id] = rows[id] with { Entry = rows[id].Entry with { Content = content } };
                }
            }
            void Marker(string kind, string text) => Add(kind, new ConversationContent.Marker(kind, text));
            void Undelivered()
            {
                foreach (var queued in queue)
                {
                    SetMessageState(rows, queued.Entry, MessageState.NotDelivered);
                }
            }

            switch (e)
            {
                case AttemptEvent.Requested requested when before is null:
                    Marker("attempt", $"Attempt {record.Id}\n{Clients.Name(record.Requested.Client)}, model {record.Requested.Model ?? "unspecified"}, reasoning {record.Requested.Reasoning ?? "unspecified"}, conversation {record.Conversation}.");
                    Message(requested.Continues is null || requested.Fix is not null || requested.Subject is not null
                        ? MessageAuthor.Application : MessageAuthor.Person, requested.Prompt, MessageState.Submitted, "prompt");
                    break;
                case AttemptEvent.MessageQueued queued:
                    var queueId = queued.Id ?? $"legacy:{record.Id}:{before?.AppliedEvents ?? line.Position}";
                    var entry = Add("queued", new ConversationContent.Message(MessageAuthor.Person, queued.Text,
                        record.Status is AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Interrupted
                            ? MessageState.NotDelivered : MessageState.Queued), queued.Id);
                    queue.Add((queueId, entry));
                    break;
                case AttemptEvent.TurnRequested requested:
                    var consumed = requested.Consumed.IsDefault
                        ? queue.Count > 0 && string.Join("\n\n", queue.Select(item => ((ConversationContent.Message)rows[item.Entry].Entry.Content).Text)) == requested.Prompt
                            ? queue.ToArray() : []
                        : queue.Where(item => requested.Consumed.Contains(item.Id)).ToArray();
                    foreach (var queued in consumed)
                    {
                        SetMessageState(rows, queued.Entry, MessageState.Submitted);
                        queue.Remove(queued);
                    }

                    if (requested.Consumed.IsDefault && consumed.Length == 0)
                    {
                        Undelivered();
                        queue.Clear();
                    }

                    if (record.Subject is not null || requested.Report is not null)
                    {
                        Message(MessageAuthor.Application, requested.Prompt, MessageState.Submitted, "prompt");
                    }
                    else if (consumed.Length == 0)
                    {
                        Message(MessageAuthor.Person, requested.Prompt, MessageState.Submitted, "prompt");
                    }

                    if (requested.Conversation is { } mode && mode != before?.Conversation)
                    {
                        Marker("configuration", $"Conversation mode changed to {mode}.");
                    }

                    break;
                case AttemptEvent.Agent { Event: AgentEvent.Message message }:
                    Message(MessageAuthor.Agent, message.Text, message.Partial ? MessageState.Partial : MessageState.Complete, "message", message.Id);
                    if (!message.Partial)
                    {
                        lastComplete[turn.Number] = message.Text;
                    }

                    break;
                case AttemptEvent.Agent { Event: AgentEvent.Succeeded { Result: { } result } }:
                    if (!lastComplete.TryGetValue(turn.Number, out var final) || final != result)
                    {
                        Message(MessageAuthor.Agent, result, MessageState.Complete, "result");
                    }

                    break;
                case AttemptEvent.Agent { Event: AgentEvent.PermissionRequested permission }:
                    Add("request", new ConversationContent.Request(new RequestKey(turn, permission.RequestId)), permission.RequestId);
                    break;
                case AttemptEvent.QuestionRecorded question:
                    Add("request", new ConversationContent.Request(new RequestKey(turn, question.RequestId)), question.RequestId);
                    break;
                case AttemptEvent.Agent { Event: AgentEvent.ToolStarted tool }:
                    Add("activity", new ConversationContent.Activity(tool.Detail is null ? tool.Tool : $"{tool.Tool}: {tool.Detail}", true));
                    break;
                case AttemptEvent.Agent { Event: AgentEvent.Notice notice }:
                    Add("activity", new ConversationContent.Activity(notice.Text, false));
                    break;
                case AttemptEvent.Agent { Event: AgentEvent.Reported reported }:
                    if ((reported.Model is not null && reported.Model != before?.ReportedModel)
                        || (reported.Reasoning is not null && reported.Reasoning != before?.ReportedReasoning))
                    {
                        Marker("configuration", $"Client reported model {record.ReportedModel ?? "unspecified"}, reasoning {record.ReportedReasoning ?? "unspecified"}.");
                    }

                    break;
                case AttemptEvent.Agent { Event: AgentEvent.Failed failed }:
                    Marker("failure", failed.Reason);
                    failures[turn.Number] = failed.Reason;
                    break;
                case AttemptEvent.GuidanceAdded guidance:
                    Message(MessageAuthor.Person, guidance.Text, MessageState.Submitted, "guidance");
                    break;
                case AttemptEvent.HandedToTerminal handoff:
                    Marker("terminal", $"Opened in terminal in {handoff.Folder}\n{handoff.Command}");
                    break;
                case AttemptEvent.RequestDeferred deferred:
                    Marker("deferred", deferred.Question);
                    Undelivered();
                    break;
                case AttemptEvent.CancelRequested:
                case AttemptEvent.InterruptRequested:
                    Undelivered();
                    break;
                case AttemptEvent.Exited:
                case AttemptEvent.Reconciled:
                case AttemptEvent.LaunchFailed:
                case AttemptEvent.Concluded:
                case AttemptEvent.MarkedDone:
                    var outcome = record.Turns[^1].Outcome;
                    if (outcome is TurnOutcome.Failed or TurnOutcome.Stopped or TurnOutcome.Interrupted or TurnOutcome.Deferred
                        || record.Status is AttemptStatus.Failed or AttemptStatus.Cancelled or AttemptStatus.Interrupted)
                    {
                        Undelivered();
                    }

                    var detail = record.Turns[^1].Detail ?? record.Detail;
                    if (outcome == TurnOutcome.Interrupted || record.Status == AttemptStatus.Interrupted)
                    {
                        Marker("interruption", detail ?? "The turn was interrupted.");
                    }
                    else if ((outcome == TurnOutcome.Failed || record.Status == AttemptStatus.Failed)
                        && (!failures.TryGetValue(turn.Number, out var failure) || failure != detail))
                    {
                        Marker("failure", detail ?? "The turn failed.");
                    }
                    else if (outcome == TurnOutcome.Stopped || record.Status == AttemptStatus.Cancelled)
                    {
                        Marker("stopped", detail ?? "The turn was stopped.");
                    }

                    break;
                case AttemptEvent.Requested:
                case AttemptEvent.Launched:
                case AttemptEvent.RequestAnswered:
                case AttemptEvent.RequestClosed:
                case AttemptEvent.ShutdownForced:
                case AttemptEvent.Agent { Event: AgentEvent.SessionStarted or AgentEvent.MessageDelta or AgentEvent.QuestionAsked or AgentEvent.RequestClosed or AgentEvent.Succeeded }:
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled history event {e.GetType().Name}.");
            }
        }

        if (record is null)
        {
            return null;
        }

        if (live is not null && record.Status == AttemptStatus.Running && !record.BetweenTurns)
        {
            foreach (var (messageId, buffer) in live)
            {
                var turn = new TurnKey(record.Id, record.Turns.Count);
                var id = Id(record.Id, turn.Number, "message", messageId, buffer.Order);
                rows.TryAdd(id, new ProjectedConversationEntry(new ConversationEntry(id, turn, 2 * buffer.Order,
                    buffer.At ?? record.RequestedAt, new ConversationContent.Message(MessageAuthor.Agent, buffer.Text, MessageState.Streaming)), buffer.Order, 0));
            }
        }

        return new AttemptHistory(record.Id, [.. rows.Values.OrderBy(row => row.Entry.Order).ThenBy(row => row.Position)
            .ThenBy(row => row.Slot).ThenBy(row => row.Entry.Id.Value, StringComparer.Ordinal)]);
    }

    private static EntryId Id(AttemptId attempt, int turn, string kind, string? identity, long position) =>
        new($"c1/{attempt}/{turn}/{kind}/{(identity is null ? $"p{position}" : "i" + Encode(identity))}");

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void SetMessageState(Dictionary<EntryId, ProjectedConversationEntry> rows, EntryId id, MessageState state)
    {
        var row = rows[id];
        rows[id] = row with { Entry = row.Entry with { Content = ((ConversationContent.Message)row.Entry.Content) with { State = state } } };
    }
}
