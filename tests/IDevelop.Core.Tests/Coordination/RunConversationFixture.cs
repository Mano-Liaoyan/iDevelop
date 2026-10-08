using System.Text.Json;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A workflow run on scratch Git whose tasks a person talks to (E3c.1). One fake client answers every turn: the start of
/// a turn's prompt picks the task's scripted folder, so the folder's count is that task's launch count and its stdin files
/// hold its prompts. A later turn's prompt is the person's text, so <see cref="Route"/> names which task answers it.
/// </summary>
internal sealed class RunConversationFixture : IAsyncDisposable
{
    private readonly TempFolder _temp = new();
    private readonly HostQuestions? _questions;
    private readonly Dictionary<TaskId, FakeRule[]> _turns = [];
    private readonly List<(string Prompt, TaskId Task)> _routes = [];

    public RunConversationFixture(Workflow workflow, ClientId client = ClientId.Codex, HostQuestions? questions = null)
    {
        Client = client;
        _questions = questions;
        Preparation = new(workflow, CoordinatorFixture.PlanCommit);
        Evidence = _temp.Create("evidence");
        Fakes = new(_temp.Create("bin")) { LaunchFolder = _temp.Create("launches") };
    }

    public ClientId Client { get; }
    public PreparationFixture Preparation { get; }
    public FakeClients Fakes { get; }
    public string Evidence { get; }
    public ProjectRuns Runs { get; private set; } = null!;
    public WorkflowRunCoordinator Coordinator { get; private set; } = null!;
    public RunAddress Address => Coordinator.Address;
    public RunView View => Coordinator.View;

    /// <summary>An agent of <paramref name="client"/> whose first prompt starts with <c>"Build &lt;name&gt;."</c>.</summary>
    public static TaskDefinition Agent(TaskId task, ConversationMode conversation, ClientId client = ClientId.Codex, bool readOnly = false) =>
        Turns.TurnFixture.Agent(task, conversation, readOnly, client, client == ClientId.ClaudeCode ? "claude-haiku-4-5" : "gpt-6-sol")
            .WithField("brief", $"Build {CoordinatorFixture.Name(task)}.")! with { Title = CoordinatorFixture.Name(task) };

    /// <summary>
    /// A turn that records its arguments and folder, reports <paramref name="session"/>, waits for <paramref name="gate"/> when
    /// it names a file, and answers <paramref name="text"/>.
    /// </summary>
    public FakeRule Says(TaskId task, int turn, string text, string session = "session-1", string? gate = null)
    {
        var rule = FakeRule.On()
            .RecordArguments(Path.Combine(Evidence, $"{CoordinatorFixture.Name(task)}-{turn}.args"))
            .RecordWorkingDirectory(Path.Combine(Evidence, $"{CoordinatorFixture.Name(task)}-{turn}.cwd"))
            .Print(FakeAgents.SessionLine(Client, session));
        return (gate is null ? rule : rule.WaitForFile(gate)).Print(FakeAgents.ReplyLines(Client, text));
    }

    public string Gate(string name) => Path.Combine(Evidence, name);

    public void Open(string gate) => File.WriteAllText(Gate(gate), "go");

    /// <summary>The turns <paramref name="task"/> answers, in launch order.</summary>
    public RunConversationFixture Answer(TaskId task, params FakeRule[] turns)
    {
        _turns[task] = turns;
        return this;
    }

    /// <summary>A later turn whose prompt starts with <paramref name="prompt"/> goes to <paramref name="task"/>'s script.</summary>
    public RunConversationFixture Route(string prompt, TaskId task)
    {
        _routes.Add((prompt, task));
        return this;
    }

    public string Folder(TaskId task) => Path.Combine(Evidence, CoordinatorFixture.Name(task));

    public int Launches(TaskId task) => File.Exists(Path.Combine(Folder(task), "count")) ? int.Parse(File.ReadAllText(Path.Combine(Folder(task), "count"))) : 0;

    public int TotalLaunches => LaunchMarkers.Runs(Fakes.LaunchFolder!, Client);

    public string Prompt(TaskId task, int launch) => File.ReadAllText(Path.Combine(Folder(task), $"{launch}.stdin"));

    /// <summary>The session a turn resumed, from the Codex thread request or the Claude Code arguments.</summary>
    public string? Resumed(TaskId task, int turn)
    {
        var args = Path.Combine(Evidence, $"{CoordinatorFixture.Name(task)}-{turn}.args");
        if (Client == ClientId.Codex)
        {
            using var thread = JsonDocument.Parse(File.ReadAllText(args + ".thread.json"));
            var parameters = thread.RootElement.GetProperty("params");
            return thread.RootElement.GetProperty("method").GetString() == "thread/resume" ? parameters.GetProperty("threadId").GetString() : null;
        }
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args))!;
        var index = Array.IndexOf(arguments, "--resume");
        return index < 0 ? null : arguments[index + 1];
    }

    public string WorkingFolder(TaskId task, int turn) => File.ReadAllText(Path.Combine(Evidence, $"{CoordinatorFixture.Name(task)}-{turn}.cwd"));

    public RunRecord Read() => Preparation.Read();

    public AttemptId Attempt(TaskId task) => RunProjection.LatestAttempts(Read())[task];

    public string AttemptFolder(TaskId task, AttemptId? attempt = null) => Preparation.Store.AttemptFolder(W, Preparation.RunId, task, attempt ?? Attempt(task));

    public AttemptEvidence Log(TaskId task) => AttemptEvidence.Read(AttemptFolder(task));

    /// <summary>How many lines of <paramref name="type"/> the task's newest attempt log holds.</summary>
    public int Lines(TaskId task, string type) =>
        File.ReadAllLines(Path.Combine(AttemptFolder(task), "events.jsonl")).Count(line => line.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal));

    /// <summary>The project-relative checkout of <paramref name="task"/>'s newest attempt.</summary>
    public string Checkout(TaskId task)
    {
        var record = Read();
        return Path.Combine(Preparation.Git.Folder, record.Preparations[new(Attempt(task), 1)].Location.Owner.RelativePath);
    }

    public void Install()
    {
        foreach (var (task, turns) in _turns)
        {
            var folder = Directory.CreateDirectory(Folder(task)).FullName;
            for (var turn = 0; turn < turns.Length; turn++) File.WriteAllText(Path.Combine(folder, $"{turn + 1}.json"), turns[turn].StepsJson());
        }
        var options = _turns.Keys.Select(task => ($"Build {CoordinatorFixture.Name(task)}.", task)).Concat(_routes)
            .Select(route => (route.Item1, FakeRule.On().Scripted(Folder(route.Item2))));
        FakeAgents.Install(Fakes, Client, (Client == ClientId.Codex ? FakeRule.On("app-server") : FakeRule.On()).Choose([.. options]));
    }

    /// <summary>Installs the fake client with every answer so far, then opens the project and the run's coordinator.</summary>
    public async Task Open(bool install = true)
    {
        if (install) Install();
        Runs = OpenRuns(await Fakes.DiscoverAsync());
        Coordinator = Assert.IsType<RunOpen.Opened>(Runs.OpenRun(W, Preparation.RunId)).Coordinator;
    }

    public ProjectRuns OpenRuns(ClientDirectory clients)
    {
        var runs = ProjectRuns.Open(Preparation.Git.Folder, clients, _questions);
        runs.Store = Preparation.Store;
        runs.GitEnvironment = Preparation.Git.Environment;
        runs.MaterializerClock = new PreparationFixture.Clock();
        runs.ShutdownTime = TimeSpan.FromMilliseconds(250);
        runs.LeaveTimeout = TimeSpan.FromSeconds(5);
        runs.CoordinatorRetry = TimeSpan.FromMilliseconds(50);
        return runs;
    }

    /// <summary>The task's conversation as this window's run owns it.</summary>
    public IConversationSession Session(TaskId task) => Runs.OpenConversation(Coordinator, task);

    /// <summary>Opens the project in a second window, which only reads the run while this one controls it.</summary>
    public async Task<(ProjectRuns Runs, WorkflowRunCoordinator Coordinator)> SecondWindow()
    {
        var runs = OpenRuns(await Fakes.DiscoverAsync());
        return (runs, Assert.IsType<RunOpen.Opened>(runs.OpenRun(W, Preparation.RunId)).Coordinator);
    }

    /// <summary>Closes the project and opens it again in a new window, as a restart does.</summary>
    public async Task Reopen()
    {
        await Runs.DisposeAsync();
        Runs = OpenRuns(await Fakes.DiscoverAsync());
        Coordinator = Assert.IsType<RunOpen.Opened>(Runs.OpenRun(W, Preparation.RunId)).Coordinator;
    }

    public async Task Resume() => Assert.IsType<RunCommand.Accepted>(await Coordinator.Resume(Address).WaitAsync(CoordinatorFixture.Bound));

    /// <summary>Signals the coordinator and returns the projection of a decision made after the signal.</summary>
    public Task<RunView> Decided()
    {
        var before = View.Decision;
        Coordinator.Refresh();
        return Until(view => view.Decision > before);
    }

    public async Task<RunView> Until(Func<RunView, bool> condition)
    {
        try { return await Coordinator.Until(condition).WaitAsync(CoordinatorFixture.Bound); }
        catch (TimeoutException) { throw new TimeoutException("The run did not reach the state in time: " + CoordinatorFixture.Describe(View)); }
    }

    public Task<RunView> UntilStatus(RunStatus status) => Until(view => view.Status == status);

    /// <summary>Waits until <paramref name="task"/> rests for the person at its <paramref name="turns"/>th turn.</summary>
    public Task<RunView> UntilWaiting(TaskId task, int turns) => Until(view => view.Tasks.GetValueOrDefault(task) is { State: TaskState.Waiting } &&
        Log(task).Record is { Turns.Count: var count } && count == turns);

    public async ValueTask DisposeAsync()
    {
        if (Runs is not null) await Runs.DisposeAsync();
        Fakes.Dispose();
        Preparation.Dispose();
        _temp.Dispose();
    }
}
