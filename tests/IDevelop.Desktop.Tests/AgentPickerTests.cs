using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

[Collection(ProcessTests.Name)]
public sealed class AgentPickerTests : IDisposable
{
    private static readonly TaskId Design = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly TaskId Build = new(Guid.Parse("019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22"));
    private static readonly TaskId Review = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));

    private readonly TempFolder _temp = new();
    private readonly ClientDirectory _clients;

    // Codex and Antigravity CLI are installed and ready. Claude Code and Pi are not installed.
    public AgentPickerTests()
    {
        var fakes = new FakeClients(_temp.Create("bin"));
        FakeAgents.Install(fakes, ClientId.Codex);
        FakeAgents.Install(fakes, ClientId.Antigravity);
        _clients = new ClientDirectory(fakes.Resolver);
        _clients.RefreshAsync().Wait();
    }

    public void Dispose() => _temp.Dispose();

    private static WorkflowEdit.CreateTask Task(TaskId id, string title, double x, ExecutionSettings? execution = null) =>
        new(new TaskDefinition(id) { Title = title, Execution = execution }, new CanvasPoint(x, 90));

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
    public void Switching_the_inspector_between_tasks_leaves_each_tasks_agent_as_it_was()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "xhigh" }),
            Task(Build, "Build", 405, new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "low" }),
            Task(Review, "Review", 705, new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "high" })), _clients);

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
}
