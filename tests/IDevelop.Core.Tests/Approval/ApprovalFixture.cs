using IDevelop.Core.Tests.Git;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>
/// A project on scratch Git that no run owns yet, opened in a window with one fake Codex. Each task answers from its own
/// scripted folder, chosen by the brief that starts its prompt, as in the coordinator's fixture.
/// </summary>
internal sealed class ApprovalFixture : IAsyncDisposable
{

    /// <summary>The diamond fixture's HEAD: <c>root.txt</c>, <c>plan.txt</c>, and <c>a.txt</c>.</summary>
    public static readonly CommitId Head = new("adfe40b30c176fb407933286f51d15ea9b54cdc3");

    private readonly TempFolder _temp = new();
    private readonly Dictionary<TaskId, FakeRule[]> _turns = [];
    private readonly List<ProjectRuns> _windows = [];

    public ApprovalFixture(Workflow workflow)
    {
        Git = new();
        Git.Diamond();
        Workflow = workflow;
        Evidence = _temp.Create("evidence");
        Fakes = new(_temp.Create("bin")) { LaunchFolder = _temp.Create("launches") };
    }

    public GitFixture Git { get; }
    public Workflow Workflow { get; set; }
    public FakeClients Fakes { get; }
    public string Evidence { get; }
    public string Project => Git.Folder;
    public ProjectRuns Runs { get; private set; } = null!;

    public ApprovalFixture Answer(TaskId task, params FakeRule[] turns)
    {
        _turns[task] = turns;
        return this;
    }

    public async Task Open()
    {
        Install();
        Runs = await Window();
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

    /// <summary>Opens the project in another window, which the fixture closes at the end.</summary>
    public async Task<ProjectRuns> Window()
    {
        var runs = ProjectRuns.Open(Project, await Fakes.DiscoverAsync());
        runs.GitEnvironment = Git.Environment;
        runs.MaterializerClock = new PreparationFixture.Clock();
        runs.ShutdownTime = TimeSpan.FromMilliseconds(250);
        runs.LeaveTimeout = TimeSpan.FromSeconds(5);
        runs.CoordinatorRetry = TimeSpan.FromMilliseconds(50);
        _windows.Add(runs);
        return runs;
    }

    /// <summary>Closes the window and opens the project again, as a restart does.</summary>
    public async Task Reopen()
    {
        await Runs.DisposeAsync();
        _windows.Remove(Runs);
        Runs = await Window();
    }

    public RunPreflight Preflight() => Runs.Preflight(Workflow);

    public string Folder(TaskId task) => Path.Combine(Evidence, Name(task));

    public int Launches(TaskId task) => File.Exists(Path.Combine(Folder(task), "count")) ? int.Parse(File.ReadAllText(Path.Combine(Folder(task), "count"))) : 0;

    public int TotalLaunches => LaunchMarkers.Runs(Fakes.LaunchFolder!, ClientId.Codex);

    /// <summary>The runs of the workflow that have a journal.</summary>
    public RunId[] ApprovedRuns() => RunsOf(Project);

    public static RunId[] RunsOf(string project)
    {
        var folder = Path.Combine(project, ".idp", "runs", W.ToString());
        return Directory.Exists(folder)
            ? [.. Directory.EnumerateDirectories(folder).Where(run => File.Exists(Path.Combine(run, "events.jsonl")) &&
                new FileInfo(Path.Combine(run, "events.jsonl")).Length > 0).Select(run => new RunId(Guid.Parse(Path.GetFileName(run)))).OrderBy(run => run.Value)]
            : [];
    }

    public RunRecord Read(RunId run) => Assert.IsType<RunRead.Loaded>(RunStore.Open(Project).Read(W, run)).Record;

    public string GitText(params string[] arguments) => Git.Git(arguments);

    /// <summary>The bytes of <paramref name="file"/> in <paramref name="task"/>'s accepted result, or null when the commit lacks it.</summary>
    public string? ResultFile(RunId run, TaskId task, string file)
    {
        var commit = Assert.IsType<CodeOutput.Produced>(Read(run).CurrentResults[task].Code).Code.Commit.Hex;
        var shown = Git.Run(Project, "show", $"{commit}:{file}");
        return shown.ExitCode == 0 ? shown.Text : null;
    }

    public byte[] Index() => File.ReadAllBytes(Path.Combine(Project, ".git", "index"));

    public string Text(string relative) => File.ReadAllText(Git.PathOf(relative));

    public async ValueTask DisposeAsync()
    {
        foreach (var window in _windows) await window.DisposeAsync();
        Fakes.Dispose();
        Git.Dispose();
        _temp.Dispose();
    }
}
