using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.AttemptEvents;

namespace IDevelop.Core.Tests;

/// <summary>Attempts that go on with one client session read as one conversation (#92).</summary>
public class ConversationChainTests
{
    private static readonly AttemptId B = new(Guid.Parse("019aa000-0000-7000-8000-000000000002"));
    private static readonly AttemptId C = new(Guid.Parse("019aa000-0000-7000-8000-000000000003"));

    private static readonly AttemptHistory Asked = Project(BuildRequested(First), LaunchedAt1s, Said(2, new AgentEvent.SessionStarted("thread-1")),
        Said(3, new AgentEvent.Message("Which fruit?")), Said(4, new AgentEvent.Succeeded("Which fruit?")), Exit(5, 0));

    private static AttemptHistory Answer(AttemptId attempt, AttemptId continues, string text, params AttemptEvent[] events) =>
        Project([BuildRequested(attempt) with { Prompt = text, Continues = new Continuation(continues, "thread-1") }, LaunchedAt1s, .. events]);

    private static AttemptHistory Project(params AttemptEvent[] events) => ConversationHistory.Project(events)!;

    private static string[] Rows(IEnumerable<AttemptHistory> chain) => [.. ConversationChain.Join([.. chain]).SelectMany(history => history.Entries).Select(entry => entry.Content switch
    {
        ConversationContent.Marker marker => $"{marker.Kind}: {marker.Text.Split('\n')[^1]}",
        ConversationContent.Message message => $"{message.Author}: {message.Text}",
        var other => other.GetType().Name,
    })];

    [Fact]
    public void A_continuation_that_resumed_the_same_session_reads_on_without_a_new_attempt_marker()
    {
        var banana = Answer(B, First, "banana", Said(2, new AgentEvent.SessionStarted("thread-1")), Said(3, new AgentEvent.Succeeded("Noted.")), Exit(4, 0));
        var apple = Answer(C, B, "and an apple", Said(2, new AgentEvent.SessionStarted("thread-1")), Said(3, new AgentEvent.Succeeded("Both.")), Exit(4, 0));

        Assert.Equal(
        [
            "attempt: Codex, model gpt-6-sol, reasoning high, conversation Autonomous.",
            "Application: # Implement atomic save\n", "Agent: Which fruit?",
            "Person: banana", "Agent: Noted.",
            "Person: and an apple", "Agent: Both.",
        ], Rows([Asked, banana, apple]));
    }

    [Fact]
    public void A_continuation_still_starting_reads_on()
    {
        var starting = Answer(B, First, "banana");

        Assert.Equal(1, Rows([Asked, starting]).Count(row => row.StartsWith("attempt:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_continuation_with_another_model_level_or_mode_reads_on_after_a_settings_marker()
    {
        var luna = Project(BuildRequested(B) with
            {
                Prompt = "banana", Continues = new Continuation(First, "thread-1"), Settings = CodexHigh with { Model = "gpt-6-luna" },
                Conversation = ConversationMode.Chat,
            },
            LaunchedAt1s, Said(2, new AgentEvent.SessionStarted("thread-1")), Said(3, new AgentEvent.Succeeded("Noted.")), Exit(4, 0));

        Assert.Equal(
        [
            "attempt: Codex, model gpt-6-sol, reasoning high, conversation Autonomous.",
            "Application: # Implement atomic save\n", "Agent: Which fruit?",
            "configuration: Codex, model gpt-6-luna, reasoning high, conversation Chat.",
            "Person: banana", "Agent: Noted.",
        ], Rows([Asked, luna]));
    }

    [Fact]
    public void Each_changed_setting_on_its_own_brings_the_settings_marker_and_the_mode_compares_with_the_earlier_attempts_latest_mode()
    {
        AttemptHistory With(ExecutionSettings settings, ConversationMode mode) => Project(BuildRequested(B) with
            {
                Prompt = "banana", Continues = new Continuation(First, "thread-1"), Settings = settings, Conversation = mode,
            },
            LaunchedAt1s, Said(2, new AgentEvent.SessionStarted("thread-1")), Said(3, new AgentEvent.Succeeded("Noted.")), Exit(4, 0));
        int Settings(params AttemptHistory[] chain) => Rows(chain).Count(row => row.StartsWith("configuration:", StringComparison.Ordinal));
        var chat = Project(BuildRequested(First) with { Conversation = ConversationMode.Chat }, LaunchedAt1s, Said(2, new AgentEvent.SessionStarted("thread-1")),
            Said(3, new AgentEvent.Succeeded("Which fruit?")), Exit(4, 0), NextTurn(5, "banana") with { Conversation = ConversationMode.Autonomous },
            Said(6, new AgentEvent.Succeeded("Noted.")), Exit(7, 0));

        Assert.Equal(0, Settings(Asked, With(CodexHigh, ConversationMode.Autonomous)));
        Assert.Equal(1, Settings(Asked, With(CodexHigh with { Model = "gpt-6-luna" }, ConversationMode.Autonomous)));
        Assert.Equal(1, Settings(Asked, With(CodexHigh with { Reasoning = "low" }, ConversationMode.Autonomous)));
        Assert.Equal(1, Settings(Asked, With(CodexHigh, ConversationMode.MayAsk)));
        Assert.Equal((1, 1), (Settings(chat), Settings(chat, With(CodexHigh, ConversationMode.Autonomous))));
    }

    [Fact]
    public void A_continuation_whose_client_started_another_session_or_never_resumed_keeps_its_new_attempt_marker()
    {
        var other = Answer(B, First, "banana", Said(2, new AgentEvent.SessionStarted("thread-9")), Said(3, new AgentEvent.Succeeded("Which banana?")), Exit(4, 0));
        var failed = Answer(B, First, "banana", Said(2, new AgentEvent.Failed("No conversation found with session ID: thread-1")), Exit(3, 1));
        var unrelated = Answer(C, B, "banana", Said(2, new AgentEvent.SessionStarted("thread-1")), Exit(3, 0));

        Assert.Equal(2, Rows([Asked, other]).Count(row => row.StartsWith("attempt:", StringComparison.Ordinal)));
        Assert.Equal(2, Rows([Asked, failed]).Count(row => row.StartsWith("attempt:", StringComparison.Ordinal)));
        Assert.Equal(2, Rows([Asked, unrelated]).Count(row => row.StartsWith("attempt:", StringComparison.Ordinal)));
        Assert.Equal(1, Rows([unrelated]).Count(row => row.StartsWith("attempt:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_conversation_reads_its_continuations_without_a_marker_between_them_while_the_picker_lists_every_attempt()
    {
        using var temp = new TempFolder();
        var project = temp.Create("project");
        var folder = DataFolder.Attempts(project);
        using (var a = AttemptLog.Create(folder, BuildRequested(First))) { a.Append(Said(1, new AgentEvent.SessionStarted("thread-1"))); a.Append(Said(2, new AgentEvent.Succeeded("Which fruit?"))); a.Append(Exit(3, 0)); }
        using (var b = AttemptLog.Create(folder, BuildRequested(B) with { Prompt = "banana", Continues = new Continuation(First, "thread-1") }))
        {
            b.Append(Said(1, new AgentEvent.SessionStarted("thread-1"))); b.Append(Said(2, new AgentEvent.Succeeded("Noted."))); b.Append(Exit(3, 0));
        }

        await using var runs = ProjectRuns.Open(project, new ClientDirectory(CommandResolver.Create([], [])));
        using var session = runs.OpenConversation(TestTasks.Build);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(B, new HistoryQuery.Latest(), 100, default));

        Assert.Equal(["attempt"], page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Marker>().Select(marker => marker.Kind));
        Assert.Equal(["# Implement atomic save\n", "Which fruit?", "banana", "Noted."],
            page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Message>().Select(message => message.Text));
        Assert.Equal([First, B], (await session.ListAttemptsAsync(default)).Select(attempt => attempt.Id));
        Assert.Equal((false, false), (session.Snapshot.Actions.Terminal.Enabled, session.Snapshot.Actions.Terminal.ShowsWhenUnavailable));
    }
}
