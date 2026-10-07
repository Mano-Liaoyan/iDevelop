using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>Synthetic conversation history: attempt logs on disk for the real runner, and a list pager for the fake session.</summary>
internal static class ConversationFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    public static readonly AttemptId A = new(Guid.Parse("019aa000-0000-7000-8000-00000000000a"));
    public static readonly AttemptId B = new(Guid.Parse("019aa000-0000-7000-8000-00000000000b"));
    public static readonly AttemptId C = new(Guid.Parse("019aa000-0000-7000-8000-00000000000c"));
    public static readonly ExecutionSettings CodexHigh = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    public static AttemptEvent.Requested Requested(AttemptId attempt, TaskId task, int seconds, string prompt, Continuation? continues = null) =>
        new(T0.AddSeconds(seconds), attempt, task, "Design", CodexHigh, prompt, "codex", ["exec", "--json"]) { Continues = continues };

    public static AttemptEvent At(int seconds, AgentEvent e) => new AttemptEvent.Agent(T0.AddSeconds(seconds), e);

    public static AttemptEvent.Launched Launched(int seconds) => new(T0.AddSeconds(seconds), 4242, T0.AddSeconds(seconds));

    public static AttemptEvent.Exited Exited(int seconds, int code = 0) => new(T0.AddSeconds(seconds), code, "");

    /// <summary>Writes a settled attempt's log where the project's runner reads it.</summary>
    public static void WriteAttempt(string project, AttemptEvent.Requested requested, params AttemptEvent[] events)
    {
        using var log = AttemptLog.Create(Path.Combine(project, ".idp", "attempts"), requested);
        foreach (var e in events)
        {
            log.Append(e);
        }
    }

    public static AttemptRecord Record(AttemptId attempt, params AttemptEvent[] events) =>
        AttemptReducer.Replay([Requested(attempt, TestTasks.Design, 0, "Draft it."), .. events])!;

    public static ConversationEntry Entry(string id, ConversationContent content, AttemptId? attempt = null, int turn = 1) =>
        new(new EntryId(id), new TurnKey(attempt ?? A, turn), 0, T0, content);

    public static ConversationEntry Said(string id, MessageAuthor author, string text, MessageState state = MessageState.Complete) =>
        Entry(id, new ConversationContent.Message(author, text, state));

    public static ActionAvailability On(string reason = "Ready.") => new(true, reason);

    public static ActionAvailability Off(string reason) => new(false, reason);

    public static ConversationSnapshot Snapshot(AttemptRecord latest, ConversationActions actions, long revision = 1, long logRevision = 1,
        string limitations = "") =>
        new(revision, logRevision, new TurnKey(latest.Id, latest.Turns.Count), latest, new ClientCapabilities(true, false, limitations), actions);

    public static ConversationActions Actions(bool send = true, bool stopAndSend = false, bool cancel = false) =>
        new(send ? On() : Off("Run this task first."), stopAndSend ? On() : Off("Stop and send requires a running agent turn."),
            cancel ? On() : Off("There is no owned run to cancel."), Off("Not waiting."), Off("Not waiting."));

    /// <summary>The text a markdown block shows, with its runs and links in order.</summary>
    public static string Shown(TextBlock text) => text.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.Select(Shown)) : text.Text ?? "";

    public static string[] Blocks(Control root) =>
        [.. root.GetVisualDescendants().OfType<SelectableTextBlock>().Where(text => text.IsEffectivelyVisible).Select(Shown)];

    private static string Shown(Inline inline) => inline switch
    {
        Run run => run.Text ?? "",
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Select(Shown)),
        InlineUIContainer { Child: Button { Content: TextBlock label } } => label.Text ?? "",
        _ => "",
    };
}

/// <summary>Pages a list of entries the way the runner pages history: by entry id cursors and windows of first and last ids.</summary>
internal sealed class ListPager
{
    public List<ConversationEntry> Rows { get; } = [];

    public HistoryResult Read(FakeConversationSession.Call.Page call)
    {
        int IndexOf(string id) => Rows.FindIndex(row => row.Id.Value == id);
        switch (call.Query)
        {
            case HistoryQuery.Latest:
                return Page(Math.Max(0, Rows.Count - call.Count), Rows.Count);
            case HistoryQuery.Before before:
                var end = before.Cursor.Value == "" ? 0 : IndexOf(before.Cursor.Value);
                return end < 0 ? new HistoryResult.Unavailable("Unknown cursor.") : Page(Math.Max(0, end - call.Count), end);
            case HistoryQuery.After after:
                var last = after.Cursor.Value == "" ? -1 : IndexOf(after.Cursor.Value);
                return after.Cursor.Value != "" && last < 0 ? new HistoryResult.Unavailable("Unknown cursor.")
                    : Page(last + 1, Math.Min(Rows.Count, last + 1 + call.Count));
            case HistoryQuery.RefreshWindow refresh:
                var ids = refresh.Window.Value.Split('|');
                return ids is ["", ""] ? Page(0, 0) : Page(IndexOf(ids[0]), IndexOf(ids[1]) + 1);
            case HistoryQuery.AroundRequest around:
                var at = Rows.FindIndex(row => row.Content is ConversationContent.Request request && request.Key == around.Request);
                return at < 0 ? new HistoryResult.Unavailable("No such request.") : Page(Math.Max(0, at - call.Count / 2), Math.Min(Rows.Count, at + call.Count / 2));
            default:
                return new HistoryResult.Unavailable("Unsupported.");
        }
    }

    private HistoryResult.Page Page(int start, int end)
    {
        var entries = Rows.Skip(start).Take(Math.Max(0, end - start)).ToImmutableArray();
        var first = entries.IsEmpty ? (start > 0 ? Rows[start - 1].Id.Value : "") : entries[0].Id.Value;
        var last = entries.IsEmpty ? first : entries[^1].Id.Value;
        return new HistoryResult.Page(1, entries, new HistoryWindow(entries.IsEmpty ? "|" : $"{first}|{last}"),
            new HistoryCursor(entries.IsEmpty ? "" : first), new HistoryCursor(last), start > 0, end < Rows.Count);
    }
}
