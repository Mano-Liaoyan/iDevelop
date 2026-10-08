using System.Text.Json;
using System.Text.Json.Nodes;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// A workflow run on scratch Git with one fake Codex. Each task answers from its own scripted folder, chosen by the
/// brief that starts its prompt, so the folder's count is that task's launch count and its stdin files hold its prompts.
/// </summary>
internal sealed class CoordinatorFixture : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    public static readonly TaskId A = Task("a1");
    public static readonly TaskId B = Task("b2");
    public static readonly TaskId C = Task("c3");
    public static readonly TaskId D = Task("d4");
    public static readonly TaskId X = Task("f9");

    /// <summary>The diamond fixture's commit with <c>plan.txt="approved\n"</c> and no <c>a.txt</c>.</summary>
    public static readonly CommitId PlanCommit = new("81ddb7c330112c7f16700ed002803a04b0bce693");

    private static readonly Dictionary<TaskId, string> Names = new() { [A] = "A", [B] = "B", [C] = "C", [D] = "D", [X] = "X" };
    private readonly TempFolder _temp = new();
    private readonly Dictionary<TaskId, FakeRule[]> _turns = [];

    public CoordinatorFixture(Workflow workflow, CommitId? runBase = null)
    {
        Preparation = new(workflow, runBase ?? PlanCommit);
        Evidence = _temp.Create("evidence");
        Fakes = new(_temp.Create("bin")) { LaunchFolder = _temp.Create("launches") };
    }

    public PreparationFixture Preparation { get; }
    public FakeClients Fakes { get; }
    public string Evidence { get; }
    public ProjectRuns Runs { get; private set; } = null!;
    public ClientDirectory Clients { get; private set; } = null!;
    public WorkflowRunCoordinator Coordinator { get; private set; } = null!;
    public RunAddress Address => Coordinator.Address;
    public RunView View => Coordinator.View;

    private static TaskId Task(string suffix) => new(Guid.Parse($"00000000-0000-0000-0000-0000000000{suffix}"));

    public static string Name(TaskId task) => Names[task];

    /// <summary>A writer agent, or a read-only one, whose prompt starts with <c>"Build &lt;name&gt;."</c>.</summary>
    public static TaskDefinition Agent(TaskId task, bool readOnly = false, ConversationMode conversation = ConversationMode.Autonomous) =>
        Turns.TurnFixture.Agent(task, conversation, readOnly).WithField("brief", $"Build {Name(task)}.")! with { Title = Name(task) };

    public static Workflow Graph(IEnumerable<TaskDefinition> tasks, params (TaskId From, TaskId To)[] dependencies)
    {
        var workflow = FixtureWorkflow([.. tasks]);
        foreach (var (from, to) in dependencies) workflow = Connect(workflow, from, to);
        return workflow;
    }

    /// <summary><c>A → B</c> plus a read-only root <c>X</c>.</summary>
    public static Workflow Chain() => Graph([Agent(A), Agent(B), Agent(X, readOnly: true)], (A, B));

    /// <summary><c>A → {B, C} → D</c> plus a read-only root <c>X</c>.</summary>
    public static Workflow Diamond() => Graph([Agent(A), Agent(B), Agent(C), Agent(D), Agent(X, readOnly: true)], (A, B), (A, C), (B, D), (C, D));

    /// <summary>A turn that writes <paramref name="file"/> and reports <c>"&lt;name&gt; ready.\n"</c>.</summary>
    public static FakeRule Writes(TaskId task, string file, string text) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task)))
        .Write(file, text)
        .Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    public static FakeRule Reports(TaskId task) => FakeRule.On()
        .Print(FakeAgents.SessionLine(ClientId.Codex, "session-" + Name(task)))
        .Print(FakeAgents.ReplyLines(ClientId.Codex, $"{Name(task)} ready.\n"));

    /// <summary>The turns <paramref name="task"/> answers, in launch order.</summary>
    public CoordinatorFixture Answer(TaskId task, params FakeRule[] turns)
    {
        _turns[task] = turns;
        return this;
    }

    public string Folder(TaskId task) => Path.Combine(Evidence, Name(task));

    public int Launches(TaskId task) => File.Exists(Path.Combine(Folder(task), "count")) ? int.Parse(File.ReadAllText(Path.Combine(Folder(task), "count"))) : 0;

    public string Prompt(TaskId task, int launch = 1) => File.ReadAllText(Path.Combine(Folder(task), $"{launch}.stdin"));

    public int TotalLaunches => LaunchMarkers.Runs(Fakes.LaunchFolder!, ClientId.Codex);

    public RunRecord Read() => Preparation.Read();

    public int Claims(TaskId task) => Read().Claims.Keys.Count(key => Read().Attempts[key.Attempt].Task == task);

    public ResultRecord Result(TaskId task) => Read().CurrentResults[task];

    /// <summary>The bytes of <paramref name="file"/> in <paramref name="task"/>'s accepted result commit, or null without it.</summary>
    public string? ResultFile(TaskId task, string file)
    {
        var commit = Assert.IsType<CodeOutput.Produced>(Result(task).Code).Code.Commit.Hex;
        var shown = Preparation.Git.Run(Preparation.Git.Folder, "show", $"{commit}:{file}");
        return shown.ExitCode == 0 ? shown.Text : null;
    }

    /// <summary>The refs under <paramref name="prefix"/>, one per line.</summary>
    public string Refs(string prefix) => Preparation.Git.Git("for-each-ref", "--format=%(refname)", prefix);

    /// <summary>The project-relative checkout of <paramref name="task"/>'s newest attempt.</summary>
    public string Checkout(TaskId task)
    {
        var record = Read();
        var attempt = RunProjection.LatestAttempts(record)[task];
        return Path.Combine(Preparation.Git.Folder, record.Preparations[new(attempt, 1)].Location.Owner.RelativePath);
    }

    /// <summary>Installs the fake client with every answer so far, then opens the project and the run's coordinator.</summary>
    public async Task Open(bool install = true)
    {
        if (install) Install();
        Clients = await Fakes.DiscoverAsync();
        Runs = OpenRuns(Clients);
        Coordinator = Assert.IsType<RunOpen.Opened>(Runs.OpenRun(W, Preparation.RunId)).Coordinator;
    }

    public void Install()
    {
        foreach (var (task, turns) in _turns)
        {
            var folder = Directory.CreateDirectory(Folder(task)).FullName;
            for (var turn = 0; turn < turns.Length; turn++) File.WriteAllText(Path.Combine(folder, $"{turn + 1}.json"), turns[turn].StepsJson());
        }
        FakeAgents.Install(Fakes, ClientId.Codex, FakeAgents.Fresh(ClientId.Codex)
            .Choose([.. _turns.Keys.Select(task => ($"Build {Name(task)}.", FakeRule.On().Scripted(Folder(task))))]));
    }

    public ProjectRuns OpenRuns(ClientDirectory clients)
    {
        var runs = ProjectRuns.Open(Preparation.Git.Folder, clients);
        runs.Store = Preparation.Store;
        runs.GitEnvironment = Preparation.Git.Environment;
        runs.MaterializerClock = new PreparationFixture.Clock();
        runs.ShutdownTime = TimeSpan.FromMilliseconds(250);
        runs.LeaveTimeout = TimeSpan.FromSeconds(5);
        runs.CoordinatorRetry = TimeSpan.FromMilliseconds(50);
        return runs;
    }

    /// <summary>
    /// Runs <paramref name="task"/>'s initial turn in a racer process under the coordinator's own operation, and exits it at
    /// <paramref name="point"/>, as a crash does. Call it before <see cref="Open"/>, which then takes control after the crash.
    /// </summary>
    public async Task Crash(TaskId task, string point)
    {
        Install();
        Preparation.Git.Git("config", "user.name", "E2");
        Preparation.Git.Git("config", "user.email", "e2@example.test");
        Preparation.Git.Git("config", "commit.gpgSign", "false");
        using var racer = new Runs.Racer("turn-crash", Preparation.Git.Folder, W.Value.ToString("D"), Preparation.RunId.Value.ToString("D"),
            task.Value.ToString("D"), RunOperations.Initial(Preparation.RunId, task).Value.ToString("D"), Fakes.Folder, Fakes.LaunchFolder!, "1", point);
        Assert.Equal("Owned:", await racer.Line());
        Assert.Equal(point, await racer.Line());
        await racer.Exit();
        Assert.Equal(73, racer.ExitCode);
    }

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

    /// <summary>Signals the coordinator and returns the projection of a decision made after the signal.</summary>
    public Task<RunView> Decided()
    {
        var before = View.Decision;
        Coordinator.Refresh();
        return Until(view => view.Decision > before);
    }

    public async Task Resume() => Assert.IsType<RunCommand.Accepted>(await Coordinator.Resume(Address).WaitAsync(Bound));

    public async Task<RunView> Until(Func<RunView, bool> condition)
    {
        try { return await Coordinator.Until(condition).WaitAsync(Bound); }
        catch (TimeoutException) { throw new TimeoutException("The run did not reach the state in time: " + Describe(View)); }
    }

    public static string Describe(RunView view) => $"{view.Status} ({view.Phase}, slots {view.Slots}, problem {view.Problem ?? "none"}): " +
        string.Join("; ", view.Tasks.Values.Select(task => $"{Names.GetValueOrDefault(task.Task, task.Task.ToString())} {task.State}" +
            (task.Refusal is { } refusal ? $" refused {refusal.Problem} task {(refusal.Task is { } holder ? Names.GetValueOrDefault(holder, holder.ToString()) : "-")} seq {refusal.Sequence}" : "") + (task.Block is { } block ? $" block {block.Problem} {block.Detail}" : "") +
            (task.Unresolved is { } unresolved ? $" unresolved {unresolved}" : "") + (task.Problem is { } problem ? $" problem {problem}" : "") +
            (task.End is { } end ? $" end {end}" : "")));

    public Task<RunView> UntilStatus(RunStatus status) => Until(view => view.Status == status);

    public TaskState State(TaskId task) => View.Tasks[task].State;

    public async ValueTask DisposeAsync()
    {
        if (Runs is not null) await Runs.DisposeAsync();
        Fakes.Dispose();
        Preparation.Dispose();
        _temp.Dispose();
    }
}
