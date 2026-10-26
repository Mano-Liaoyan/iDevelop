using System.Text.Json;
using System.Text.Json.Nodes;
using IDevelop.Core.Tests.Git;
using IDevelop.Engine;
using IDevelop.Clients;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// A project in a Git repository with one commit, a fake Antigravity CLI, and a workflow of Implement nodes. Antigravity
/// reads its whole prompt from stdin and has a read-only mode, so one fake answers each node by the start of its prompt,
/// and a stop ends its turn at once.
/// </summary>
internal sealed class EngineFixture : IAsyncDisposable
{
    public static readonly ExecutionSettings Agy = new(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" };

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly TempFolder _temp = new();
    private readonly List<WorkflowEngine> _engines = [];
    private ClientDirectory? _clients;
    private int _placed;

    public EngineFixture()
    {
        Fakes = new FakeClients(_temp.Create("bin"));
        Project = _temp.Create("project");
        Evidence = _temp.Create("evidence");
        Repository = new GitFixture(folder: Project);
        Repository.Write("README.md", "Project\n");
        Start = Repository.Commit("Start").Hex;
    }

    public GitFixture Repository { get; }

    /// <summary>The folder the engine opens as the project, when it is not the repository's top.</summary>
    public string? ProjectFolder { get; set; }

    /// <summary>The project branch's first commit.</summary>
    public string Start { get; }

    public FakeClients Fakes { get; }

    public string Project { get; }

    public string Evidence { get; }

    public Workflow Workflow { get; set; } = Workflow.Empty(WorkflowId.New());

    public string File(string name) => Path.Combine(Evidence, name);

    /// <summary>Installs the fake agy with these turn rules, most specific first.</summary>
    public void Install(params FakeRule[] turns) => FakeAgents.Install(Fakes, ClientId.Antigravity, turns);

    public TaskId Node(string title, ConversationMode mode = ConversationMode.Autonomous, ExecutionSettings? execution = null)
    {
        var node = TestNodes.Implement(NextId(), title, $"Do {title}.", execution: execution ?? Agy, conversation: mode);
        Workflow = Workflow.Must(TestNodes.Place(node, new CanvasPoint(0, 0)));
        return node.Id;
    }

    /// <summary>A Plan node, which only reads the project.</summary>
    public TaskId Reader(string title)
    {
        var node = new TaskDefinition(NextId(), BuiltInBlueprints.Plan) { Title = title, Execution = Agy, Conversation = ConversationMode.Autonomous }
            .WithField("goal", $"Plan {title}.")!;
        Workflow = Workflow.Must(TestNodes.Place(node, new CanvasPoint(0, 0)));
        return node.Id;
    }

    public TaskId Review(string title)
    {
        var node = new TaskDefinition(NextId(), BuiltInBlueprints.Review) { Title = title, Execution = Agy };
        Workflow = Workflow.Must(TestNodes.Place(node, new CanvasPoint(0, 0)));
        return node.Id;
    }

    /// <summary>Steps that run <paramref name="turns"/> one per call, in order, so the fresh turns of one node can differ.</summary>
    public FakeRule Turns(string name, params FakeRule[] turns)
    {
        var folder = Directory.CreateDirectory(File(name)).FullName;
        foreach (var (index, turn) in turns.Index())
        {
            System.IO.File.WriteAllText(Path.Combine(folder, $"{index + 1}.json"), new JsonArray([.. turn.Steps.Select(step => step.DeepClone())]).ToJsonString());
        }

        return On().Scripted(folder);
    }

    public TaskId Merge(string title)
    {
        var node = new TaskDefinition(NextId(), BuiltInBlueprints.Merge) { Title = title, Execution = Agy };
        Workflow = Workflow.Must(TestNodes.Place(node, new CanvasPoint(0, 0)));
        return node.Id;
    }

    /// <summary>The folder of <paramref name="node"/>'s worktree.</summary>
    public string Worktree(TaskId node) => Path.Combine(Project, ".worktrees", Workflow.Id.ToString(), node.ToString());

    /// <summary>The files <paramref name="commit"/> holds, with their contents.</summary>
    public IReadOnlyDictionary<string, string> Files(string commit) => Repository.Git("ls-tree", "-r", "--name-only", commit)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToDictionary(path => path, path => Repository.Git("show", $"{commit}:{path}"));

    public string[] Parents(string commit) => Repository.Git("rev-list", "--parents", "-n", "1", commit).Trim().Split(' ')[1..];

    public string Head => Repository.Git("rev-parse", "HEAD").Trim();

    public TaskId Approval(string title)
    {
        var id = NextId();
        Workflow = Workflow.Must(new WorkflowEdit.PlaceNode(id, BuiltInBlueprints.Approval, new CanvasPoint(0, 0)) { Title = title });
        return id;
    }

    // Ids in the order the nodes were placed, so a node's predecessors come in that order too. Two version 7 ids from one
    // millisecond need not.
    private TaskId NextId() => new(Guid.Parse($"019a0000-0000-7000-8000-{++_placed:D12}"));

    public void Connect(TaskId from, TaskId to) => Workflow = Workflow.Must(new WorkflowEdit.Connect(new ConnectionKey(from, to), ConnectionKind.Dependency));

    public async Task<WorkflowEngine> Open()
    {
        _clients ??= await Fakes.DiscoverAsync();
        var engine = new WorkflowEngine(ProjectFolder ?? Project, Workflow, _clients, TimeSpan.FromSeconds(1), Repository.Environment);
        _engines.Add(engine);
        return engine;
    }

    /// <summary>The steps of a turn that reports <paramref name="session"/> and replies <paramref name="reply"/>, after <paramref name="first"/>.</summary>
    public static FakeRule Reply(string session, string reply, FakeRule? first = null) =>
        (first ?? On()).Print(FakeAgents.SessionLine(ClientId.Antigravity, session)).Print(FakeAgents.ReplyLines(ClientId.Antigravity, reply));

    /// <summary>The start of the stdin line of a fresh turn of the node titled <paramref name="title"/>.</summary>
    public static string Fresh(string title) => """{"event":"user","message":{"role":"user","content":"# """ + title + "\\n";

    /// <summary>The prompt a turn captured, which a turn that chose its steps by the prompt captured as agy's stdin line.</summary>
    public string Prompt(string name)
    {
        var text = System.IO.File.ReadAllText(File(name));
        return text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.GetProperty("message").GetProperty("content").GetString()! : text;
    }

    public string[] Arguments(string name) => JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(File(name)))!;

    /// <summary>Waits until <paramref name="node"/>'s state matches.</summary>
    public static async Task<NodeState> Until(WorkflowEngine engine, TaskId node, Func<NodeState, bool> condition)
    {
        var reached = new TaskCompletionSource<NodeState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(TaskId changed, NodeState state)
        {
            if (changed == node && condition(state)) reached.TrySetResult(state);
        }

        engine.NodeChanged += Changed;
        try
        {
            var now = engine.State(node);
            if (condition(now)) return now;
            return await reached.Task.WaitAsync(Patience);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The node stayed {engine.State(node)}.");
        }
        finally
        {
            engine.NodeChanged -= Changed;
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, for what no state change announces.</summary>
    public static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    public static Task<NodeState> Until<T>(WorkflowEngine engine, TaskId node) where T : NodeState => Until(engine, node, state => state is T);

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines) await engine.DisposeAsync();
        Fakes.Dispose();
        Repository.Dispose();
        _temp.Dispose();
    }
}
