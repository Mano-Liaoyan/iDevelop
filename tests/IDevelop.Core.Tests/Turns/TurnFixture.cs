using System.Diagnostics;
using System.Text.Json;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Turns;

internal sealed class TurnFixture : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly TempFolder _temp = new();
    public PreparationFixture Preparation { get; }
    public FakeClients Fakes { get; }
    public ProjectRuns Runs { get; private set; } = null!;
    public string Shim { get; private set; } = "";
    public string Evidence { get; }
    public TaskDefinition Writer { get; }
    public ClientId Client { get; }
    public string Checkout => Path.Combine(Preparation.Git.Folder, ".worktrees", "93f23689", "90d5b0a2");
    public int Launches => LaunchMarkers.Runs(Fakes.LaunchFolder!, Client);
    public string JournalLock => Path.Combine(Preparation.Git.Folder, ".idp", "runs", "write.lock");

    public TurnFixture(ConversationMode conversation = ConversationMode.Autonomous, bool readOnly = false,
        ClientId client = ClientId.Codex, string model = "gpt-6-sol", Func<Workflow, Workflow>? configure = null, string? folder = null)
    {
        Client = client;
        Writer = PreparationFixture.Writer(T) with
        {
            Execution = new(client) { Model = model, Reasoning = "high" }, Conversation = conversation,
        };
        if (readOnly)
        {
            var blueprint = Writer.Blueprint;
            var work = (WorkSpec.Agent)blueprint.Work;
            Writer = new(T, new(blueprint.Key, blueprint.Name, work with { Access = AgentAccess.ReadOnly }, blueprint.Fields, blueprint.Defaults))
            { Title = Writer.Title, Execution = Writer.Execution, Conversation = conversation };
        }
        var workflow = FixtureWorkflow(Writer);
        Preparation = new(configure?.Invoke(workflow) ?? workflow, folder: folder);
        Evidence = _temp.Create("evidence");
        Fakes = new(_temp.Create("bin")) { LaunchFolder = _temp.Create("launches") };
    }

    public async Task Open(FakeRule? rule = null)
    {
        Shim = FakeAgents.Install(Fakes, Client, rule ?? Success());
        Runs = OpenRuns(await Fakes.DiscoverAsync());
    }

    public ProjectRuns OpenRuns(ClientDirectory clients)
    {
        var runs = ProjectRuns.Open(Preparation.Git.Folder, clients);
        runs.Store = Preparation.Store;
        runs.GitEnvironment = Preparation.Git.Environment;
        runs.MaterializerClock = new PreparationFixture.Clock();
        runs.ShutdownTime = TimeSpan.FromMilliseconds(250);
        runs.LeaveTimeout = TimeSpan.FromSeconds(5);
        return runs;
    }

    public FakeRule Success() => FakeAgents.Fresh(Client)
        .RecordWorkingDirectory(Path.Combine(Evidence, "cwd.txt"))
        .Print(FakeAgents.SessionLine(Client, "session-1"))
        .Write("result.txt", "done\n")
        .Print(FakeAgents.ReplyLines(Client, "Done."));

    public FakeRule Waiting(bool hang = false)
    {
        var rule = FakeAgents.Fresh(Client)
            .RecordWorkingDirectory(Path.Combine(Evidence, "cwd.txt"))
            .Print(FakeAgents.SessionLine(Client, "session-1"))
            .Write("result.txt", "done\n");
        return hang ? rule.Hang() : rule.WaitForFile(Path.Combine(Evidence, "gate"));
    }

    public TurnIntent.First First(OperationId? operation = null) => new(operation ?? Preparation.Op(), T, new AttemptCause.Initial());
    public string Folder(LaunchKey launch) => Preparation.Store.AttemptFolder(W, Preparation.RunId, T, launch.Attempt);
    public AttemptEvidence Log(LaunchKey launch) => AttemptEvidence.Read(Folder(launch));
    public async Task<RunningTurn> Start(TurnIntent? intent = null) =>
        Assert.IsType<TurnStart.Started>(await Runs.StartTurn(Preparation.Permit, intent ?? First()).WaitAsync(Bound)).Turn;
    public async Task<SettledTurn> Settled(RunningTurn turn) =>
        Assert.IsType<TurnSettlement.Settled>(await turn.Settlement.WaitAsync(Bound)).Turn;
    public FileStream LockJournal() => new(JournalLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private void ConfigureRacerGit()
    {
        Preparation.Git.Git("config", "user.name", "E2");
        Preparation.Git.Git("config", "user.email", "e2@example.test");
        Preparation.Git.Git("config", "commit.gpgSign", "false");
    }

    private IDevelop.Core.Tests.Runs.Racer CrashRacer(OperationId operation, string point, int launches = 1,
        LaunchKey? next = null, string prompt = "Use the fixture")
    {
        string[] args = ["turn-crash", Preparation.Git.Folder, W.Value.ToString("D"), Preparation.RunId.Value.ToString("D"),
            T.Value.ToString("D"), operation.Value.ToString("D"), Fakes.Folder, Fakes.LaunchFolder!, launches.ToString(), point];
        if (next is { } launch) args = [.. args, launch.Attempt.Value.ToString("D"), launch.Turn.ToString(), prompt];
        return new(args);
    }

    private void TrackLaunches(Processes processes)
    {
        foreach (var file in Directory.EnumerateFiles(Fakes.LaunchFolder!, "*.json"))
        {
            var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(file))!;
            if (arguments.Contains("app-server") || arguments.Contains("-p") || arguments.Contains("--print="))
                processes.Add(int.Parse(Path.GetFileName(file).Split('-')[0]));
        }
    }

    public async Task<LaunchKey> Crash(Processes processes, OperationId operation, string point, int launches = 1,
        LaunchKey? next = null)
    {
        ConfigureRacerGit();
        Preparation.ReleaseControl();
        var racer = CrashRacer(operation, point, launches, next);
        try
        {
            Assert.Equal("Owned:", await racer.Line());
            Assert.Equal(point, await racer.Line());
            await racer.Exit();
            Assert.Equal(73, racer.ExitCode);
        }
        finally
        {
            racer.Dispose();
            TrackLaunches(processes);
        }
        var launch = next ?? Assert.Single(Preparation.Read().Preparations).Key;
        _ = Preparation.Permit;
        Assert.Equal(point is "runner.prepared" or "runner.request.after" or "journal.close-turn.after" ? [] : new[] { launch }, Preparation.Read().Fenced);
        return launch;
    }

    public ResultRecord Publish(SettledTurn turn)
    {
        Assert.IsType<RunDecision.Recorded>(Preparation.Store.CloseAttempt(Preparation.Permit, Preparation.Op(),
            turn.Address.Launch.Attempt, TerminalAttemptOutcome.Succeeded, turn.Log));
        return Assert.IsType<Publication.Accepted>(Preparation.Materializer().Publish(turn.Lease, Preparation.Op(), turn.Address.Launch.Attempt)).Result;
    }

    public static async Task SuccessfulControl()
    {
        await using var control = new TurnFixture();
        await control.Open();
        var turn = await control.Settled(await control.Start());
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        control.Publish(turn);
        Assert.Equal("Succeeded", turn.Attempt.Status.ToString());
        Assert.Equal(0, Assert.IsType<RootExit.Exited>(turn.Exit.Exit).Code);
        Assert.Single(control.Log(turn.Address.Launch).Events.OfType<AttemptEvent.Launched>());
        Assert.Single(control.Log(turn.Address.Launch).Events.OfType<AttemptEvent.CleanedUp>());
        Assert.Equal(1, control.Launches);
        Assert.Single(control.Preparation.Read().Claims);
        Assert.Single(control.Preparation.Read().Results);
    }

    public static async Task RestingNextControl()
    {
        await using var control = new TurnFixture(ConversationMode.Chat);
        await control.Open();
        var first = await control.Settled(await control.Start());
        Assert.IsType<Release.Released>(first.Release());
        FakeAgents.Install(control.Fakes, ClientId.Codex, FakeAgents.Resuming(ClientId.Codex, "session-1")
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1")).Print(FakeAgents.ReplyLines(ClientId.Codex, "Second.")));
        var next = await control.Settled(await control.Start(new TurnIntent.Next(control.Preparation.Op(), new(first.Address.Launch.Attempt, 2), "Use the fixture")));
        Assert.Equal("WaitingForInput", next.Attempt.Status.ToString());
        Assert.Single(control.Log(next.Address.Launch).Events.OfType<AttemptEvent.TurnRequested>());
        Assert.Equal(2, control.Launches);
    }

    public async ValueTask DisposeAsync()
    {
        if (Runs is not null) await Runs.DisposeAsync();
        Fakes.Dispose();
        Preparation.Dispose();
        _temp.Dispose();
    }

    public static async Task WaitUntilAsync(Func<bool> done)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            try { if (done()) return; }
            catch (IOException) { }
            catch (JsonException) { }
            Assert.True(started.Elapsed < Bound, "The condition did not become true in time.");
            await System.Threading.Tasks.Task.Delay(50);
        }
    }

    public sealed class ProbeBarrier(string point) : IDisposable
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Probe(string step)
        {
            if (step != point) return;
            Reached.TrySetResult();
            _released.Task.WaitAsync(Bound).GetAwaiter().GetResult();
        }
        public void Dispose() => _released.TrySetResult();
    }
}
