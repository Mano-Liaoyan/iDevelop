using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

[Collection(ProcessCollection.Name)]
public sealed class AgentPickerTests : IDisposable
{
    private static readonly TaskId Design = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly TaskId Build = new(Guid.Parse("019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22"));
    private static readonly TaskId Review = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));
    private static readonly ExecutionSettings PiAtHigh = new(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly ClientDirectory _clients;

    // Codex and Antigravity CLI are installed and ready. Claude Code and Pi are not installed.
    public AgentPickerTests()
    {
        var fakes = new FakeClients(_temp.Create("bin"));
        FakeAgents.Install(fakes, ClientId.Codex);
        FakeAgents.Install(fakes, ClientId.Antigravity);
        _clients = fakes.DiscoverAsync().Result;
    }

    public void Dispose() => _temp.Dispose();

    // Codex, Pi, and Antigravity CLI are installed and ready.
    private ClientDirectory WithPi()
    {
        var fakes = new FakeClients(_temp.Create("bin-with-pi"));
        foreach (var client in new[] { ClientId.Codex, ClientId.Pi, ClientId.Antigravity })
        {
            FakeAgents.Install(fakes, client);
        }

        return fakes.DiscoverAsync().Result;
    }

    private static WorkflowEdit.CreateTask Task(TaskId id, string title, double x, ExecutionSettings? execution = null, double y = 90) =>
        new(new TaskDefinition(id) { Title = title, Execution = execution }, new CanvasPoint(x, y));

    private static string CardAgent(Shell shell, string title) =>
        shell.Node(title).GetVisualDescendants().OfType<TextBlock>().Single(text => Avalonia.Automation.AutomationProperties.GetAutomationId(text) == "CardAgent").Text!;

    [AvaloniaFact]
    public void The_pickers_offer_what_the_clients_offer_and_the_choice_survives_save_and_reopen()
    {
        var folder = _temp.Seed(Task(Design, "Design", 105));
        var shell = Shell.Open(folder, _clients);
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal("No agent", CardAgent(shell, "Design"));
        Assert.Equal("None", shell.Picked("TaskClient"));
        Assert.False(shell.Find<ComboBox>("TaskModel").IsEffectivelyVisible);

        Assert.Equal(
            ["None", "Claude Code · not installed", "Codex", "Pi · not installed", "Antigravity CLI"],
            shell.Pick("TaskClient", "Codex"));
        Assert.Equal(["GPT-6.1-Sol", "GPT-6-Sol", "GPT-5.5"], shell.Pick("TaskModel", "GPT-5.5"));
        Assert.Equal(["low", "medium", "high", "xhigh"], shell.Pick("TaskReasoning", "high"));

        Assert.Equal("Codex · GPT-5.5 · high", CardAgent(shell, "Design"));
        Assert.Equal(
            ["Codex", "GPT-5.5", "high"],
            new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(id =>
                ControlAutomationPeer.CreatePeerForElement(shell.Find<ComboBox>(id)).GetProvider<IValueProvider>()!.Value));
        Assert.Equal("Codex may edit files in the project folder. Its commands run in its workspace sandbox.", shell.Find<TextBlock>("PermissionNote").Text);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.Equal(
            new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" },
            WorkflowDocument.Open(folder).Current.Tasks[Design].Execution);

        var reopened = Shell.Open(folder, _clients);
        reopened.Click(reopened.Header(reopened.Node("Design")));
        Assert.Equal("Codex · GPT-5.5 · high", CardAgent(reopened, "Design"));
        Assert.Equal(["Codex", "GPT-5.5", "high"], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(reopened.Picked));
    }

    [AvaloniaFact]
    public void A_model_without_reasoning_levels_hides_the_reasoning_picker()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105)), _clients);
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Pick("TaskClient", "Antigravity CLI");

        Assert.Equal("Antigravity CLI · Gemini 3.8 Flash · medium", CardAgent(shell, "Design"));
        Assert.Equal(
            ["Gemini 3.8 Flash", "Gemini 3.7 Flash", "Gemini 3.6 Flash", "Gemini 3.1 Pro", "Claude Sonnet 4.6 (Thinking)", "Claude Opus 4.6 (Thinking)", "GPT-OSS 120B"],
            shell.Pick("TaskModel", "Claude Opus 4.6 (Thinking)"));
        Assert.Equal("Antigravity CLI · Claude Opus 4.6 (Thinking)", CardAgent(shell, "Design"));
        Assert.False(shell.Find<ComboBox>("TaskReasoning").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_model_this_machine_does_not_offer_stays_chosen_and_marked_until_another_is_chosen()
    {
        var folder = _temp.Seed(Task(Design, "Design", 105, new ExecutionSettings(ClientId.Codex) { Model = "gpt-7", Reasoning = "ultra" }));
        var shell = Shell.Open(folder, _clients);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("Codex · gpt-7 · ultra", CardAgent(shell, "Design"));
        Assert.Equal(["gpt-7 (not offered on this machine)", "ultra"], new[] { "TaskModel", "TaskReasoning" }.Select(shell.Picked));
        Assert.Equal("seed - iDevelop", shell.Window.Title);

        Assert.Equal(
            ["GPT-6.1-Sol", "GPT-6-Sol", "GPT-5.5", "gpt-7 (not offered on this machine)"],
            shell.Pick("TaskModel", "GPT-5.5"));

        Assert.Equal("Codex · GPT-5.5 · medium", CardAgent(shell, "Design"));
        Assert.Equal(["low", "medium", "high", "xhigh"], shell.Pick("TaskReasoning", "medium"));
        Assert.Equal(["GPT-6.1-Sol", "GPT-6-Sol", "GPT-5.5"], shell.Pick("TaskModel", "GPT-5.5"));
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.Equal(
            new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "medium" },
            WorkflowDocument.Open(folder).Current.Tasks[Design].Execution);
    }

    [AvaloniaFact]
    public void While_its_client_is_being_checked_a_task_shows_its_model_plainly()
    {
        var checking = new ClientDirectory(CommandResolver.Create([], []));
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" })), checking);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal(["Codex · checking", "gpt-5.5", "high"], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked));
        Assert.Equal("Codex · gpt-5.5 · high", CardAgent(shell, "Design"));
        Assert.Equal("iDevelop is still checking Codex.", shell.InView<TextBlock>("StartProblem").Text);
    }

    [AvaloniaFact]
    public void Switching_the_inspector_between_tasks_leaves_each_tasks_agent_as_it_was()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "xhigh" }),
            Task(Build, "Build", 405, new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "low" }),
            Task(Review, "Review", 105, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "high" }, y: 330)), _clients);

        foreach (var title in new[] { "Design", "Build", "Review", "Design", "Review", "Build" })
        {
            shell.Click(shell.Header(shell.Node(title)));
        }

        Assert.Equal(["Codex", "GPT-6-Sol", "low"], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked));
        Assert.Equal(
            ["Codex · GPT-5.5 · xhigh", "Codex · GPT-6-Sol · low", "Antigravity CLI · Gemini 3.8 Flash · high"],
            new[] { "Design", "Build", "Review" }.Select(title => CardAgent(shell, title)));
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Choosing_tasks_of_different_clients_in_the_sidebar_leaves_each_tasks_agent_as_it_was()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" }),
            Task(Build, "Build", 405, new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "xhigh" }),
            Task(Review, "Review", 705, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" })), _clients);

        foreach (var title in new[] { "Design", "Review", "Build", "Design", "Review" })
        {
            shell.Click(shell.Find<ListBox>("SidebarTasks").GetVisualDescendants().OfType<ListBoxItem>()
                .Single(row => ((Canvas.TaskNodeViewModel)row.DataContext!).Title == title));
        }

        Assert.Equal(["Antigravity CLI", "Gemini 3.8 Flash", "low"], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked));
        Assert.Equal(
            ["Codex · GPT-5.5 · low", "Codex · GPT-6-Sol · xhigh", "Antigravity CLI · Gemini 3.8 Flash · low"],
            new[] { "Design", "Build", "Review" }.Select(title => CardAgent(shell, title)));
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    // Pi's high is also a level of Codex's GPT-5.5, at another place in its list. A picker keeps the keyboard focus after
    // the user chooses in it.
    [AvaloniaFact]
    public void Choosing_each_task_in_the_sidebar_and_on_the_canvas_changes_no_tasks_agent()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, PiAtHigh),
            Task(Build, "Build", 405, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" }),
            Task(Review, "Review", 105, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "medium" }, y: 330)), WithPi());
        var pickers = new Dictionary<string, string[]>
        {
            ["Design"] = ["Pi", "DeepSeek V4 Pro (deepseek)", "high"],
            ["Build"] = ["Codex", "GPT-5.5", "low"],
            ["Review"] = ["Antigravity CLI", "Gemini 3.8 Flash", "medium"],
        };
        shell.Click(shell.Header(shell.Node("Design")));

        string[] order = ["Build", "Review", "Design", "Review", "Build", "Design"];
        foreach (var (title, where) in order.Select(title => (title, "sidebar")).Concat(order.Select(title => (title, "canvas"))))
        {
            shell.Find<ComboBox>("TaskReasoning").Focus();
            shell.Click(where == "sidebar"
                ? shell.Center(shell.Find<ListBox>("SidebarTasks").GetVisualDescendants().OfType<ListBoxItem>().Single(row => ((Canvas.TaskNodeViewModel)row.DataContext!).Title == title))
                : shell.Header(shell.Node(title)));

            Assert.Equal(pickers[title], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked));
            Assert.Equal(
                ["Pi · DeepSeek V4 Pro (deepseek) · high", "Codex · GPT-5.5 · low", "Antigravity CLI · Gemini 3.8 Flash · medium"],
                new[] { "Design", "Build", "Review" }.Select(task => CardAgent(shell, task)));
            Assert.True("seed - iDevelop" == shell.Window.Title, $"Choosing {title} in the {where} made the title '{shell.Window.Title}'.");
        }
    }

    // A screen reader's select command selects a row through UI Automation, which leaves the keyboard focus on the picker
    // the user last chose in.
    [AvaloniaFact]
    public void Selecting_a_task_while_a_picker_keeps_the_focus_changes_no_tasks_agent()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, PiAtHigh),
            Task(Build, "Build", 405, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" })), WithPi());
        shell.Click(shell.Header(shell.Node("Design")));
        var reasoning = shell.Find<ComboBox>("TaskReasoning");
        reasoning.Focus();
        var build = shell.Find<ListBox>("SidebarTasks").GetVisualDescendants().OfType<ListBoxItem>()
            .Single(row => ((Canvas.TaskNodeViewModel)row.DataContext!).Title == "Build");

        ControlAutomationPeer.CreatePeerForElement(build).GetProvider<ISelectionItemProvider>()!.Select();
        shell.Render();

        Assert.True(reasoning.IsKeyboardFocusWithin);
        Assert.Equal(["Codex", "GPT-5.5", "low"], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked));
        Assert.Equal(
            ["Pi · DeepSeek V4 Pro (deepseek) · high", "Codex · GPT-5.5 · low"],
            new[] { "Design", "Build" }.Select(task => CardAgent(shell, task)));
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    // The reasoning picker still holds Pi's high when Codex's levels replace Pi's, and Codex's first model offers high.
    [AvaloniaFact]
    public void Choosing_another_client_takes_its_first_model_at_that_models_default_level()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, PiAtHigh)), WithPi());
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Pick("TaskClient", "Codex");

        Assert.Equal(["Codex", "GPT-6.1-Sol", "low"], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked));
        Assert.Equal("Codex · GPT-6.1-Sol · low", CardAgent(shell, "Design"));
    }
}
