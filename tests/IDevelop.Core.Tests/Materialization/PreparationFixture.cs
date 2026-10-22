using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

internal sealed class PreparationFixture : IDisposable
{
    public readonly GitFixture Git;
    public readonly RunStore Store;
    public readonly RunId RunId;
    private int _id = 100;
    private int _op = 1000;
    private CoordinatorPermit? _permit;
    private readonly Dictionary<TaskId, RunLease> _leases = [];
    public readonly CommitId A;
    /// <param name="node">The node whose Run starts the run, or null for a run of every root, as Run Workflow approves.</param>
    /// <param name="branch">The branch the run started from, which its finished work merges into, or null for a detached HEAD.</param>
    public PreparationFixture(Workflow workflow, CommitId? runBase = null, RunId? run = null, Func<GitFixture, CommitId>? configureBase = null,
        string? folder = null, TaskId? node = null, string? branch = null)
    {
        Git = new(folder: folder);
        A = Git.Diamond();
        RunId = run ?? Run;
        Store = RunStore.Open(Git.Folder, new Clock(), () => Id(++_id));
        Assert.IsType<RunDecision.Created>(Store.Approve(W, RunId, Op(), Revision.Capture(workflow),
            new(configureBase?.Invoke(Git) ?? runBase ?? A) { Branch = branch }, node: node));
    }

    public Materializer Materializer(IJoinComposer? joins = null, Action<string>? probe = null, string? project = null, TimeProvider? clock = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        Execution.Materializer.Open(project ?? Git.Folder, Store, joins, clock ?? new Clock(), environment ?? Git.Environment, probe);

    public Materializer FailingGit(string arguments)
    {
        var realGit = CommandResolver.Create((System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;
        var bin = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Git.Folder)!, "failing-git")).FullName;
        Executable.Write(Path.Combine(bin, "git"), "#!/bin/sh\ncase \" $* \" in *' " + arguments + " '*) echo 'fatal: injected failure' >&2; exit 128;; esac\nexec '" +
            realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\n");
        return Materializer(environment: new Dictionary<string, string>(Git.Environment)
        {
            ["PATH"] = bin + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH"),
        });
    }
    public CoordinatorPermit Permit => _permit ??= Assert.IsType<ControlTake.Owned>(RunStore.Open(Git.Folder).TakeControl(W, RunId)).Permit;

    public RunLease Lease(TaskId task)
    {
        if (_leases.TryGetValue(task, out var lease)) return lease;
        lease = Assert.IsType<LeaseTake.Taken>(Permit.TakeTask(task)).Lease;
        _leases.Add(task, lease);
        return lease;
    }

    public void Release(TaskId task)
    {
        if (_leases.Remove(task, out var lease)) lease.Dispose();
    }

    public void ReleaseControl()
    {
        foreach (var lease in _leases.Values) lease.Dispose();
        _leases.Clear();
        _permit?.Dispose();
        _permit = null;
    }

    public OperationId Op() => new(Id(++_op));
    public RunRecord Read() => Assert.IsType<RunRead.Loaded>(Store.Read(W, RunId)).Record;
    public ValueTask<Preparation> Prepare(TaskId task, OperationId? operation = null, AttemptCause? cause = null, string? prompt = null) =>
        Materializer().Prepare(Lease(task), operation ?? Op(), cause ?? new AttemptCause.Initial(), prompt);

    public static TaskDefinition Writer(TaskId task, string template = "{{brief}}")
    {
        var blueprint = new Blueprint(new($"example.writer.{task}", 1), "Writer",
            new WorkSpec.Agent(AgentAccess.Edit, false, PromptTemplate.Parse(template)),
            [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Autonomous));
        return new(task, blueprint) { Execution = Task().Execution, Title = task == T ? "B" : task == C ? "C" : "U" };
    }

    public async Task<ResultRecord> Accept(TaskId task, CommitId commit, string report = "B ready.\n", bool artifact = false, string artifactName = "payload")
    {
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(task));
        Assert.Equal(0, Git.Run(ready.Checkout, "merge", "--ff-only", "--no-edit", commit.Hex).ExitCode);
        if (artifact)
        {
            var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
            File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
            File.WriteAllText(Path.Combine(outbox, "manifest.json"),
                "{\"schema\":1,\"artifacts\":[{\"name\":\"" + artifactName + "\",\"path\":\"payload.bin\"}]}");
        }
        await Close(ready, report);
        return Assert.IsType<Acceptance.Accepted>(Materializer().Accept(Lease(task), Op(), ready.Execution.Launch.Attempt)).Result;
    }

    public async Task Close(Preparation.Ready ready, string report = "B ready.\n",
        TerminalAttemptOutcome outcome = TerminalAttemptOutcome.Succeeded, bool assertMatched = true)
    {
        var checkpoint = ObserveAndLog(ready, report, outcome);
        var task = ready.Execution.Location.Owner.Task;
        var operation = Op();
        var closed = Assert.IsType<Settlement.Closed>(await Materializer().Settle(Lease(task), operation, ready.Execution.Launch, checkpoint));
        if (assertMatched) SettlementAssertions.Matched(closed);
        Assert.IsType<RunDecision.Recorded>(Store.CloseAttempt(Permit, operation, ready.Execution.Launch.Attempt, outcome, checkpoint));
    }

    public LogCheckpoint ObserveAndLog(Preparation.Ready ready, string report = "B ready.\n",
        TerminalAttemptOutcome outcome = TerminalAttemptOutcome.Succeeded)
    {
        var execution = ready.Execution;
        var attempt = Read().Attempts[execution.Launch.Attempt];
        var input = Read().Inputs[execution.Inputs];
        var claimed = Read().Claims.ContainsKey(execution.Launch);
        var claim = Store.Claim(Lease(attempt.Task), Op(), execution.Launch, input, execution.PromptHash);
        if (claimed) Assert.IsType<RunDecision.Existing>(claim);
        else Assert.IsType<RunDecision.Granted>(claim);
        Assert.IsType<RootObservation.Observed>(Materializer().ObserveRootExit(Lease(attempt.Task), Op(), execution.Launch,
            new RootExit.Exited(outcome == TerminalAttemptOutcome.Failed ? 1 : 0)));
        var definition = Read().Revision.Snapshot.Tasks[attempt.Task];
        var folder = Store.AttemptFolder(W, RunId, attempt.Task, attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(
            At, attempt.Id, attempt.Task, definition.Title, definition.Execution!, execution.Prompt, "codex", [])
        {
            RunBinding = new(W, RunId, attempt.Revision, input.Id), Conversation = definition.Conversation,
            ReadOnly = definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review,
            Fix = Read().ReviewOf(attempt.Id),
        }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("fixture")));
            log.Append(new AttemptEvent.Agent(At, outcome == TerminalAttemptOutcome.Failed ? new AgentEvent.Failed("Failed.") : new AgentEvent.Succeeded(report)));
            log.Append(new AttemptEvent.Exited(At, outcome == TerminalAttemptOutcome.Failed ? 1 : 0, ""));
        }
        return Checkpoint(folder);
    }

    public void Dispose()
    {
        ReleaseControl();
        Git.Dispose();
    }

    /// <summary>A crash a probe throws at a step, which a test catches.</summary>
    internal sealed class Crash : Exception;

    /// <summary>Prepares T, commits b.txt on its branch, changes a.txt, adds new.txt, optionally declares an artifact, and closes its turn.</summary>
    internal async Task<Preparation.Ready> ChangedWriter(bool artifact = false, bool assertMatched = true)
    {
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(T));
        Git.Write("b.txt", "B\n", ready.Checkout);
        Assert.Equal(0, Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "b").ExitCode);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67\n", Git.Run(ready.Checkout, "rev-parse", "HEAD").Text);
        Git.Write("a.txt", "A captured\n", ready.Checkout);
        Git.Write("new.txt", "new\n", ready.Checkout);
        if (artifact)
        {
            var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
            File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
            File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
        }
        await Close(ready, assertMatched: assertMatched);
        return ready;
    }

    /// <summary>
    /// Always <see cref="At"/>, and its timers fire at once. A held clock's elapsed time moves only by <see cref="Advance"/>,
    /// so a time budget measured on it counts the time a test gives it, never the time the test's injected work takes.
    /// </summary>
    public sealed class Clock(bool held = false) : TimeProvider
    {
        private long _elapsed;
        public override DateTimeOffset GetUtcNow() => At;
        public override long TimestampFrequency => held ? TimeSpan.TicksPerSecond : base.TimestampFrequency;
        public override long GetTimestamp() => held ? Interlocked.Read(ref _elapsed) : base.GetTimestamp();
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _elapsed, elapsed.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
}
