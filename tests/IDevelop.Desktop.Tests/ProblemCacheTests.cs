using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A node keeps why it cannot start and asks again only after a change that can alter the answer, through the real main
/// window and the fake agent.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class ProblemCacheTests : IDisposable
{
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };
    private static readonly ConnectionKey BuildToCheck = new(TestTasks.Build, TestTasks.Review);

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public ProblemCacheTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    private static TaskNodeViewModel Node(Shell shell, string title) => (TaskNodeViewModel)shell.Node(title).DataContext!;

    [AvaloniaFact]
    public void A_card_needs_setup_once_its_agent_is_cleared_and_no_longer_once_one_is_chosen()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, Codex, "Draft it.")), _fakes.DiscoverAsync().Result);
        var node = Node(shell, "Design");
        Assert.Equal((NodeState.Idle, null), (node.State, node.Problem));
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Pick("TaskClient", "None");

        Assert.Equal((NodeState.NeedsSetup, new StartProblem.NoAgent()), (node.State, node.Problem));

        shell.Pick("TaskClient", "Codex");

        Assert.Equal((NodeState.Idle, null), (node.State, node.Problem));
    }

    [AvaloniaFact]
    public void A_review_needs_setup_while_nothing_it_depends_on_gives_it_a_subject()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(
            _temp.Seed(
                TaskAt(TestTasks.Build, "Build", 105, 90, Codex, "Write it."),
                new PlaceNode(TestTasks.Review, BuiltInBlueprints.Review, new CanvasPoint(465, 90))
                {
                    Title = "Check",
                    Settings = new NodeSettings(Codex, ConversationMode.Autonomous),
                },
                new Connect(BuildToCheck, ConnectionKind.Dependency)),
            _fakes.DiscoverAsync().Result);
        var canvas = shell.Window.ViewModel.Canvas!;
        var review = Node(shell, "Check");
        Assert.Equal((NodeState.Idle, new StartProblem.SubjectNotDone("Build")), (review.State, review.Problem));

        canvas.Edit(new Delete([], [BuildToCheck]));
        shell.Render();

        Assert.Equal((NodeState.NeedsSetup, new StartProblem.NoSubject()), (review.State, review.Problem));

        canvas.Edit(new Connect(BuildToCheck, ConnectionKind.Dependency));
        shell.Render();

        Assert.Equal((NodeState.Idle, new StartProblem.SubjectNotDone("Build")), (review.State, review.Problem));
    }

    [AvaloniaFact]
    public void A_card_needs_setup_while_its_client_is_not_ready()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var clients = _fakes.DiscoverAsync().Result;
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, Codex, "Draft it.")), clients);
        var node = Node(shell, "Design");
        Assert.Equal(NodeState.Idle, node.State);

        _fakes.Install("codex", FakeAgents.CodexModels, On("login", "status").Print("Not logged in").Exit(1));
        clients.RefreshAsync().Wait();
        shell.Render();

        Assert.Equal(
            (NodeState.NeedsSetup, new StartProblem.ClientUnready(ClientId.Codex, "Codex is not signed in. Run codex login in a terminal.")),
            (node.State, node.Problem));

        FakeAgents.Install(_fakes, ClientId.Codex);
        clients.RefreshAsync().Wait();
        shell.Render();

        Assert.Equal((NodeState.Idle, null), (node.State, node.Problem));
    }

    [AvaloniaFact]
    public void A_run_asks_again_for_its_own_task_and_the_tasks_it_depends_on_or_holds_but_not_for_others()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(
            _temp.Seed(
                TaskAt(TestTasks.Design, "Design", 105, 90, Codex, "Draft it."),
                TaskAt(TestTasks.Build, "Build", 465, 90, Codex, "Write it."),
                TaskAt(TestTasks.Review, "Elsewhere", 105, 450, Codex, "Something else."),
                new Connect(new ConnectionKey(TestTasks.Design, TestTasks.Build), ConnectionKind.Dependency)),
            _fakes.DiscoverAsync().Result);
        TaskNodeViewModel[] nodes = [Node(shell, "Design"), Node(shell, "Build"), Node(shell, "Elsewhere")];
        var before = nodes.Select(node => node.ProblemChecks).ToArray();
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Design", "CardStatus") == "Succeeded", "the run succeeds");

        Assert.Equal(NodeState.Succeeded, nodes[0].State);
        Assert.Equal([true, true, false], nodes.Select((node, i) => node.ProblemChecks > before[i]));
    }
}
