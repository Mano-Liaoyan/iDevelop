using System.Diagnostics;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A project on scratch Git whose workflow a window runs with one fake Codex. Each task answers from a scripted folder of
/// its own, chosen by the title that starts its prompt, so the folder's count is the task's launch count. A later turn's
/// prompt is the person's text, which <see cref="Route"/> sends to a task's script.
/// </summary>
internal sealed class WorkflowRunFixture : IDisposable
{
    public static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly Dictionary<string, FakeRule[]> _turns = [];
    private readonly List<(string Prompt, string Title)> _routes = [];
    private readonly List<string> _gates = [];
    private readonly List<Shell> _windows = [];
    private ClientDirectory? _clients;

    public WorkflowRunFixture(params WorkflowEdit[] edits)
    {
        WorkflowRunViewModel.ControlRetry = TimeSpan.FromMilliseconds(100);
        WorkflowRunViewModel.RetryDelay = TimeSpan.FromMilliseconds(100);
        Project = _temp.Seed(edits);
        Evidence = _temp.Create("evidence");
        Fakes = new FakeClients(_temp.Create("bin"));
        var config = Path.Combine(_temp.Create("git"), "config");
        File.WriteAllText(config, "[maintenance]\n\tauto = false\n");
        Git = new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_AUTHOR_NAME"] = "E3", ["GIT_AUTHOR_EMAIL"] = "e3@example.test", ["GIT_AUTHOR_DATE"] = "2026-10-08T00:00:00Z",
            ["GIT_COMMITTER_NAME"] = "E3", ["GIT_COMMITTER_EMAIL"] = "e3@example.test", ["GIT_COMMITTER_DATE"] = "2026-10-08T00:00:00Z",
            ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C", ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        _ = Run("init", "-q", "--object-format=sha1", "-b", "main");
        Run("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(Project, "base.txt"), "base\n");
        Run("add", "base.txt");
        Run("commit", "-q", "-m", "base");
    }

    public string Project { get; }

    /// <summary>A second, empty project folder beside the run's, named "other".</summary>
    public string Other() => _temp.Create("other");

    public string Evidence { get; }

    public FakeClients Fakes { get; }

    public IReadOnlyDictionary<string, string> Git { get; }

    /// <summary>An Implement task of Codex whose prompt starts with its title.</summary>
    public static WorkflowEdit.PlaceNode Task(TaskId id, string title, double x, ConversationMode conversation = ConversationMode.Autonomous,
        ExecutionSettings? execution = null) =>
        TaskAt(id, title, x, 90, execution ?? Codex, $"Build {title}.", conversation);

    /// <summary>An Approval node.</summary>
    public static WorkflowEdit.PlaceNode Approval(TaskId id, string title, double x) =>
        new(id, BuiltInBlueprints.Approval, new CanvasPoint(x, 90)) { Title = title };

    public static WorkflowEdit.Connect Dependency(TaskId from, TaskId to) => new(new ConnectionKey(from, to), ConnectionKind.Dependency);

    /// <summary>A Review node whose reviewer is Codex, whose first prompt starts with its title.</summary>
    public static WorkflowEdit.PlaceNode Review(TaskId id, string title, double x) =>
        new(id, BuiltInBlueprints.Review, new CanvasPoint(x, 90)) { Title = title, Settings = new NodeSettings(Codex, ConversationMode.Autonomous) };

    /// <summary>A reviewer's answer with its verdict block.</summary>
    public static string Verdict(string json) => $"I read the change.\n\n```idevelop\n{json}\n```";

    /// <summary>A fix round's answer to each finding.</summary>
    public static string Answers(string answers) => $"I answered each finding.\n\n```idevelop\n{{\"status\": \"answers\", \"answers\": {answers}}}\n```";

    /// <summary>Called with every probe point of each window's runs, off the UI thread.</summary>
    public Action<string>? Probe { get; set; }

    /// <summary>The file system each folder lives on, for Restore. Null reads the real one; a function returning null emulates macOS.</summary>
    public Func<string, ulong?>? Volumes { get; set; }

    /// <summary>A turn that writes <paramref name="file"/> in the task's checkout, then answers <paramref name="reply"/>.</summary>
    public FakeRule Writes(string file, string text, string reply, string session = "session-1", string? gate = null)
    {
        var rule = FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, session));
        return (gate is null ? rule : rule.WaitForFile(Gate(gate))).Write(file, text).Print(FakeAgents.ReplyLines(ClientId.Codex, reply));
    }

    /// <summary>The journal of the project's one run.</summary>
    public RunRecord Record()
    {
        var folder = RunFolders().Single(folder => File.Exists(Path.Combine(folder, "events.jsonl")));
        var workflow = new WorkflowId(Guid.Parse(Path.GetFileName(Path.GetDirectoryName(folder))!));
        return Assert.IsType<RunRead.Loaded>(RunStore.Open(Project).Read(workflow, new RunId(Guid.Parse(Path.GetFileName(folder))))).Record;
    }

    /// <summary>The checkout of <paramref name="task"/>'s newest attempt in the run.</summary>
    public string Checkout(TaskId task)
    {
        var record = Record();
        return Path.Combine(Project, record.Preparations[new(RunProjection.LatestAttempts(record)[task], 1)].Location.Owner.RelativePath);
    }

    /// <summary>
    /// Approves a run of the project's workflow in another process, which exits once the run is open, then runs
    /// <paramref name="title"/>'s initial turn in another and ends that one at <paramref name="point"/>, as a crash or a
    /// kill ends the app. Call it before any window opens the project.
    /// </summary>
    public void Crash(TaskId task, string title, string point)
    {
        _ = Clients();
        var workflowFile = Directory.EnumerateFiles(Path.Combine(Project, ".idp", "workflows"), "*.json").Single();
        var approval = RunRacerProcess.Run(Git, "approve-crash", Project, workflowFile, Fakes.Folder, Guid.NewGuid().ToString("D"), "Head",
            "approval.opened.after");
        Assert.True(approval.Exit == 73, approval.Output);
        var folder = Assert.Single(RunFolders());
        var run = new RunId(Guid.Parse(Path.GetFileName(folder)));
        var crash = RunRacerProcess.Run(Git, "turn-crash", Project, Path.GetFileName(Path.GetDirectoryName(folder))!, run.Value.ToString("D"),
            task.Value.ToString("D"), RunOperations.Initial(run, task).Value.ToString("D"), Fakes.Folder, Evidence, "1", point);
        Assert.True(crash.Exit == 73 && crash.Output.Contains(point), crash.Output);
        Assert.Equal(1, Launches(title));
    }

    /// <summary>A turn that reports <paramref name="session"/>, waits for the gate when it names one, and answers <paramref name="text"/>.</summary>
    public FakeRule Says(string text, string session = "session-1", string? gate = null)
    {
        var rule = FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, session));
        return (gate is null ? rule : rule.WaitForFile(Gate(gate))).Print(FakeAgents.ReplyLines(ClientId.Codex, text));
    }

    public string Gate(string name)
    {
        var path = Path.Combine(Evidence, name);
        if (!_gates.Contains(path))
        {
            _gates.Add(path);
        }

        return path;
    }

    public void Open(string gate) => File.WriteAllText(Gate(gate), "go");

    /// <summary>The turns the task titled <paramref name="title"/> answers, in launch order.</summary>
    public WorkflowRunFixture Answer(string title, params FakeRule[] turns)
    {
        _turns[title] = turns;
        return this;
    }

    /// <summary>A later turn whose prompt starts with <paramref name="prompt"/> goes to the script of the task titled <paramref name="title"/>.</summary>
    public WorkflowRunFixture Route(string prompt, string title)
    {
        _routes.Add((prompt, title));
        return this;
    }

    public string Folder(string title) => Path.Combine(Evidence, title);

    public int Launches(string title) => File.Exists(Path.Combine(Folder(title), "count")) ? int.Parse(File.ReadAllText(Path.Combine(Folder(title), "count"))) : 0;

    public string Prompt(string title, int launch) => File.ReadAllText(Path.Combine(Folder(title), $"{launch}.stdin"));

    /// <summary>The run folders of the project's workflows.</summary>
    public string[] RunFolders() => Directory.Exists(Path.Combine(Project, ".idp", "runs"))
        ? [.. Directory.EnumerateDirectories(Path.Combine(Project, ".idp", "runs")).SelectMany(Directory.EnumerateDirectories)]
        : [];

    /// <summary>Installs the fake Codex with every answer so far and returns the directory that finds it.</summary>
    public ClientDirectory Clients()
    {
        if (_clients is not null)
        {
            return _clients;
        }

        foreach (var (title, turns) in _turns)
        {
            var folder = Directory.CreateDirectory(Folder(title)).FullName;
            for (var turn = 0; turn < turns.Length; turn++)
            {
                File.WriteAllText(Path.Combine(folder, $"{turn + 1}.json"), turns[turn].StepsJson());
            }
        }

        var options = _turns.Keys.Select(title => ($"# {title}\n", title)).Concat(_routes)
            .Select(route => (route.Item1, FakeRule.On().Scripted(Folder(route.Item2))));
        FakeAgents.Install(Fakes, ClientId.Codex, FakeRule.On("app-server").Choose([.. options]));
        return _clients = Fakes.DiscoverAsync().Result;
    }

    /// <summary>A window that shows the project, with the runner pointed at the scratch Git.</summary>
    public Shell Window()
    {
        var shell = Shell.Open(Project, Clients(), Configure);
        _windows.Add(shell);
        return shell;
    }

    public void Configure(ProjectRuns runs)
    {
        runs.GitEnvironment = Git;
        runs.Probe = point => Probe?.Invoke(point);
        runs.Volumes = Volumes;
        runs.ShutdownTime = TimeSpan.FromMilliseconds(250);
        runs.LeaveTimeout = TimeSpan.FromSeconds(5);
        runs.CoordinatorRetry = TimeSpan.FromMilliseconds(50);
    }

    /// <summary>HEAD's commit, cut to seven characters.</summary>
    public string Head() => Run("rev-parse", "--short=7", "HEAD").Trim();

    public string Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Project, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in Git)
        {
            start.Environment[name] = value;
        }

        using var git = Process.Start(start)!;
        var errors = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {errors.Result}");
        return output;
    }

    // A fake client that a failed test left waiting at a gate ends here. Each window's runs stop following their
    // coordinators, so no timer of a window that only read a run asks for control after the test.
    public void Dispose()
    {
        foreach (var canvas in _windows.SelectMany(shell => shell.Window.ViewModel.Projects).SelectMany(project => project.Workflows))
        {
            canvas.DisposeRun();
        }

        foreach (var gate in _gates)
        {
            File.WriteAllText(gate, "");
        }

        Fakes.Dispose();
        _temp.Dispose();
    }
}
