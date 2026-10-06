using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

[Collection(ProcessCollection.Name)]
public sealed class AgentPickerTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;
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

    private static string[] Pickers(Shell shell) => [.. new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked)];

    [AvaloniaFact]
    public void The_pickers_offer_what_the_clients_offer_and_the_choice_survives_save_and_reopen()
    {
        var folder = _temp.Seed(TaskAt(Design, "Design", 105, 90));
        var shell = Shell.Open(folder, _clients);
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal("No agent", shell.CardText("Design", "CardAgent"));
        Assert.Equal("None", shell.Picked("TaskClient"));
        Assert.False(shell.Find<ComboBox>("TaskModel").IsEffectivelyVisible);

        Assert.Equal(
            ["None", "Claude Code · not installed", "Codex", "Pi · not installed", "Antigravity CLI"],
            shell.Pick("TaskClient", "Codex"));
        Assert.Equal(["GPT-6.1-Sol", "GPT-6-Sol", "GPT-5.5"], shell.Pick("TaskModel", "GPT-5.5"));
        Assert.Equal(["low", "medium", "high", "xhigh"], shell.Pick("TaskReasoning", "high"));

        Assert.Equal("Codex · GPT-5.5 · high", shell.CardText("Design", "CardAgent"));
        Assert.Equal(
            ["Codex", "GPT-5.5", "high"],
            new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(id =>
                ControlAutomationPeer.CreatePeerForElement(shell.Find<ComboBox>(id)).GetProvider<IValueProvider>()!.Value));
        Assert.Equal("Edits files. Runs commands in a sandbox.", shell.Note("PermissionNote").Name);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.Equal(
            new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" },
            WorkflowDocument.OpenProject(folder).Single().Current.Tasks[Design].Execution);

        var reopened = Shell.Open(folder, _clients);
        reopened.Click(reopened.Header(reopened.Node("Design")));
        Assert.Equal("Codex · GPT-5.5 · high", reopened.CardText("Design", "CardAgent"));
        Assert.Equal(["Codex", "GPT-5.5", "high"], Pickers(reopened));
    }

    [AvaloniaFact]
    public void A_model_without_reasoning_levels_hides_the_reasoning_picker()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)), _clients);
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Pick("TaskClient", "Antigravity CLI");

        Assert.Equal("Antigravity CLI · Gemini 3.8 Flash · medium", shell.CardText("Design", "CardAgent"));
        Assert.Equal(
            ["Gemini 3.8 Flash", "Gemini 3.7 Flash", "Gemini 3.6 Flash", "Gemini 3.1 Pro", "Claude Sonnet 4.6 (Thinking)", "Claude Opus 4.6 (Thinking)", "GPT-OSS 120B"],
            shell.Pick("TaskModel", "Claude Opus 4.6 (Thinking)"));
        Assert.Equal("Antigravity CLI · Claude Opus 4.6 (Thinking)", shell.CardText("Design", "CardAgent"));
        Assert.False(shell.Find<ComboBox>("TaskReasoning").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_model_this_machine_does_not_offer_stays_chosen_and_marked_until_another_is_chosen()
    {
        var folder = _temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-7", Reasoning = "ultra" }));
        var shell = Shell.Open(folder, _clients);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("Codex · gpt-7 · ultra", shell.CardText("Design", "CardAgent"));
        Assert.Equal(["gpt-7 (not offered on this machine)", "ultra"], new[] { "TaskModel", "TaskReasoning" }.Select(shell.Picked));
        Assert.Equal("seed - iDevelop", shell.Window.Title);

        Assert.Equal(
            ["GPT-6.1-Sol", "GPT-6-Sol", "GPT-5.5", "gpt-7 (not offered on this machine)"],
            shell.Pick("TaskModel", "GPT-5.5"));

        Assert.Equal("Codex · GPT-5.5 · medium", shell.CardText("Design", "CardAgent"));
        Assert.Equal(["low", "medium", "high", "xhigh"], shell.Pick("TaskReasoning", "medium"));
        Assert.Equal(["GPT-6.1-Sol", "GPT-6-Sol", "GPT-5.5"], shell.Pick("TaskModel", "GPT-5.5"));
        shell.Press(Key.S, RawInputModifiers.Control);
        Assert.Equal(
            new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "medium" },
            WorkflowDocument.OpenProject(folder).Single().Current.Tasks[Design].Execution);
    }

    [AvaloniaFact]
    public void A_level_the_model_does_not_offer_stays_chosen_and_marked_until_another_is_chosen()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "ultra" })), _clients);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("ultra (not offered)", shell.Picked("TaskReasoning"));
        Assert.Equal(["low", "medium", "high", "xhigh", "ultra (not offered)"], shell.Pick("TaskReasoning", "high"));

        Assert.Equal("Codex · GPT-5.5 · high", shell.CardText("Design", "CardAgent"));
        Assert.Equal(["low", "medium", "high", "xhigh"], shell.Pick("TaskReasoning", "high"));
    }

    [AvaloniaFact]
    public void While_its_client_is_being_checked_a_task_shows_its_model_plainly()
    {
        var checking = new ClientDirectory(CommandResolver.Create([], []));
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" })), checking);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal(["Codex · checking", "gpt-5.5", "high"], Pickers(shell));
        Assert.Equal("Codex · gpt-5.5 · high", shell.CardText("Design", "CardAgent"));
        Assert.Equal("iDevelop is still checking Codex.", shell.InView<TextBlock>("StartProblem").Text);
    }

    [AvaloniaFact]
    public void Switching_the_inspector_between_tasks_leaves_each_tasks_agent_as_it_was()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "xhigh" }),
            TaskAt(Build, "Build", 405, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "low" }),
            TaskAt(Review, "Review", 105, 330, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "high" })), _clients);

        foreach (var title in new[] { "Design", "Build", "Review", "Design", "Review", "Build" })
        {
            shell.Click(shell.Header(shell.Node(title)));
        }

        Assert.Equal(["Codex", "GPT-6-Sol", "low"], Pickers(shell));
        Assert.Equal(
            ["Codex · GPT-5.5 · xhigh", "Codex · GPT-6-Sol · low", "Antigravity CLI · Gemini 3.8 Flash · high"],
            new[] { "Design", "Build", "Review" }.Select(title => shell.CardText(title, "CardAgent")));
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Choosing_tasks_of_different_clients_in_the_sidebar_leaves_each_tasks_agent_as_it_was()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" }),
            TaskAt(Build, "Build", 405, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "xhigh" }),
            TaskAt(Review, "Review", 705, 90, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" })), _clients);

        foreach (var title in new[] { "Design", "Review", "Build", "Design", "Review" })
        {
            shell.Click(shell.SidebarRow(title));
        }

        Assert.Equal(["Antigravity CLI", "Gemini 3.8 Flash", "low"], Pickers(shell));
        Assert.Equal(
            ["Codex · GPT-5.5 · low", "Codex · GPT-6-Sol · xhigh", "Antigravity CLI · Gemini 3.8 Flash · low"],
            new[] { "Design", "Build", "Review" }.Select(title => shell.CardText(title, "CardAgent")));
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    // Pi's high is also a level of Codex's GPT-5.5, at another place in its list. A picker keeps the keyboard focus after
    // the user chooses in it.
    [AvaloniaFact]
    public void Choosing_each_task_in_the_sidebar_and_on_the_canvas_changes_no_tasks_agent()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90, PiAtHigh),
            TaskAt(Build, "Build", 405, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" }),
            TaskAt(Review, "Review", 105, 330, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "medium" })), WithPi());
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
                ? shell.Center(shell.SidebarRow(title))
                : shell.Header(shell.Node(title)));

            Assert.Equal(pickers[title], Pickers(shell));
            Assert.Equal(
                ["Pi · DeepSeek V4 Pro (deepseek) · high", "Codex · GPT-5.5 · low", "Antigravity CLI · Gemini 3.8 Flash · medium"],
                new[] { "Design", "Build", "Review" }.Select(task => shell.CardText(task, "CardAgent")));
            Assert.True("seed - iDevelop" == shell.Window.Title, $"Choosing {title} in the {where} made the title '{shell.Window.Title}'.");
        }
    }

    // A screen reader's select command selects a row through UI Automation, which leaves the keyboard focus on the picker
    // the user last chose in.
    [AvaloniaFact]
    public void Selecting_a_task_while_a_picker_keeps_the_focus_changes_no_tasks_agent()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90, PiAtHigh),
            TaskAt(Build, "Build", 405, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "low" })), WithPi());
        shell.Click(shell.Header(shell.Node("Design")));
        var reasoning = shell.Find<ComboBox>("TaskReasoning");
        reasoning.Focus();
        var build = shell.SidebarRow("Build");

        ControlAutomationPeer.CreatePeerForElement(build).GetProvider<ISelectionItemProvider>()!.Select();
        shell.Render();

        Assert.True(reasoning.IsKeyboardFocusWithin);
        Assert.Equal(["Codex", "GPT-5.5", "low"], Pickers(shell));
        Assert.Equal(
            ["Pi · DeepSeek V4 Pro (deepseek) · high", "Codex · GPT-5.5 · low"],
            new[] { "Design", "Build" }.Select(task => shell.CardText(task, "CardAgent")));
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    // The reasoning picker still holds Pi's high when Codex's levels replace Pi's, and Codex's first model offers high.
    [AvaloniaFact]
    public void Choosing_another_client_takes_its_first_model_at_that_models_default_level()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, PiAtHigh)), WithPi());
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Pick("TaskClient", "Codex");

        Assert.Equal(["Codex", "GPT-6.1-Sol", "low"], Pickers(shell));
        Assert.Equal("Codex · GPT-6.1-Sol · low", shell.CardText("Design", "CardAgent"));
    }
}
