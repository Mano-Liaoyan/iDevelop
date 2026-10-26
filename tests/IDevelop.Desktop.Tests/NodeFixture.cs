using System.Collections.Immutable;
using System.Diagnostics;
using IDevelop.Desktop.Canvas;
using IDevelop.Clients;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A project on scratch Git whose workflow a window's engine runs with one fake Antigravity CLI. Antigravity reads its
/// whole prompt from stdin, so one fake answers each node by the title that starts its prompt, and a stop ends its turn
/// at once.
/// </summary>
internal sealed class NodeFixture : IDisposable
{
    public static readonly ExecutionSettings Agy = new(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" };

    // A folder of its own, apart from the App's, which the test class's own temporary folder may share.
    private readonly TempFolder _temp = new();
    private readonly List<Shell> _windows = [];
    private ClientDirectory? _clients;

    public NodeFixture(params WorkflowEdit[] edits)
    {
        Project = _temp.Seed(edits);
        Evidence = _temp.Create("evidence");
        Fakes = new FakeClients(_temp.Create("bin"));
        var config = Path.Combine(_temp.Create("git"), "config");
        File.WriteAllText(config, "[maintenance]\n\tauto = false\n");
        Git = new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_AUTHOR_NAME"] = "E3", ["GIT_AUTHOR_EMAIL"] = "e3@example.test", ["GIT_COMMITTER_NAME"] = "E3", ["GIT_COMMITTER_EMAIL"] = "e3@example.test",
            ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C", ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        Run("init", "-q", "--object-format=sha1", "-b", "main");
        Run("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(Project, ".gitignore"), ".idp/\n.worktrees/\n");
        File.WriteAllText(Path.Combine(Project, "base.txt"), "base\n");
        Run("add", "base.txt", ".gitignore");
        Run("commit", "-q", "-m", "base");
    }

    public string Project { get; }

    public string Evidence { get; }

    public FakeClients Fakes { get; }

    private IReadOnlyDictionary<string, string> Git { get; }

    /// <summary>An Implement node of Antigravity whose prompt starts with its title.</summary>
    public static WorkflowEdit.PlaceNode Task(TaskId id, string title, double x, ConversationMode conversation = ConversationMode.Autonomous) =>
        AppTempFolder.TaskAt(id, title, x, 90, Agy, $"Build {title}.", conversation);

    /// <summary>A Plan node of Antigravity, which only reads the project.</summary>
    public static WorkflowEdit.PlaceNode Plan(TaskId id, string title, double x, ConversationMode conversation = ConversationMode.Autonomous) =>
        new(id, BuiltInBlueprints.Plan, new CanvasPoint(x, 90))
        {
            Title = title, Fields = new Dictionary<string, string> { ["goal"] = $"Plan {title}." }.ToImmutableDictionary(),
            Settings = new NodeSettings(Agy, conversation),
        };

    public static WorkflowEdit.PlaceNode Approval(TaskId id, string title, double x) =>
        new(id, BuiltInBlueprints.Approval, new CanvasPoint(x, 90)) { Title = title };

    /// <summary>A Review node whose reviewer is Antigravity.</summary>
    public static WorkflowEdit.PlaceNode Review(TaskId id, string title, double x) =>
        new(id, BuiltInBlueprints.Review, new CanvasPoint(x, 90)) { Title = title, Settings = new NodeSettings(Agy, ConversationMode.Autonomous) };

    public static WorkflowEdit.Connect Dependency(TaskId from, TaskId to) => new(new ConnectionKey(from, to), ConnectionKind.Dependency);

    /// <summary>The steps of a turn that reports <paramref name="session"/> and replies <paramref name="reply"/>, after <paramref name="first"/>.</summary>
    public static FakeRule Reply(string session, string reply, FakeRule? first = null) =>
        (first ?? On()).Print(FakeAgents.SessionLine(ClientId.Antigravity, session)).Print(FakeAgents.ReplyLines(ClientId.Antigravity, reply));

    /// <summary>The start of the stdin line of a fresh turn of the node titled <paramref name="title"/>.</summary>
    public static string Fresh(string title) => """{"event":"user","message":{"role":"user","content":"# """ + title + "\\n";

    /// <summary>A file a turn waits for, which <see cref="Release"/> writes.</summary>
    public string Gate(string name) => Path.Combine(Evidence, $"gate-{name}");

    /// <summary>Lets the turns that wait for the gate go on.</summary>
    public void Release(string name) => File.WriteAllText(Gate(name), "");

    /// <summary>Installs the fake agy with these turn rules, most specific first.</summary>
    public void Install(params FakeRule[] turns) => FakeAgents.Install(Fakes, ClientId.Antigravity, turns);

    private ClientDirectory Clients() => _clients ??= Fakes.DiscoverAsync().Result;

    /// <summary>Opens the project in a new window whose engines run Git with the fixture's configuration.</summary>
    public Shell Open()
    {
        foreach (var earlier in _windows)
        {
            var leaving = earlier.Window.ViewModel.Leave();
            earlier.WaitUntil(() => leaving.IsCompleted, "the earlier window lets the project go");
        }

        var shell = Shell.Show(Clients());
        shell.Window.ViewModel.GitEnvironment = Git;
        shell.Window.ViewModel.Open(Project);
        shell.Render();
        _windows.Add(shell);
        return shell;
    }

    /// <summary>Opens the project in a new window, as <see cref="Open"/> does.</summary>
    public Shell Window() => Open();

    private void Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Project, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in Git) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }

    public void Dispose()
    {
        // Each window's engines stop their turns while the UI thread runs, since a stop may finish there. A window stays
        // open: closing one with unsaved edits would ask about them and wait for an answer.
        foreach (var shell in _windows)
        {
            var leaving = shell.Window.ViewModel.Leave();
            shell.WaitUntil(() => leaving.IsCompleted, "the window's nodes stop");
        }

        Fakes.Dispose();
        _temp.Dispose();
    }
}

internal sealed partial class Shell
{
    public TaskNodeViewModel NodeModel(string title) => Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == title);

    /// <summary>Runs the node, as its Run does.</summary>
    public void RunNode(string title)
    {
        var node = NodeModel(title);
        Assert.True(node.RunCommand.CanExecute(null), $"\"{title}\" cannot run: {node.RunRefusal}");
        node.RunCommand.Execute(null);
        Render();
    }

    /// <summary>Runs the inspected node, as its Run does.</summary>
    public void RunNode() => RunNode(Window.ViewModel.Canvas!.SelectedNode!.Title);

    public string Text(string automationId) => Find<Avalonia.Controls.TextBlock>(automationId).Text ?? "";

    public void WaitForCard(string title, string status) =>
        WaitUntil(() => CardText(title, "CardStatus") == status, $"\"{title}\" shows {status}", () => $"It shows {CardText(title, "CardStatus")}.");

    /// <summary>Runs the UI thread's jobs until the condition holds, and says what it saw when it never does.</summary>
    public void WaitUntil(Func<bool> condition, string what, Func<string> seen)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}. Saw: {seen()}");
            Thread.Sleep(20);
            Render();
        }
    }

    /// <summary>Waits until the node's card shows the state.</summary>
    public void WaitForState(string title, NodeState state) =>
        WaitUntil(() => NodeModel(title).State == state, $"\"{title}\" is {state} (it is {NodeModel(title).State}: {NodeModel(title).StatusLabel})");
}
