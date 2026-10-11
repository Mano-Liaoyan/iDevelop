using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

/// <summary>
/// A message resumes the task's own client session (#92). Pi finds a session only in the folder where it ran, so after the
/// project folder moved, a message to a Pi conversation is refused with a note, and nothing launches.
/// </summary>
public sealed class SessionContinuityTests : IDisposable
{
    private const string Original = "01a1225d-4958-74d8-8b83-b2462362dc0b";
    private static readonly TaskId SayHiId = TestTasks.Design;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly ExecutionSettings Pi = new(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" };
    private static readonly string[] PiTurn = ["-p", "--mode", "json", "--model", "deepseek/deepseek-v4-pro", "--thinking", "high"];
    private static readonly SendResult.Refused Moved = new(new SendProblem.CannotStart(new StartProblem.SessionFolderMoved(ClientId.Pi)));

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;

    public SessionContinuityTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public async Task A_Pi_message_resumes_its_session_by_id_in_the_folder_where_the_session_ran()
    {
        Install(_fakes, ClientId.Pi,
            Resuming(ClientId.Pi, Original).RecordArguments(Evidence("continue.json")).RecordWorkingDirectory(Evidence("continue-cwd.txt"))
                .Print(SessionLine(ClientId.Pi, Original)).Print(ReplyLines(ClientId.Pi, "lantern")),
            Fresh(ClientId.Pi).Print(SessionLine(ClientId.Pi, Original)).Print(ReplyLines(ClientId.Pi, "OK")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var first = await Settled(runs, () => Task.FromResult(Assert.IsType<StartResult.Started>(runs.Start(SayHi())).Attempt.Id));

        var continued = await Settled(runs, async () => Assert.IsType<SendResult.Continued>(await runs.SendAsync(SayHi(), "What word?", stopTurn: false)).Attempt.Id);

        Assert.Equal((_project, _project, Original), (first.WorkingFolder, first.SessionFolder, first.SessionId));
        Assert.Equal([.. PiTurn, "--session-id", Original], Recorded("continue.json"));
        Assert.Equal(_project, File.ReadAllText(Evidence("continue-cwd.txt")));
        Assert.Equal((new Continuation(first.Id, Original) { Folder = _project }, _project), Requested(_project, continued.Id));
        Assert.Equal((true, Original, _project), (continued.Resumed, continued.SessionId, continued.SessionFolder));
    }

    [Fact]
    public async Task After_the_project_moved_a_Pi_message_is_refused_with_a_note_and_nothing_launches()
    {
        Install(_fakes, ClientId.Pi,
            Resuming(ClientId.Pi, Original).RecordArguments(Evidence("resumed.json")).Print(SessionLine(ClientId.Pi, Original)).Print(ReplyLines(ClientId.Pi, "Unknown")),
            On().With("--fork").RecordArguments(Evidence("forked.json")).Print(ReplyLines(ClientId.Pi, "lantern")),
            Fresh(ClientId.Pi).Print(SessionLine(ClientId.Pi, Original)).Print(ReplyLines(ClientId.Pi, "OK")));
        var clients = await _fakes.DiscoverAsync();
        AttemptRecord first;
        await using (var before = ProjectRuns.Open(_project, clients))
        {
            first = await Settled(before, () => Task.FromResult(Assert.IsType<StartResult.Started>(before.Start(SayHi())).Attempt.Id));
        }

        var moved = Path.Combine(Path.GetDirectoryName(_project)!, "moved-project");
        Directory.Move(_project, moved);
        await using var runs = ProjectRuns.Open(moved, clients);

        Assert.Equal(Moved.Problem, runs.CheckSend(SayHi()));
        Assert.Equal(Moved, await runs.SendAsync(SayHi(), "What word?", stopTurn: false));

        Assert.False(File.Exists(Evidence("resumed.json")), "Pi resumed the session in the moved folder.");
        Assert.False(File.Exists(Evidence("forked.json")), "Pi forked the session.");
        Assert.Equal((first.Id, AttemptStatus.Succeeded), (runs.Latest[SayHiId].Id, runs.Latest[SayHiId].Status));
        Assert.Single(Directory.GetDirectories(Path.Combine(moved, ".idp", "attempts", SayHiId.ToString())));
    }

    [Fact]
    public async Task A_reply_to_a_waiting_Pi_attempt_after_the_project_moved_is_refused_and_the_attempt_keeps_waiting()
    {
        var old = Path.Combine(Path.GetDirectoryName(_project)!, "old-project");
        Install(_fakes, ClientId.Pi,
            Resuming(ClientId.Pi, Original).RecordArguments(Evidence("resumed.json")).Print(SessionLine(ClientId.Pi, Original)).Print(ReplyLines(ClientId.Pi, "Which banana?")));
        var waiting = PlantWaiting(old);

        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var chat = SayHi() with { Conversation = ConversationMode.Chat };
        Assert.Equal((AttemptStatus.WaitingForInput, old), (runs.Latest[SayHiId].Status, runs.Latest[SayHiId].SessionFolder));

        Assert.Equal(Moved, await runs.SendAsync(chat, "banana", stopTurn: false));

        Assert.False(File.Exists(Evidence("resumed.json")), "Pi resumed the session in the moved folder.");
        Assert.Equal((waiting, AttemptStatus.WaitingForInput, 1), (runs.Latest[SayHiId].Id, runs.Latest[SayHiId].Status, runs.Latest[SayHiId].Turns.Count));
    }

    [Fact]
    public async Task After_the_project_moved_Open_in_terminal_refuses_a_waiting_Pi_conversation_with_the_composer_s_note()
    {
        Install(_fakes, ClientId.Pi);
        var waiting = PlantWaiting(Path.Combine(Path.GetDirectoryName(_project)!, "old-project"));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(SayHi() with { Conversation = ConversationMode.Chat }, new CanvasPoint(0, 0))));
        using var session = runs.OpenConversation(SayHiId);
        var actions = session.Snapshot.Actions;

        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.Blocked(new StartProblem.SessionFolderMoved(ClientId.Pi))), runs.OpenInTerminal(SayHiId));
        Assert.Equal((false, true, false), (actions.Terminal.Enabled, actions.Terminal.ShowsWhenUnavailable, actions.Send.Enabled));
        Assert.Equal(actions.Send.Reason, actions.Terminal.Reason);
        Assert.StartsWith("Pi can't continue this conversation after the project folder moved", actions.Terminal.Reason, StringComparison.Ordinal);
        Assert.Empty(AttemptLog.Read(AttemptLog.FolderOf(Path.Combine(_project, ".idp", "attempts"), SayHiId, waiting)).OfType<AttemptEvent.HandedToTerminal>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Open_in_terminal_hands_off_a_waiting_Pi_conversation_in_its_folder_and_another_client_s_after_a_move(bool claude, bool moved)
    {
        var settings = claude ? new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "low" } : Pi;
        Install(_fakes, settings.Client);
        PlantWaiting(moved ? Path.Combine(Path.GetDirectoryName(_project)!, "old-project") : _project, settings);
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(SayHi(settings) with { Conversation = ConversationMode.Chat }, new CanvasPoint(0, 0))));
        using var session = runs.OpenConversation(SayHiId);

        Assert.True(session.Snapshot.Actions.Terminal.Enabled, session.Snapshot.Actions.Terminal.Reason);
        Assert.False(session.Snapshot.Actions.Terminal.ShowsWhenUnavailable);
        Assert.IsType<TerminalResult.HandedOff>(runs.OpenInTerminal(SayHiId));
    }

    [Fact]
    public async Task A_Pi_attempt_logged_before_folders_resumes_its_session_by_id_as_before()
    {
        Install(_fakes, ClientId.Pi,
            Resuming(ClientId.Pi, Original).RecordArguments(Evidence("continue.json")).Print(SessionLine(ClientId.Pi, Original)).Print(ReplyLines(ClientId.Pi, "lantern")));
        var earlier = AttemptId.New();
        using (var log = AttemptLog.Create(Path.Combine(_project, ".idp", "attempts"),
            new AttemptEvent.Requested(T0, earlier, SayHiId, "Say hi", Pi, "# Say hi\n", "pi", ["-p"])))
        {
            log.Append(new AttemptEvent.Launched(T0.AddSeconds(1), 4242, T0.AddSeconds(1)));
            log.Append(new AttemptEvent.Agent(T0.AddSeconds(2), new AgentEvent.SessionStarted(Original)));
            log.Append(new AttemptEvent.Agent(T0.AddSeconds(3), new AgentEvent.Succeeded("OK")));
            log.Append(new AttemptEvent.Exited(T0.AddSeconds(4), 0, ""));
        }

        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var continued = await Settled(runs, async () => Assert.IsType<SendResult.Continued>(await runs.SendAsync(SayHi(), "What word?", stopTurn: false)).Attempt.Id);

        Assert.Equal([.. PiTurn, "--session-id", Original], Recorded("continue.json"));
        Assert.Equal((new Continuation(earlier, Original), _project), Requested(_project, continued.Id));
        Assert.Equal((true, _project), (continued.Resumed, continued.SessionFolder));
    }

    [Fact]
    public async Task Every_other_client_resumes_its_session_by_id_after_the_project_moved()
    {
        Install(_fakes, ClientId.ClaudeCode,
            Resuming(ClientId.ClaudeCode, Original).RecordArguments(Evidence("continue.json"))
                .Print(SessionLine(ClientId.ClaudeCode, Original)).Print(ReplyLines(ClientId.ClaudeCode, "lantern")),
            Fresh(ClientId.ClaudeCode).Print(SessionLine(ClientId.ClaudeCode, Original)).Print(ReplyLines(ClientId.ClaudeCode, "OK")));
        var claude = new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "low" };
        var clients = await _fakes.DiscoverAsync();
        AttemptRecord first;
        await using (var before = ProjectRuns.Open(_project, clients))
        {
            first = await Settled(before, () => Task.FromResult(Assert.IsType<StartResult.Started>(before.Start(SayHi(claude))).Attempt.Id));
        }

        var moved = Path.Combine(Path.GetDirectoryName(_project)!, "moved-project");
        Directory.Move(_project, moved);
        await using var runs = ProjectRuns.Open(moved, clients);
        var continued = await Settled(runs, async () => Assert.IsType<SendResult.Continued>(await runs.SendAsync(SayHi(claude), "What word?", stopTurn: false)).Attempt.Id);

        Assert.Contains("--resume", Recorded("continue.json"));
        Assert.Equal((new Continuation(first.Id, Original) { Folder = _project }, moved), Requested(moved, continued.Id));
        Assert.Equal((true, Original, moved), (continued.Resumed, continued.SessionId, continued.SessionFolder));
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 5, 0, 0, TimeSpan.Zero);

    private static TaskDefinition SayHi(ExecutionSettings? settings = null) =>
        TestNodes.Implement(SayHiId, "Say hi", "Create hello.txt containing hi. Then reply with DONE.", execution: settings ?? Pi);

    /// <summary>Starts an attempt and returns its record once it settled, with no client process left in this window.</summary>
    private static async Task<AttemptRecord> Settled(ProjectRuns runs, Func<Task<AttemptId>> start)
    {
        var settled = NextSettled(runs);
        var id = await start();
        var record = await settled;
        Assert.Equal(id, record.Id);
        return record;
    }

    private static Task<AttemptRecord> NextSettled(ProjectRuns runs)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (runs.Latest.GetValueOrDefault(SayHiId) is { Status: not AttemptStatus.Running } record && runs.Active.IsEmpty)
            {
                settled.TrySetResult(record);
            }
        };
        return settled.Task.WaitAsync(Patience);
    }

    /// <summary>Writes a Chat attempt, of Pi unless <paramref name="settings"/> say otherwise, that waits for the person, whose session ran in <paramref name="folder"/>.</summary>
    private AttemptId PlantWaiting(string folder, ExecutionSettings? settings = null)
    {
        var waiting = AttemptId.New();
        using var log = AttemptLog.Create(Path.Combine(_project, ".idp", "attempts"),
            new AttemptEvent.Requested(T0, waiting, SayHiId, "Say hi", settings ?? Pi, "# Say hi\n", "pi", ["-p"]) { WorkingFolder = folder, Conversation = ConversationMode.Chat });
        log.Append(new AttemptEvent.Launched(T0.AddSeconds(1), 4242, T0.AddSeconds(1)));
        log.Append(new AttemptEvent.Agent(T0.AddSeconds(2), new AgentEvent.SessionStarted(Original)));
        log.Append(new AttemptEvent.Agent(T0.AddSeconds(3), new AgentEvent.Succeeded("Which fruit?")));
        log.Append(new AttemptEvent.Exited(T0.AddSeconds(4), 0, ""));
        return waiting;
    }

    private static (Continuation? Continues, string? WorkingFolder) Requested(string project, AttemptId attempt)
    {
        var requested = Assert.IsType<AttemptEvent.Requested>(AttemptLog.Read(AttemptLog.FolderOf(Path.Combine(project, ".idp", "attempts"), SayHiId, attempt))[0]);
        return (requested.Continues, requested.WorkingFolder);
    }

    private string[] Recorded(string file) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Evidence(file)))!;

    private string Evidence(string name) => Path.Combine(_evidence, name);
}
