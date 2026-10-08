using System.Diagnostics;
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
    public string Checkout => Path.Combine(Preparation.Git.Folder, ".worktrees", "93f23689", "90d5b0a2");
    public int Launches => LaunchMarkers.Runs(Fakes.LaunchFolder!, ClientId.Codex);
    public string JournalLock => Path.Combine(Preparation.Git.Folder, ".idp", "runs", "write.lock");

    public TurnFixture(ConversationMode conversation = ConversationMode.Autonomous, bool readOnly = false)
    {
        Writer = PreparationFixture.Writer(T) with
        {
            Execution = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" }, Conversation = conversation,
        };
        if (readOnly)
        {
            var blueprint = Writer.Blueprint;
            var work = (WorkSpec.Agent)blueprint.Work;
            Writer = new(T, new(blueprint.Key, blueprint.Name, work with { Access = AgentAccess.ReadOnly }, blueprint.Fields, blueprint.Defaults))
            { Title = Writer.Title, Execution = Writer.Execution, Conversation = conversation };
        }
        Preparation = new(FixtureWorkflow(Writer));
        Evidence = _temp.Create("evidence");
        Fakes = new(_temp.Create("bin")) { LaunchFolder = _temp.Create("launches") };
    }

    public async Task Open(FakeRule? rule = null)
    {
        Shim = FakeAgents.Install(Fakes, ClientId.Codex, rule ?? Success());
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

    public FakeRule Success() => FakeAgents.Fresh(ClientId.Codex)
        .RecordWorkingDirectory(Path.Combine(Evidence, "cwd.txt"))
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
        .Write("result.txt", "done\n")
        .Print(FakeAgents.ReplyLines(ClientId.Codex, "Done."));

    public FakeRule Waiting(bool hang = false)
    {
        var rule = FakeAgents.Fresh(ClientId.Codex)
            .RecordWorkingDirectory(Path.Combine(Evidence, "cwd.txt"))
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
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

    public ResultRecord Publish(SettledTurn turn)
    {
        Assert.IsType<RunDecision.Recorded>(Preparation.Store.CloseAttempt(Preparation.Permit, Preparation.Op(),
            turn.Address.Launch.Attempt, TerminalAttemptOutcome.Succeeded, turn.Log));
        return Assert.IsType<Publication.Accepted>(Preparation.Materializer().Publish(turn.Lease, Preparation.Op(), turn.Address.Launch.Attempt)).Result;
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
