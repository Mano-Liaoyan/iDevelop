using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.AttemptEvents;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests;

public sealed class ConversationSessionReadTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private static readonly ExecutionSettings Settings = new(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" };
    private static TaskDefinition Node => TestNodes.Implement(TestTasks.Build, "Build", "Build it", execution: Settings);

    public ConversationSessionReadTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("attempts")]
    [InlineData("page")]
    [InlineData("request")]
    public async Task Cancelled_reads_return_a_cancelled_task_without_throwing_synchronously(string operation)
    {
        Seed();
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        using var session = runs.OpenConversation(Node.Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task? read = null;

        var error = Record.Exception(() => { read = operation switch
        {
            "attempts" => session.ListAttemptsAsync(cancellation.Token),
            "page" => session.ReadPageAsync(First, new HistoryQuery.Latest(), 20, cancellation.Token),
            "request" => session.ReadRequestAsync(new RequestKey(new TurnKey(First, 1), "s:q"), cancellation.Token),
            _ => throw new ArgumentException(operation),
        }; });

        Assert.Null(error);
        Assert.NotNull(read);
        Assert.True(read.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(AttemptStatus.WaitingForInput, session.Snapshot.Latest!.Status);
    }

    [Theory]
    [InlineData("bTE1", "m10,m11,m12,m13,m14,m15,m16,m17,m18,m19", "m9", "m20", true, true)]
    [InlineData("bTE", "attempt,prompt,m0,m1,m2,m3,m4,m5,m6,m7", null, "m8", false, true)]
    [InlineData("bTI3", "m18,m19,m20,m21,m22,m23,m24,m25,m26,m27", "m17", null, true, false)]
    public async Task Around_entry_centres_the_page_and_clamps_at_the_chain_ends(string identity, string expected, string? preceding, string? following, bool earlier, bool later)
    {
        using (var log = AttemptLog.Create(DataFolder.Attempts(_project), BuildRequested(First)))
        {
            for (var i = 0; i < 28; i++)
            {
                log.Append(Said(i + 1, new AgentEvent.Message($"m{i}") { Id = $"m{i}" }));
            }

            log.Append(Said(31, new AgentEvent.Succeeded(null)));
            log.Append(Exit(32, 0));
        }

        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        using var session = runs.OpenConversation(Node.Id);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First,
            new HistoryQuery.AroundEntry(new EntryId($"c1/{First}/1/message/i{identity}")), 10, default));
        string Text(ConversationEntry entry) => entry.Content switch
        {
            ConversationContent.Message { Author: MessageAuthor.Agent } text => text.Text,
            ConversationContent.Message => "prompt",
            ConversationContent.Marker marker => marker.Kind,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(30, Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 100, default)).Entries.Length);
        Assert.Equal(expected.Split(','), page.Entries.Select(Text));
        Assert.Equal((earlier, later), (page.HasEarlier, page.HasLater));
        var refreshed = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.RefreshWindow(page.Window), 10, default));
        Assert.Equal(expected.Split(','), refreshed.Entries.Select(Text));
        var before = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Before(page.Before), 1, default));
        var after = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.After(page.After), 1, default));
        Assert.Equal(preceding is null ? [] : new[] { preceding }, before.Entries.Select(Text));
        Assert.Equal(following is null ? [] : new[] { following }, after.Entries.Select(Text));
    }

    [Fact]
    public async Task Around_entry_refuses_an_entry_outside_the_selected_chain()
    {
        Seed();
        var independent = new AttemptId(Guid.Parse("019aa000-0000-7000-8000-000000000002"));
        using (var log = AttemptLog.Create(DataFolder.Attempts(_project), BuildRequested(independent)))
        {
            log.Append(Said(1, new AgentEvent.Message("Elsewhere") { Id = "other" }));
            log.Append(Said(2, new AgentEvent.Succeeded(null)));
            log.Append(Exit(3, 0));
        }

        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        using var session = runs.OpenConversation(Node.Id);
        var query = new HistoryQuery.AroundEntry(new EntryId("c1/019aa000-0000-7000-8000-000000000002/1/message/ib3RoZXI"));
        Assert.Equal(["Elsewhere"], AgentTexts(Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(independent, query, 10, default))));
        Assert.Equal(new HistoryResult.Unavailable("The entry is not in the selected attempt chain. Select its attempt to read it."),
            await session.ReadPageAsync(First, query, 10, default));
    }

    [Fact]
    public async Task Settled_reads_reuse_the_page_and_request_until_the_log_grows()
    {
        var folder = Seed();
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        using var session = runs.OpenConversation(Node.Id);
        var first = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 20, default));
        var again = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 20, default));
        Assert.Equal(first.Entries.ToArray(), again.Entries.ToArray());
        Assert.Equal(first.Revision, again.Revision);
        Assert.Equal(["First"], AgentTexts(first));
        Assert.Equal(6L, session.Snapshot.LogRevision);
        var path = Path.Combine(folder, "events.jsonl");
        var length = new FileInfo(path).Length;
        File.WriteAllText(path, File.ReadAllText(path).Replace("First", "Other", StringComparison.Ordinal)
            .Replace("Fixture?", "Storage?", StringComparison.Ordinal));
        Assert.Equal(length, new FileInfo(path).Length);

        var cached = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 20, default));
        Assert.Equal(first.Entries.ToArray(), cached.Entries.ToArray());
        Assert.Equal(["First"], AgentTexts(cached));
        Assert.Equal("Fixture?", Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(new RequestKey(new TurnKey(First, 1), "s:q"), default)).Questions.Single().Text);
        Assert.Equal(AttemptStatus.WaitingForInput, Assert.Single(await session.ListAttemptsAsync(default)).Status);
        using (var log = AttemptLog.Open(folder))
        {
            log.Append(new AttemptEvent.HandedToTerminal(T0.AddSeconds(7), "/project", "pi --session-id session-1"));
            log.Append(new AttemptEvent.MarkedDone(T0.AddSeconds(8)));
        }

        var updated = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 20, default));
        Assert.Equal(["Other"], AgentTexts(updated));
        Assert.Equal("Opened in terminal in /project\npi --session-id session-1", updated.Entries.Select(entry => entry.Content).OfType<ConversationContent.Marker>().Single(marker => marker.Kind == "terminal").Text);
        Assert.Equal("Storage?", Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(new RequestKey(new TurnKey(First, 1), "s:q"), default)).Questions.Single().Text);
        Assert.Equal(AttemptStatus.Succeeded, Assert.Single(await session.ListAttemptsAsync(default)).Status);
        Assert.Equal((AttemptStatus.WaitingForInput, 6L), (session.Snapshot.Latest!.Status, session.Snapshot.LogRevision));
    }

    [Fact]
    public async Task Snapshots_keep_the_cached_line_count_until_an_owner_reload_observes_growth()
    {
        var folder = Seed();
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        using var session = runs.OpenConversation(Node.Id);
        Assert.Equal(6L, session.Snapshot.LogRevision);
        using (var log = AttemptLog.Open(folder))
        {
            log.Append(new AttemptEvent.MarkedDone(T0.AddSeconds(7)));
        }

        Assert.Equal((AttemptStatus.WaitingForInput, 6L), (session.Snapshot.Latest!.Status, session.Snapshot.LogRevision));
        Assert.Null(runs.MarkDone(Node.Id));
        Assert.Equal((AttemptStatus.Succeeded, 7L), (session.Snapshot.Latest!.Status, session.Snapshot.LogRevision));
        Assert.Equal(AttemptStatus.Succeeded, Assert.Single(await session.ListAttemptsAsync(default)).Status);
        Assert.Equal((AttemptStatus.Succeeded, 7L), (session.Snapshot.Latest!.Status, session.Snapshot.LogRevision));
    }

    [Fact]
    public async Task Owner_open_leaves_history_uncached_until_the_first_session_read()
    {
        var folder = Seed();
        await using var runs = ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver));
        using var session = runs.OpenConversation(Node.Id);
        Assert.Equal((AttemptStatus.WaitingForInput, 6L), (session.Snapshot.Latest!.Status, session.Snapshot.LogRevision));
        var path = Path.Combine(folder, "events.jsonl");
        var length = new FileInfo(path).Length;
        File.WriteAllText(path, File.ReadAllText(path).Replace("First", "Other", StringComparison.Ordinal));
        Assert.Equal(length, new FileInfo(path).Length);
        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 20, default));
        Assert.Equal(["Other"], AgentTexts(page));
        Assert.Equal("First", runs.Latest[Node.Id].LastMessage);
    }

    [Theory]
    [InlineData("attempts")]
    [InlineData("page")]
    [InlineData("request")]
    public async Task Reading_external_growth_keeps_Latest_and_notifications_unchanged(string operation)
    {
        var folder = Seed();
        await using var runs = ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver));
        using var session = runs.OpenConversation(Node.Id);
        var latest = runs.Latest[Node.Id];
        var revision = session.Snapshot.Revision;
        var projectNotifications = 0;
        var sessionNotifications = 0;
        runs.Changed += (_, _) => Interlocked.Increment(ref projectNotifications);
        session.Changed += _ => Interlocked.Increment(ref sessionNotifications);
        using (var log = AttemptLog.Open(folder))
        {
            log.Append(new AttemptEvent.MarkedDone(T0.AddSeconds(7)));
        }

        switch (operation)
        {
            case "attempts":
                Assert.Equal(AttemptStatus.Succeeded, Assert.Single(await session.ListAttemptsAsync(default)).Status);
                break;
            case "page":
                var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(First, new HistoryQuery.Latest(), 20, default));
                Assert.Equal(["First"], AgentTexts(page));
                Assert.Equal(6L, session.Snapshot.LogRevision);
                break;
            case "request":
                Assert.Equal("Fixture?", Assert.IsType<RequestRecord.Question>(await session.ReadRequestAsync(
                    new RequestKey(new TurnKey(First, 1), "s:q"), default)).Questions.Single().Text);
                break;
            default:
                throw new ArgumentException(operation);
        }

        Assert.Same(latest, runs.Latest[Node.Id]);
        Assert.Equal(AttemptStatus.WaitingForInput, session.Snapshot.Latest!.Status);
        Assert.Equal(revision, session.Snapshot.Revision);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref projectNotifications));
        Assert.Equal(0, Volatile.Read(ref sessionNotifications));
    }

    [Fact]
    public async Task Lock_reload_notifies_every_session_and_mark_done_notifies_its_task()
    {
        Seed();
        await using var runs = ProjectRuns.Open(_project, new ClientDirectory(_fakes.Resolver));
        using var own = runs.OpenConversation(Node.Id);
        using var other = runs.OpenConversation(TestTasks.Design);
        var ownNotifications = 0;
        var otherNotifications = 0;
        var projectNotifications = 0;
        var ownChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherChanged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        own.Changed += _ =>
        {
            if (Interlocked.Increment(ref ownNotifications) == 2)
            {
                ownChanged.TrySetResult();
            }
        };
        other.Changed += revision =>
        {
            Interlocked.Increment(ref otherNotifications);
            otherChanged.TrySetResult(revision);
        };
        runs.Changed += (_, _) => Interlocked.Increment(ref projectNotifications);
        Assert.Null(runs.MarkDone(Node.Id));
        await Task.WhenAny(otherChanged.Task, Task.Delay(1000));
        Assert.Equal(1, Volatile.Read(ref otherNotifications));
        await ownChanged.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1L, await otherChanged.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(2, Volatile.Read(ref ownNotifications));
        Assert.Equal(1, Volatile.Read(ref otherNotifications));
        Assert.Equal(1, Volatile.Read(ref projectNotifications));
        Assert.Equal((AttemptStatus.Succeeded, 7L), (own.Snapshot.Latest!.Status, own.Snapshot.LogRevision));
        Assert.Equal(2L, own.Snapshot.Revision);
    }

    [Fact]
    public async Task Another_tasks_twenty_ignored_lines_notify_no_sessions_but_an_own_message_does()
    {
        Seed(waiting: false);
        var noiseGo = Path.Combine(_project, "noise-go");
        Install(_fakes, ClientId.Pi, Fresh(ClientId.Pi).WaitForFile(noiseGo)
            .Print(Enumerable.Repeat("{\"type\":\"ignored\"}", 20)).Print(ReplyLines(ClientId.Pi, "Done")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var other = TestNodes.Implement(TestTasks.Design, "Other", "Build it", execution: Settings);
        runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(Node, new CanvasPoint(0, 0))).Must(TestNodes.Place(other, new CanvasPoint(300, 0))));
        using var session = runs.OpenConversation(Node.Id);
        var notifications = 0;
        session.Changed += _ => Interlocked.Increment(ref notifications);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += _ => reloaded.TrySetResult();
        Assert.IsType<StartResult.Started>(runs.Start(other));
        await Task.WhenAny(reloaded.Task, Task.Delay(1000));
        Assert.Equal(1, Volatile.Read(ref notifications));
        Interlocked.Exchange(ref notifications, 0);
        File.WriteAllText(noiseGo, "go");
        await Until(() => runs.Latest.GetValueOrDefault(other.Id) is { Status: AttemptStatus.Succeeded } && runs.Live(other.Id) is null);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref notifications));
        var go = Path.Combine(_project, "message-go");
        var end = Path.Combine(_project, "message-end");
        Install(_fakes, ClientId.Pi, Fresh(ClientId.Pi).WaitForFile(go)
            .Print("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}]}}""")
            .WaitForFile(end).Print(ReplyLines(ClientId.Pi, "Hello")));
        var notified = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += revision =>
        {
            if (session.Snapshot.Latest is { Status: AttemptStatus.Running, LastMessage: "Hello" })
            {
                notified.TrySetResult(revision);
            }
        };
        var attempt = Assert.IsType<StartResult.Started>(runs.Start(Node)).Attempt.Id;
        File.WriteAllText(go, "go");
        Assert.True(await notified.Task.WaitAsync(TimeSpan.FromSeconds(15)) > 0);
        Assert.True(Volatile.Read(ref notifications) >= 1);
        Assert.Equal((AttemptStatus.Running, "Hello"), (session.Snapshot.Latest!.Status, session.Snapshot.Latest.LastMessage));
        File.WriteAllText(end, "go");
        await Until(() => runs.Latest.GetValueOrDefault(Node.Id) is { Status: AttemptStatus.Succeeded } && runs.Live(Node.Id) is null);
        Assert.Equal(["Hello"], AgentTexts(Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(attempt, new HistoryQuery.Latest(), 20, default))));
    }

    [Fact]
    public async Task Project_notifications_are_exactly_three_with_zero_or_twenty_ignored_lines()
    {
        var counts = new List<int>();
        foreach (var lines in new[] { 0, 20 })
        {
            Install(_fakes, ClientId.Pi, Fresh(ClientId.Pi).Print(Enumerable.Repeat("{\"type\":\"ignored\"}", lines)).Print(ReplyLines(ClientId.Pi, "Done")));
            await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
            var previous = runs.Latest.GetValueOrDefault(Node.Id)?.Id;
            var count = 0;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runs.Changed += (_, _) =>
            {
                Interlocked.Increment(ref count);
                if (runs.Latest.GetValueOrDefault(Node.Id) is { Status: AttemptStatus.Succeeded } completed
                    && completed.Id != previous && runs.Live(Node.Id) is null)
                {
                    finished.TrySetResult();
                }
            };
            Assert.IsType<StartResult.Started>(runs.Start(Node));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("Done", runs.Latest[Node.Id].Result);
            counts.Add(Volatile.Read(ref count));
        }

        Assert.Equal([3, 3], counts);
        Assert.Equal(counts[0], counts[1]);
    }

    private string Seed(bool waiting = true)
    {
        var requested = BuildRequested(First) with { Conversation = waiting ? ConversationMode.Chat : ConversationMode.Autonomous };
        using var log = AttemptLog.Create(DataFolder.Attempts(_project), requested);
        log.Append(Said(1, new AgentEvent.SessionStarted("session-1")));
        log.Append(new AttemptEvent.QuestionRecorded(T0.AddSeconds(2), "s:q", [new AskedQuestion("q:0", "Fixture", "Fixture?", [], false, true)], new QuestionState.Closed(RequestCloseReason.PolicyDenied, null)));
        log.Append(Said(3, new AgentEvent.Message("First")));
        log.Append(Said(4, new AgentEvent.Succeeded("First")));
        log.Append(Exit(5, 0));
        return log.Folder;
    }

    private static string[] AgentTexts(HistoryResult.Page page) => [.. page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Message>().Where(message => message.Author == MessageAuthor.Agent).Select(message => message.Text)];

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The conversation did not reach the expected state.");
            await Task.Delay(10);
        }
    }
}
