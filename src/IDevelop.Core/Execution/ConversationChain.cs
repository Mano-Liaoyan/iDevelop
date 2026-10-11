using System.Collections.Immutable;

namespace IDevelop.Execution;

/// <summary>
/// Reads a chain of attempts, oldest first, each continuing the one before, as one conversation (#92). An attempt whose
/// first turn resumed the earlier attempt's client session goes on with that conversation: its New attempt marker goes,
/// and a Settings changed marker takes its place when it runs another model, level, or conversation mode. An attempt
/// whose client reported another session, or ended its first turn without reporting one, started over, so it keeps its
/// marker. While its first turn starts, it reads as going on. The attempts stay apart in their logs and the attempt picker.
/// </summary>
internal static class ConversationChain
{
    public static ImmutableArray<AttemptHistory> Join(IReadOnlyList<AttemptHistory> chain) =>
        [.. chain.Select((history, index) => index > 0 && GoesOn(chain[index - 1], history) ? Continued(chain[index - 1], history) : history)];

    private static bool GoesOn(AttemptHistory earlier, AttemptHistory later) =>
        later.Record.Continues == earlier.Id && later.Record.Resumed != false;

    private static AttemptHistory Continued(AttemptHistory earlier, AttemptHistory later)
    {
        var changed = later.Record.Requested != earlier.Record.Requested || later.Opening != earlier.Record.Conversation;
        return later with
        {
            Rows = [.. later.Rows.SelectMany(row => row.Entry.Content is ConversationContent.Marker { Kind: "attempt" }
                ? changed ? [Settings(later, row)] : []
                : new[] { row })],
        };
    }

    private static ProjectedConversationEntry Settings(AttemptHistory attempt, ProjectedConversationEntry marker) => marker with
    {
        Entry = marker.Entry with
        {
            Id = ConversationHistory.Id(attempt.Id, marker.Entry.Turn.Number, "configuration", "attempt", marker.Position),
            Content = new ConversationContent.Marker("configuration", ConversationHistory.Settings(attempt.Record.Requested, attempt.Opening)),
        },
    };
}
