using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>Runs go through the fake agent behind on-disk shims, resolved and launched like a real client.</summary>
[Collection(ProcessTests.Name)]
public sealed class RunTests : IDisposable
{
    private static readonly TaskId SayHi = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _gate;

    public RunTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _gate = Path.Combine(_temp.Create("evidence"), "go");
    }

    // A fake client that a failed test left waiting at the gate ends here, or dotnet test would wait for it. Leaving the
    // project instead would touch the window after the headless session has ended the test.
    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _temp.Dispose();
    }

    /// <summary>The client waits at the gate until it is stopped or the test opens the gate.</summary>
    private FakeRule Waits() => On("exec", "--json")
        .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
        .WaitForFile(_gate);

    private static WorkflowEdit.CreateTask Task(ExecutionSettings execution) => new(
        new TaskDefinition(SayHi) { Title = "Say hi", Instructions = "Create hello.txt containing hi. Then reply with DONE.", Execution = execution },
        new CanvasPoint(105, 90));

    private static Control Part(Shell shell, string automationId) =>
        shell.Node("Say hi").GetVisualDescendants().OfType<Control>().Single(control => AutomationProperties.GetAutomationId(control) == automationId);

    private static string CardStatus(Shell shell) => Shell.TextOf(Part(shell, "CardStatus"));

    private static Color CardFill(Shell shell) =>
        ((ISolidColorBrush)shell.Node("Say hi").GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("card")).Background!).Color;

    private ClientDirectory Discover()
    {
        var clients = new ClientDirectory(_fakes.Resolver);
        clients.RefreshAsync().Wait();
        return clients;
    }

    [AvaloniaFact]
    public void Running_a_task_whose_client_is_not_ready_shows_why_and_launches_nothing()
    {
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        var folder = _temp.Seed(Task(new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "high" }));
        var shell = Shell.Open(folder, Discover());
        shell.Click(shell.Header(shell.Node("Say hi")));
        const string reason = "Claude Code is not ready. Claude Code is not signed in. Run claude in a terminal and sign in.";
        Assert.Equal(reason, shell.InView<TextBlock>("StartProblem").Text);

        shell.Click(shell.InView<Button>("RunTask"));

        Assert.Equal(reason, shell.Status);
        Assert.Equal("Not run", CardStatus(shell));
        Assert.False(Directory.Exists(Path.Combine(folder, ".idp", "attempts")));
    }

    [AvaloniaFact]
    public void A_task_shows_that_it_runs_and_then_its_result_on_its_card_and_in_the_inspector()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits().Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(_temp.Seed(Task(Codex)), Discover());
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Say hi")));
        Assert.Equal(("Not run", Color.Parse("#FFFFFF")), (CardStatus(shell), CardFill(shell)));

        shell.Click(shell.InView<Button>("RunTask"));

        Assert.Equal(("Running", Color.Parse("#CDF4F3")), (CardStatus(shell), CardFill(shell)));
        Assert.Equal((false, true), (shell.Find<Button>("RunTask").IsEffectivelyEnabled, shell.Find<Button>("CancelRun").IsEffectivelyEnabled));
        Assert.Equal("", shell.Status);

        File.WriteAllText(_gate, "");
        shell.WaitUntil(() => CardStatus(shell) == "Succeeded", "the run succeeds");

        Assert.Equal(Color.Parse("#D9F4D9"), CardFill(shell));
        Assert.Equal(Color.Parse("#44984A"), ((ISolidColorBrush)Part(shell, "CardStatus").GetVisualDescendants().OfType<TextBlock>().Single().Foreground!).Color);
        Assert.Equal("Succeeded", Shell.TextOf(shell.InView<Border>("LastRunStatus")));
        Assert.Equal("DONE", shell.Find<TextBox>("LastRunResult").Text);
        Assert.Equal("Requested Codex · gpt-5.5 · high.", shell.Find<TextBlock>("LastRunConfiguration").Text);
        Assert.Equal(
            ["command: pwsh.exe -Command \"Set-Content -LiteralPath .\\\\hello.txt -Value 'hi' -NoNewline\"", "DONE"],
            shell.Find<ItemsControl>("LastRunActivity").GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.Equal((true, false), (shell.Find<Button>("RunTask").IsEffectivelyEnabled, shell.Find<Button>("CancelRun").IsEffectivelyEnabled));
    }

    [AvaloniaFact]
    public void Cancelling_a_running_task_stops_it_and_records_it_as_cancelled()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var folder = _temp.Seed(Task(Codex));
        var clients = Discover();
        var shell = Shell.Open(folder, clients);
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.Click(shell.InView<Button>("RunTask"));
        Assert.Equal("Running", CardStatus(shell));

        shell.Click(shell.InView<Button>("CancelRun"));

        shell.WaitUntil(() => CardStatus(shell) == "Cancelled", "the run is cancelled");
        Assert.Equal("Cancelled", Shell.TextOf(shell.InView<Border>("LastRunStatus")));
        Assert.Equal(Color.Parse("#FFFFFF"), CardFill(shell));
        Assert.Equal("Cancelled", CardStatus(Shell.Open(folder, clients)));
    }
}
