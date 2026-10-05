using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>Runs go through the fake agent behind on-disk shims, resolved and launched like a real client.</summary>
[Collection(ProcessCollection.Name)]
public sealed class RunTests : IDisposable
{
    private static readonly TaskId SayHi = TestTasks.Design;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;
    private readonly string _gate;

    public RunTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _gate = Path.Combine(_temp.Create("evidence"), "go");
    }

    // A fake client that a failed test left waiting at the gate ends here, at the gate or at the deleted folder, or dotnet
    // test would wait for it. Leaving the project instead would touch the window after the headless session ended the test.
    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _temp.Dispose();
    }

    /// <summary>The client waits at the gate until it is stopped or the test opens the gate.</summary>
    private FakeRule Waits() => On("exec", "--json")
        .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
        .WaitForFile(_gate);

    private static WorkflowEdit.PlaceNode SayHiTask(ExecutionSettings execution) =>
        TaskAt(SayHi, "Say hi", 105, 90, execution, "Create hello.txt containing hi. Then reply with DONE.");

    private static Color CardFill(Shell shell) =>
        ((ISolidColorBrush)shell.Node("Say hi").GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("card")).Background!).Color;

    /// <summary>A window whose task runs and waits at the gate.</summary>
    private (Shell Shell, string Folder, ClientDirectory Clients) StartWaitingRun()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var folder = _temp.Seed(SayHiTask(Codex));
        var clients = _fakes.DiscoverAsync().Result;
        var shell = Shell.Open(folder, clients);
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.Click(shell.InView<Button>("RunTask"));
        Assert.Equal("Running", shell.CardText("Say hi", "CardStatus"));
        return (shell, folder, clients);
    }

    [AvaloniaFact]
    public void Running_a_task_whose_client_is_not_ready_shows_why_and_launches_nothing()
    {
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        var folder = _temp.Seed(SayHiTask(new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "high" }));
        var shell = Shell.Open(folder, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Say hi")));
        const string reason = "Claude Code is not ready. Claude Code is not signed in. Run claude in a terminal and sign in.";
        Assert.Equal(reason, shell.InView<TextBlock>("StartProblem").Text);

        shell.Click(shell.InView<Button>("RunTask"));

        Assert.Equal(reason, shell.Status);
        Assert.Equal("Not run", shell.CardText("Say hi", "CardStatus"));
        Assert.False(Directory.Exists(Path.Combine(folder, ".idp", "attempts")));
    }

    [AvaloniaFact]
    public void A_task_shows_that_it_runs_and_then_its_result_on_its_card_and_in_the_inspector()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits().Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(_temp.Seed(SayHiTask(Codex)), _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Say hi")));
        Assert.Equal(("Not run", Color.Parse("#FFFFFF")), (shell.CardText("Say hi", "CardStatus"), CardFill(shell)));

        shell.Click(shell.InView<Button>("RunTask"));

        Assert.Equal(("Running", Color.Parse("#1A0088FF")), (shell.CardText("Say hi", "CardStatus"), CardFill(shell)));
        Assert.Equal((false, true), (shell.Find<Button>("RunTask").IsEffectivelyEnabled, shell.Find<Button>("CancelRun").IsEffectivelyEnabled));
        Assert.Equal("", shell.Status);
        Assert.True(shell.Find<Control>("RunBar").IsEffectivelyVisible);
        var bar = ControlAutomationPeer.CreatePeerForElement(shell.Find<Control>("RunBar"));
        Assert.Equal(("RunBar", "Running task", true), (bar.GetAutomationId(), bar.GetName(), bar.IsControlElement()));
        Assert.Equal(
            ["Say hi", "Codex · GPT-5.5 · high", "Waiting for Codex…"],
            new[] { "RunBarTask", "RunBarAgent", "RunBarActivity" }.Select(id => shell.Find<TextBlock>(id).Text));
        Assert.Matches(@"^\d+ s$", shell.Find<TextBlock>("RunBarElapsed").Text);

        File.WriteAllText(_gate, "");
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the run succeeds");

        Assert.False(shell.Find<Control>("RunBar").IsEffectivelyVisible);
        Assert.Equal(Color.Parse("#1A34C759"), CardFill(shell));
        Assert.Equal(Color.Parse("#25292E"), ((ISolidColorBrush)shell.InCard<TextBlock>("Say hi", "CardStatus").Foreground!).Color);
        Assert.Equal("Succeeded", shell.InView<TextBlock>("LastRunStatus").Text);
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
        var (shell, folder, clients) = StartWaitingRun();

        shell.Click(shell.InView<Button>("CancelRun"));

        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Cancelled", "the run is cancelled");
        Assert.Equal("Cancelled", shell.InView<TextBlock>("LastRunStatus").Text);
        Assert.Equal(Color.Parse("#FFFFFF"), CardFill(shell));
        Assert.Equal("Cancelled", Shell.Open(folder, clients).CardText("Say hi", "CardStatus"));
    }

    [AvaloniaFact]
    public void Closing_the_window_during_a_run_asks_first_and_stop_and_leave_records_the_run_as_interrupted()
    {
        var (shell, folder, clients) = StartWaitingRun();

        shell.Window.Close();
        shell.Render();

        Assert.Equal(["\"Say hi\" is running. Stop it and leave?", "Stop and leave", "Keep running"], shell.DialogTexts());
        shell.Choose("StopAndLeave");
        shell.WaitUntil(() => !shell.Window.IsVisible, "the window closes");
        var reopened = Shell.Open(folder, clients);
        Assert.Equal("Interrupted", reopened.CardText("Say hi", "CardStatus"));
        reopened.Click(reopened.Header(reopened.Node("Say hi")));
        Assert.Equal("The project was closed while this task ran.", reopened.InView<TextBlock>("LastRunDetail").Text);
    }

    [AvaloniaFact]
    public void Two_tasks_run_at_once_and_closing_asks_about_both_and_stops_both()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var folder = _temp.Seed(SayHiTask(Codex), TaskAt(TestTasks.Review, "Review", 405, 90, Codex, "Review hello.txt."));
        var clients = _fakes.DiscoverAsync().Result;
        var shell = Shell.Open(folder, clients);
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.Click(shell.Header(shell.Node("Review")));
        shell.Click(shell.InView<Button>("RunTask"));

        Assert.Equal(["Running", "Running"], new[] { "Say hi", "Review" }.Select(title => shell.CardText(title, "CardStatus")));
        Assert.Equal("Review and 1 more", shell.Find<TextBlock>("RunBarTask").Text);
        shell.Window.Close();
        shell.Render();
        Assert.Equal(["2 tasks are running. Stop them and leave?", "Stop and leave", "Keep running"], shell.DialogTexts());
        shell.Choose("StopAndLeave");
        shell.WaitUntil(() => !shell.Window.IsVisible, "the window closes");

        var reopened = Shell.Open(folder, clients);
        Assert.Equal(["Interrupted", "Interrupted"], new[] { "Say hi", "Review" }.Select(title => reopened.CardText(title, "CardStatus")));
    }

    [AvaloniaFact]
    public void Keep_running_cancels_the_close_and_leaves_the_run_going()
    {
        var (shell, _, _) = StartWaitingRun();
        shell.Window.Close();
        shell.Render();

        shell.Choose("KeepRunning");

        Assert.Null(shell.Dialog);
        Assert.True(shell.Window.IsVisible);
        Assert.Equal("Running", shell.CardText("Say hi", "CardStatus"));
        shell.Click(shell.Find<Button>("RunBarCancel"));
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Cancelled", "the run is cancelled");
    }

    [AvaloniaFact]
    public void The_running_prompt_comes_before_the_save_prompt_and_cancelling_the_save_prompt_stops_nothing()
    {
        var (shell, _, _) = StartWaitingRun();
        shell.Click(shell.Find<Button>("AddTask"));
        shell.Window.Close();
        shell.Render();
        Assert.IsType<RunningTaskDialog>(shell.Dialog);
        shell.Choose("StopAndLeave");
        Assert.Equal(["Save changes to seed?", "Save", "Don't save", "Cancel"], shell.DialogTexts());

        shell.Choose("CancelChanges");

        Assert.Null(shell.Dialog);
        Assert.True(shell.Window.IsVisible);
        Assert.Equal(("Running", "seed* - iDevelop"), (shell.CardText("Say hi", "CardStatus"), shell.Window.Title));
        shell.Click(shell.Find<Button>("RunBarCancel"));
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Cancelled", "the run is cancelled");
    }

    [AvaloniaFact]
    public void Opening_another_folder_during_a_run_asks_first_and_records_the_run_as_interrupted()
    {
        var (shell, folder, clients) = StartWaitingRun();
        var other = _temp.Create("other");
        shell.Window.PickFolder = () => Task.FromResult<string?>(other);

        shell.Click(shell.Find<Button>("OpenFolder"));
        Assert.IsType<RunningTaskDialog>(shell.Dialog);
        shell.Choose("StopAndLeave");

        shell.WaitUntil(() => shell.Window.Title == "other - iDevelop", "the other folder opens");
        Assert.False(shell.Find<Control>("RunBar").IsEffectivelyVisible);
        Assert.Equal("Interrupted", Shell.Open(folder, clients).CardText("Say hi", "CardStatus"));
    }

    [AvaloniaFact]
    public void A_second_window_shows_the_other_windows_runs_and_learns_how_they_ended_when_it_tries_to_start_a_task()
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var folder = _temp.Seed(SayHiTask(Codex), TaskAt(TestTasks.Review, "Review", 405, 90, Codex, "Review hello.txt."));
        var clients = _fakes.DiscoverAsync().Result;
        var first = Shell.Open(folder, clients);
        first.Click(first.Header(first.Node("Say hi")));
        first.Click(first.InView<Button>("RunTask"));
        var second = Shell.Open(folder, clients);
        second.Click(second.Header(second.Node("Say hi")));

        Assert.Equal(("Running in another window", "Running in another window"), (second.CardText("Say hi", "CardStatus"), second.InView<TextBlock>("LastRunStatus").Text));
        Assert.Equal((true, false), (second.Find<Button>("RunTask").IsEffectivelyEnabled, second.Find<Button>("CancelRun").IsEffectivelyEnabled));
        second.Click(second.InView<Button>("RunTask"));
        Assert.Equal("\"Say hi\" is already running.", second.Status);

        first.Click(first.Find<Button>("RunBarCancel"));
        first.WaitUntil(() => first.CardText("Say hi", "CardStatus") == "Cancelled", "the first window's run is cancelled");
        first.Click(first.Header(first.Node("Review")));
        first.Click(first.InView<Button>("RunTask"));
        second.Click(second.InView<Button>("RunTask"));

        Assert.Equal(["Running", "Running in another window"], new[] { "Say hi", "Review" }.Select(title => second.CardText(title, "CardStatus")));

        first.Click(first.Find<Button>("RunBarCancel"));
        first.WaitUntil(() => first.CardText("Review", "CardStatus") == "Cancelled", "the first window's second run is cancelled");
        second.Click(second.Find<Button>("RunBarCancel"));
        second.WaitUntil(() => second.CardText("Say hi", "CardStatus") == "Cancelled", "the second window's run is cancelled");
    }

    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(900, 600)]
    public void The_run_bar_covers_neither_the_zoom_controls_nor_the_minimap(double width, double height)
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var shell = Shell.Open(_temp.Seed(SayHiTask(Codex)), _fakes.DiscoverAsync().Result);
        shell.Window.Width = width;
        shell.Window.Height = height;
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.Click(shell.InView<Button>("RunTask"));

        var bar = shell.Bounds(shell.Find<Control>("RunBar"));
        Assert.Equal(new Size(width, height), shell.Window.ClientSize);
        Assert.All(
            [shell.Bounds(Shell.Around(shell.Find<Button>("ZoomIn"), "floating")), shell.Bounds(Shell.Around(shell.Find<Minimap>("Minimap"), "floating"))],
            other => Assert.False(bar.Intersects(other), $"The run bar at {bar} covers {other}"));
        Assert.True(shell.Bounds(shell.Editor).Contains(bar), $"The run bar at {bar} leaves the canvas");
        var (title, cancel) = (shell.Bounds(shell.Find<TextBlock>("RunBarTask")), shell.Bounds(shell.Find<Button>("RunBarCancel")));
        Assert.True(bar.Contains(title) && bar.Contains(cancel) && !title.Intersects(cancel), $"The bar at {bar} squeezes {title} and {cancel}");

        shell.Click(shell.Find<Button>("RunBarCancel"));
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Cancelled", "the run is cancelled");
    }

    [AvaloniaFact]
    public void The_run_bar_still_cancels_a_run_whose_task_was_deleted()
    {
        var (shell, folder, clients) = StartWaitingRun();
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.Press(Key.Delete);
        Assert.Empty(shell.Nodes());
        Assert.Equal("Say hi", shell.Find<TextBlock>("RunBarTask").Text);

        shell.Click(shell.Find<Button>("RunBarCancel"));

        shell.WaitUntil(() => !shell.Find<Control>("RunBar").IsEffectivelyVisible, "the run bar goes away");
        Assert.Equal("Cancelled", Shell.Open(folder, clients).CardText("Say hi", "CardStatus"));
    }
}
