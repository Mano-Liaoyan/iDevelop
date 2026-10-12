using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests;

/// <summary>A Claude Code turn whose agent hands work to a background subagent ends when the client exits (#113).</summary>
public sealed class BackgroundSubagentTests : IDisposable
{
    private const string Session = "session-1";
    private const string Answer = "The subagent found 3 files.";
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _gate;

    public BackgroundSubagentTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _gate = Path.Combine(_temp.Create("gates"), "subagent-done");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task The_turn_waits_past_the_success_exit_time_for_a_background_subagent_and_takes_the_later_result()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().Sleep(800).Print(ClaudeBackgroundEnd(Session, "Found 3 files.", Answer)));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.SuccessExitTime = TimeSpan.FromMilliseconds(200);
        var task = Task();
        runs.Start(task);

        var record = await Settled(runs, task.Id);

        Assert.Equal((AttemptStatus.Succeeded, Answer), (record.Status, record.Result));
        Assert.Empty(Events(record).OfType<AttemptEvent.ShutdownForced>());
        Assert.Empty(record.Background);
    }

    [Fact]
    public async Task While_the_subagent_runs_the_attempt_names_it_and_stays_running()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().WaitForFile(_gate).Print(ClaudeBackgroundEnd(Session, "Found 3 files.", Answer)));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.SuccessExitTime = TimeSpan.FromMilliseconds(200);
        var task = Task();
        runs.Start(task);

        await Until(() => runs.Latest.GetValueOrDefault(task.Id) is { Background.Length: > 0 });
        await System.Threading.Tasks.Task.Delay(500);
        var waiting = runs.Latest[task.Id];
        Assert.Equal((AttemptStatus.Running, new BackgroundTask("Survey the files", IsSubagent: true)), (waiting.Status, Assert.Single(waiting.Background)));

        File.WriteAllText(_gate, "");
        var record = await Settled(runs, task.Id);
        Assert.Equal((AttemptStatus.Succeeded, Answer), (record.Status, record.Result));
    }

    [Fact]
    public async Task Once_the_background_work_has_ended_a_client_that_does_not_exit_still_meets_the_success_exit_time()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().Print(ClaudeBackgroundEnd(Session, "Found 3 files.", Answer)).Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.SuccessExitTime = TimeSpan.FromMilliseconds(200);
        var task = Task();
        runs.Start(task);

        var record = await Settled(runs, task.Id);

        Assert.Equal((AttemptStatus.Failed, "The client reported success but did not shut down."), (record.Status, record.Detail));
    }

    [Fact]
    public async Task Cancel_stops_a_turn_that_waits_for_its_background_subagent()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().Hang());
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.ShutdownTime = TimeSpan.FromMilliseconds(500);
        var task = Task();
        runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(task, new CanvasPoint(0, 0))));
        using var session = runs.OpenConversation(task.Id);
        runs.Start(task);
        await Until(() => runs.Latest.GetValueOrDefault(task.Id) is { Background.Length: > 0 });

        Assert.Equal(CommandOutcome.Applied, (await session.CancelAsync(session.Snapshot.Current!.Value, default)).Outcome);

        Assert.Equal(AttemptStatus.Cancelled, (await Settled(runs, task.Id)).Status);
    }

    [Fact]
    public async Task A_subagent_s_message_shows_as_the_subagent_s_own_and_is_not_the_agent_s_reply()
    {
        Install(_fakes, ClientId.ClaudeCode, Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Session)).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().Print(ClaudeBackgroundEnd(Session, "Found 3 files.", Answer)));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = Task();
        runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(task, new CanvasPoint(0, 0))));
        using var session = runs.OpenConversation(task.Id);
        runs.Start(task);
        var record = await Settled(runs, task.Id);

        var page = Assert.IsType<HistoryResult.Page>(await session.ReadPageAsync(record.Id, new HistoryQuery.Latest(), 50, default));
        var messages = page.Entries.Select(entry => entry.Content).OfType<ConversationContent.Message>().Where(message => message.Author == MessageAuthor.Agent).ToArray();
        Assert.Equal(new (string, string?)[]
        {
            ("The subagent runs in the background. I'll wait for it.", null),
            ("Found 3 files.", "general-purpose"),
            (Answer, null),
        }, messages.Select(message => (message.Text, message.Subagent)));
    }

    private AttemptEvent[] Events(AttemptRecord record) => [.. AttemptLog.Read(AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), record.Task, record.Id))];

    private static TaskDefinition Task() => TestNodes.Implement(TestTasks.Design, "Survey", "Survey the files",
        execution: new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" });

    private static async Task<AttemptRecord> Settled(ProjectRuns runs, TaskId task)
    {
        await Until(() => runs.Latest.GetValueOrDefault(task) is { Status: not AttemptStatus.Running } && runs.Active.All(record => record.Task != task));
        return runs.Latest[task];
    }

    private static async System.Threading.Tasks.Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The turn did not reach the expected state.");
            await System.Threading.Tasks.Task.Delay(10);
        }
    }
}
