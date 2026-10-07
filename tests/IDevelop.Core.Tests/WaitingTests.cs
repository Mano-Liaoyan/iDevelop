using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests;

/// <summary>A node's conversation mode decides when it waits for the person, with the fake agent behind on-disk shims.</summary>
[Collection(ProcessCollection.Name)]
public sealed class WaitingTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private const string Asking = "I need one answer first.\n\n```idevelop\n{\"status\": \"asking\", \"question\": \"Which fruit?\"}\n```";
    private static readonly TaskId SayHiId = TestTasks.Design;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<ClientId, ExecutionSettings> Settings = new()
    {
        [ClientId.ClaudeCode] = new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" },
        [ClientId.Codex] = new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" },
        [ClientId.Pi] = new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" },
        [ClientId.Antigravity] = new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" },
    };

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _evidence;

    public WaitingTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _evidence = _temp.Create("evidence");
    }

    public static TheoryData<ClientId> AllClients => new(Clients.All);

    public void Dispose() => _temp.Dispose();

    [Theory]
    [MemberData(nameof(AllClients))]
    public async Task A_May_ask_node_that_asks_waits_with_its_question_and_the_reply_continues_the_same_attempt(ClientId client)
    {
        Install(_fakes, client,
            Resuming(client, Session).RecordArguments(Evidence("turn-2.json")).Print(SessionLine(client, Session)).Print(ReplyLines(client, "Wrote banana.")),
            Fresh(client).CaptureStdin(Evidence("turn-1.txt")).Print(SessionLine(client, Session)).Print(ReplyLines(client, Asking)));
        var clients = await _fakes.DiscoverAsync();
        var task = SayHi(client, ConversationMode.MayAsk);
        await using (var first = ProjectRuns.Open(_project, clients))
        {
            var asked = await Settles(first, () => Task.FromResult<object>(first.Start(task)));

            Assert.Equal((AttemptStatus.WaitingForInput, new Pending.Question("Which fruit?")), (asked.Status, asked.Pending));
            Assert.Contains("If you cannot go on without an answer from the person, ask instead of guessing.", File.ReadAllText(Evidence("turn-1.txt")));
            Assert.Equal(new StartProblem.Waiting("Say hi"), first.Check(task));
        }

        await using var runs = ProjectRuns.Open(_project, clients);
        var waiting = runs.Latest[SayHiId];
        Assert.Equal((AttemptStatus.WaitingForInput, new Pending.Question("Which fruit?")), (waiting.Status, waiting.Pending));

        var answered = await Settles(runs, async () => await runs.SendAsync(task, "banana", stopTurn: false));

        Assert.Equal((waiting.Id, AttemptStatus.Succeeded, "Wrote banana.", (Pending?)null), (answered.Id, answered.Status, answered.Result, answered.Pending));
        Assert.Equal([TurnOutcome.Succeeded, TurnOutcome.Succeeded], answered.Turns.Select(turn => turn.Outcome));
        Assert.Equal("banana", answered.Turns[1].Message);
        if (client == ClientId.Codex)
            Assert.Equal(Session, JsonDocument.Parse(File.ReadAllText(Evidence("turn-2.json.thread.json"))).RootElement.GetProperty("params").GetProperty("threadId").GetString());
        else Assert.Contains(Session, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Evidence("turn-2.json")))!);
    }

    [Fact]
    public async Task An_Autonomous_node_finishes_even_when_its_agent_asks()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Asking)));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());

        var record = await Settles(runs, () => Task.FromResult<object>(runs.Start(SayHi(ClientId.Codex, ConversationMode.Autonomous))));

        Assert.Equal((AttemptStatus.Succeeded, (Pending?)null), (record.Status, record.Pending));
        Assert.Equal(new TerminalResult.Refused(new TerminalProblem.NotWaiting("Say hi")), runs.OpenInTerminal(SayHiId));
    }

    [Fact]
    public async Task A_May_ask_node_that_finishes_without_a_block_succeeds_and_an_unreadable_block_waits_with_its_problem()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done.\n\n```idevelop\n{\"status\": \"asking\",\n```")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done.")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = SayHi(ClientId.Codex, ConversationMode.MayAsk);

        var done = await Settles(runs, () => Task.FromResult<object>(runs.Start(task)));
        var unreadable = await Settles(runs, async () => await runs.SendAsync(task, "Check again.", stopTurn: false));

        Assert.Equal(AttemptStatus.Succeeded, done.Status);
        Assert.Equal(AttemptStatus.WaitingForInput, unreadable.Status);
        Assert.StartsWith("The block is not valid JSON.", Assert.IsType<Pending.UnreadableBlock>(unreadable.Pending).Problem);
    }

    [Fact]
    public async Task A_Chat_node_waits_after_every_turn_until_the_person_marks_it_done()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Changed it.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Here is a plan.")));
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        var task = SayHi(ClientId.Codex, ConversationMode.Chat);

        var first = await Settles(runs, () => Task.FromResult<object>(runs.Start(task)));
        var second = await Settles(runs, async () => await runs.SendAsync(task, "Change it.", stopTurn: false));
        Assert.Null(runs.MarkDone(SayHiId));

        Assert.Equal((AttemptStatus.WaitingForInput, new Pending.Reply()), (first.Status, first.Pending));
        Assert.Equal((first.Id, AttemptStatus.WaitingForInput), (second.Id, second.Status));
        var done = runs.Latest[SayHiId];
        Assert.Equal((first.Id, AttemptStatus.Succeeded, "Changed it.", (Pending?)null), (done.Id, done.Status, done.Result, done.Pending));
        await using var reopened = ProjectRuns.Open(_project, clients);
        Assert.Equal((AttemptStatus.Succeeded, 2), (reopened.Latest[SayHiId].Status, reopened.Latest[SayHiId].Turns.Count));
    }

    [Fact]
    public async Task A_turn_that_ends_waiting_shows_Running_until_teardown_releases_the_lock_and_Mark_done_then_applies()
    {
        Install(_fakes, ClientId.Codex,
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Here is a plan.")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = SayHi(ClientId.Codex, ConversationMode.Chat);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.BeforeRelease = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        var waiting = await Settles(runs, async () =>
        {
            try
            {
                var started = Assert.IsType<StartResult.Started>(runs.Start(task));
                await entered.Task.WaitAsync(Patience);
                var during = (runs.Latest[SayHiId].Status, string.Join(", ", runs.Active.Select(record => $"{record.TaskTitle} {record.Status}")), runs.MarkDone(SayHiId),
                    await runs.SendAsync(task, "Change it.", stopTurn: false));
                Assert.Equal((AttemptStatus.Running, "Say hi Running", (StartProblem?)null,
                    new SendResult.Refused(new SendProblem.Ending("Say hi"))), during);
                return started;
            }
            finally
            {
                release.TrySetResult();
            }
        });
        Assert.Equal(AttemptStatus.WaitingForInput, waiting.Status);
        Assert.Null(runs.MarkDone(SayHiId));
        Assert.Equal(AttemptStatus.Succeeded, runs.Latest[SayHiId].Status);
    }

    [Fact]
    public async Task Cancelling_a_waiting_node_records_it_cancelled_and_lets_it_run_again()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, Asking)));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = SayHi(ClientId.Codex, ConversationMode.MayAsk);
        await Settles(runs, () => Task.FromResult<object>(runs.Start(task)));
        Assert.IsType<StartResult.Refused>(runs.Start(task));

        await runs.CancelAsync(SayHiId);

        Assert.Equal((AttemptStatus.Cancelled, (Pending?)null), (runs.Latest[SayHiId].Status, runs.Latest[SayHiId].Pending));
        Assert.Null(runs.Check(task));
    }

    [Fact]
    public async Task Changing_the_mode_while_a_node_waits_applies_from_the_reply_on()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Here is a plan.")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        await Settles(runs, () => Task.FromResult<object>(runs.Start(SayHi(ClientId.Codex, ConversationMode.Chat))));

        var record = await Settles(runs, async () => await runs.SendAsync(SayHi(ClientId.Codex, ConversationMode.Autonomous), "Go ahead.", stopTurn: false));

        Assert.Equal((AttemptStatus.Succeeded, ConversationMode.Autonomous), (record.Status, record.Conversation));
    }

    [Fact]
    public async Task A_reply_to_a_waiting_node_keeps_the_model_and_reasoning_its_attempt_started_with()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).RecordArguments(Evidence("turn-2.json")).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Done.")),
            Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Here is a plan.")));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var task = SayHi(ClientId.Codex, ConversationMode.Chat);
        await Settles(runs, () => Task.FromResult<object>(runs.Start(task)));

        var changed = task with { Execution = new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" } };
        var record = await Settles(runs, async () => await runs.SendAsync(changed, "Go ahead.", stopTurn: false));

        using var thread = JsonDocument.Parse(File.ReadAllText(Evidence("turn-2.json.thread.json")));
        using var turn = JsonDocument.Parse(File.ReadAllText(Evidence("turn-2.json.turn.json")));
        Assert.Equal("gpt-6-sol", thread.RootElement.GetProperty("params").GetProperty("model").GetString());
        Assert.Equal("high", turn.RootElement.GetProperty("params").GetProperty("effort").GetString());
        Assert.Equal(Settings[ClientId.Codex], record.Requested);
    }

    [Fact]
    public void The_agent_work_decides_the_next_step_from_the_latest_attempt()
    {
        var node = new NodeContext(SayHi(ClientId.Codex, ConversationMode.MayAsk), "");
        var requested = new AttemptEvent.Requested(AttemptEvents.T0, AttemptId.New(), SayHiId, "Say hi", Settings[ClientId.Codex], "p", "codex", [])
        {
            Conversation = ConversationMode.MayAsk,
        };
        AttemptRecord Fold(params AttemptEvent[] events) => AttemptReducer.Replay([requested, .. events])!;
        var asked = Fold(Agent(new AgentEvent.SessionStarted(Session)), Agent(new AgentEvent.Succeeded(Asking)), new AttemptEvent.Exited(AttemptEvents.T0, 0, ""));
        var failed = Fold(Agent(new AgentEvent.Failed("No quota.")), new AttemptEvent.Exited(AttemptEvents.T0, 1, ""));

        Assert.StartsWith("# Say hi\n", Assert.IsType<NodeStep.RunTurn>(AgentWork.Instance.Next(node, null)).Prompt);
        Assert.Equal(new NodeStep.WaitForPerson(new Pending.Question("Which fruit?")), AgentWork.Instance.Next(node, asked));
        Assert.Equal(new NodeStep.Finish(Asking), AgentWork.Instance.Next(node, AttemptReducer.Apply(asked, new AttemptEvent.MarkedDone(AttemptEvents.T0))));
        Assert.Equal(new NodeStep.Fail("No quota."), AgentWork.Instance.Next(node, failed));
    }

    [Theory]
    [InlineData("Done.", "absent")]
    [InlineData("Text\n```idevelop\n{\"status\": \"asking\", \"question\": \"Why?\"}\n```\n\n", "asking")]
    [InlineData("```idevelop\n[1]\n```", "The block holds JSON that is not an object.")]
    [InlineData("```idevelop\n{\"status\": \"done\"}\n```\nThen more text.", "absent")]
    [InlineData("Cut off.\n```idevelop\n{\"status\": \"asking\",", "The block has no closing fence.")]
    [InlineData("```idevelop\n{\"status\": \"one\"}\n```\nand\r\n```idevelop\n{\"status\": \"two\"}\n```", "two")]
    public void A_result_block_is_read_only_at_the_end_of_the_final_message(string text, string expected)
    {
        var read = ResultBlock.Read(text) switch
        {
            ResultBlock.Absent => "absent",
            ResultBlock.Readable readable => readable.Status,
            ResultBlock.Unreadable unreadable => unreadable.Problem,
            _ => null,
        };

        Assert.Equal(expected, read);
    }

    [Theory]
    [InlineData("Done.", "Done.")]
    [InlineData("Here is the plan.\r\n\r\n```idevelop\n{\"status\": \"proposal\"}\n```\n", "Here is the plan.")]
    [InlineData("```idevelop\n{\"status\": \"asking\", \"question\": \"Why?\"}\n```", "")]
    [InlineData("Cut off.\n```idevelop\n{\"status\": \"asking\",", "Cut off.\n```idevelop\n{\"status\": \"asking\",")]
    [InlineData("Odd.\n```idevelop\n[1]\n```", "Odd.\n```idevelop\n[1]\n```")]
    [InlineData("```idevelop\n{\"status\": \"done\"}\n```\nThen more text.", "```idevelop\n{\"status\": \"done\"}\n```\nThen more text.")]
    public void The_prose_of_a_message_leaves_out_only_a_block_that_was_read(string text, string expected)
    {
        Assert.Equal(expected, ResultBlock.Prose(text));
    }

    [Fact]
    public void A_May_ask_turn_that_asks_without_a_session_fails_because_no_answer_could_reach_it()
    {
        var requested = new AttemptEvent.Requested(AttemptEvents.T0, AttemptId.New(), SayHiId, "Say hi", Settings[ClientId.Pi], "p", "pi", [])
        {
            Conversation = ConversationMode.MayAsk,
        };

        var record = AttemptReducer.Replay([requested, Agent(new AgentEvent.Succeeded(Asking)), new AttemptEvent.Exited(AttemptEvents.T0, 0, "")])!;

        Assert.Equal((AttemptStatus.Failed, "Pi reported no session, so iDevelop could not wait for your answer."), (record.Status, record.Detail));
    }

    private static AttemptEvent.Agent Agent(AgentEvent e) => new(AttemptEvents.T0, e);

    private static TaskDefinition SayHi(ClientId client, ConversationMode mode) =>
        TestNodes.Implement(SayHiId, "Say hi", "Create hello.txt containing the fruit I name.", execution: Settings[client], conversation: mode);

    /// <summary>Does <paramref name="act"/>, then waits until the task's attempt neither runs nor is about to.</summary>
    private static async Task<AttemptRecord> Settles(ProjectRuns runs, Func<Task<object>> act)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (runs.Latest.GetValueOrDefault(SayHiId) is { Status: not AttemptStatus.Running } record && runs.Active.IsEmpty)
            {
                settled.TrySetResult(record);
            }
        }

        runs.Changed += OnChanged;
        try
        {
            var result = await act();
            Assert.False(result is StartResult.Refused or SendResult.Refused, $"refused: {result}");
            return await settled.Task.WaitAsync(Patience);
        }
        finally
        {
            runs.Changed -= OnChanged;
        }
    }

    private string Evidence(string name) => Path.Combine(_evidence, name);
}
