using System.Text.RegularExpressions;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>The inspector's composer, used through the real main window, with the fake agent behind on-disk shims.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ConversationTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private static readonly TaskId SayHi = TestTasks.Design;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;
    private readonly string _evidence;
    private readonly string _gate;
    private string _project = "";

    public ConversationTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _evidence = _temp.Create("evidence");
        _gate = Path.Combine(_evidence, "go");
    }

    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _temp.Dispose();
    }

    private static FakeRule Asks() => Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session));

    private FakeRule Answers(string text) =>
        Resuming(ClientId.Codex, Session).CaptureStdin(Path.Combine(_evidence, "resumed.txt")).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, text));

    private Shell OpenSayHi(ConversationMode conversation = ConversationMode.Autonomous)
    {
        _project = _temp.Seed(TaskAt(SayHi, "Say hi", 105, 90, Codex, "Create hello.txt containing hi.", conversation));
        var shell = Shell.Open(_project, _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Say hi")));
        return shell;
    }

    private static void Invoke(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IInvokeProvider>()!.Invoke();

    [AvaloniaFact]
    public void A_message_sent_through_UI_Automation_during_a_turn_waits_for_it_and_the_next_turn_answers_it()
    {
        Install(_fakes, ClientId.Codex, Answers("Wrote banana."), Asks().WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Which fruit?")));
        var shell = OpenSayHi();
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.Find<Button>("StopAndSend").IsEffectivelyVisible && string.IsNullOrEmpty(shell.Find<TextBlock>("SendProblem").Text), "the session is reported");

        var composer = ControlAutomationPeer.CreatePeerForElement(shell.InView<TextBox>("Composer"));
        Assert.Equal(("Composer", "Message"), (composer.GetAutomationId(), composer.GetName()));
        composer.GetProvider<IValueProvider>()!.SetValue("banana");
        shell.Render();
        Invoke(shell.InView<Button>("SendMessage"));
        shell.WaitUntil(() => shell.Find<StackPanel>("Waiting").IsEffectivelyVisible, "the message waits");

        Assert.Equal(["Waiting for the turn to end", "banana"], Shell.Texts(shell.Find<StackPanel>("Waiting")));
        Assert.Equal("", shell.Find<TextBox>("Composer").Text);
        Assert.Equal("Running", shell.CardText("Say hi", "CardStatus"));
        File.WriteAllText(_gate, "");
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the second turn succeeds");
        Assert.Equal(("Which fruit?", "Wrote banana."), (shell.Find<TextBox>("TurnReply1").Text, shell.Find<TextBox>("LastRunResult").Text));
        Assert.Equal(["You", "banana"], Shell.Texts(shell.Find<ItemsControl>("Conversation")));
        Assert.Equal("banana", File.ReadAllText(Path.Combine(_evidence, "resumed.txt")));
        Assert.False(shell.Find<Button>("StopAndSend").IsEffectivelyVisible);
        Assert.False(shell.Find<StackPanel>("Waiting").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Stop_and_send_stops_the_running_turn_and_the_next_turn_answers_at_once()
    {
        Install(_fakes, ClientId.Codex, Answers("Using an apple."), Asks().Hang());
        var shell = OpenSayHi();
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.Find<Button>("StopAndSend").IsEffectivelyVisible && string.IsNullOrEmpty(shell.Find<TextBlock>("SendProblem").Text), "the session is reported");
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Stop. Use an apple.");
        Assert.True(shell.Find<Button>("StopAndSend").IsEffectivelyEnabled);

        shell.Click(shell.InView<Button>("StopAndSend"));

        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the next turn succeeds");
        Assert.Equal(["You stopped this turn.", "You", "Stop. Use an apple."], Shell.Texts(shell.Find<ItemsControl>("Conversation")));
        Assert.Equal("Using an apple.", shell.Find<TextBox>("LastRunResult").Text);
        Assert.False(shell.Has<TextBox>("TurnReply1") && shell.Find<TextBox>("TurnReply1").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_turn_that_failed_before_the_next_one_says_why()
    {
        Install(_fakes, ClientId.Codex, Answers("Tried again."), Asks().WaitForFile(_gate).Stderr("error: model not found").Exit(1));
        var shell = OpenSayHi();
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.Find<Button>("StopAndSend").IsEffectivelyVisible && string.IsNullOrEmpty(shell.Find<TextBlock>("SendProblem").Text), "the session is reported");
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Try again.");
        shell.Click(shell.InView<Button>("SendMessage"));
        shell.WaitUntil(() => shell.Find<StackPanel>("Waiting").IsEffectivelyVisible, "the message waits");

        File.WriteAllText(_gate, "");

        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the next turn succeeds");
        Assert.Equal(
            ["This turn failed. Codex exited with code 1: error: model not found", "You", "Try again."],
            Shell.Texts(shell.Find<ItemsControl>("Conversation")));
        Assert.Equal("Tried again.", shell.Find<TextBox>("LastRunResult").Text);
    }

    [AvaloniaFact]
    public void Ctrl_Enter_in_the_composer_continues_a_finished_run_in_a_new_attempt_and_Enter_starts_a_new_line()
    {
        Install(_fakes, ClientId.Codex, Answers("Added a test."), Asks().Print(ReplyLines(ClientId.Codex, "Wrote hello.txt.")));
        var shell = OpenSayHi();
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the first run succeeds");
        Assert.Equal("Wrote hello.txt.", shell.Find<TextBox>("LastRunResult").Text);
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Now add a test.");
        shell.Press(Key.Enter);
        shell.Type("Keep it short.");
        Assert.Equal("Now add a test.\nKeep it short.", shell.Find<TextBox>("Composer").Text);

        shell.Press(Key.Enter, RawInputModifiers.Control);

        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded" && shell.Find<TextBox>("LastRunResult").Text == "Added a test.", "the continuation succeeds");
        Assert.Equal("", shell.Find<TextBox>("Composer").Text);
        Assert.Equal("Wrote hello.txt.", shell.Find<TextBox>("TurnReply1").Text);
        Assert.Equal(["You", "Now add a test.\nKeep it short."], Shell.Texts(shell.Find<ItemsControl>("Conversation"))[^2..]);
        Assert.Equal("Now add a test.\nKeep it short.", File.ReadAllText(Path.Combine(_evidence, "resumed.txt")));
        Assert.Equal("", shell.Status);
    }

    [AvaloniaFact]
    public void After_a_continuation_the_question_the_person_answered_stays_above_the_answer()
    {
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Wrote banana into answer.txt.")),
            Asks().Print(ReplyLines(ClientId.Codex, "Which fruit should go into answer.txt?")));
        var shell = OpenSayHi();
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the question is asked");
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("banana");

        shell.Press(Key.Enter, RawInputModifiers.Control);

        shell.WaitUntil(() => shell.Has<TextBox>("TurnReply1"), "the earlier question shows with the continuation");
        // Its log is read once per continuation, so the running attempt's later changes never need it again.
        var attempts = Path.Combine(_project, ".idp", "attempts", SayHi.ToString());
        File.Delete(Path.Combine(Directory.EnumerateDirectories(attempts).Order().First(), "events.jsonl"));
        File.WriteAllText(_gate, "");
        shell.WaitUntil(() => shell.Find<TextBox>("LastRunResult").Text == "Wrote banana into answer.txt.", "the continuation answers");
        Assert.Equal("Which fruit should go into answer.txt?", shell.Find<TextBox>("TurnReply1").Text);
        Assert.Collection(
            Shell.Texts(shell.Find<ItemsControl>("Conversation")),
            line => Assert.Matches(@"^Succeeded · Started .+ · took \d+ s$", line),
            line => Assert.Matches(@"^Succeeded · Started .+ · took \d+ s$", line),
            line => Assert.Equal("You", line),
            line => Assert.Equal("banana", line));
    }

    [AvaloniaFact]
    public void A_task_that_never_ran_says_why_it_takes_no_message_and_offers_no_terminal()
    {
        Install(_fakes, ClientId.Codex, Asks());
        var shell = OpenSayHi();
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Hello?");

        Assert.Equal("Run this task first. Then you can write to its agent.", shell.InView<TextBlock>("SendProblem").Text);
        Assert.Equal(
            (false, false, false),
            (shell.Find<Button>("SendMessage").IsEffectivelyEnabled, shell.Find<Button>("StopAndSend").IsEffectivelyVisible, shell.Find<Button>("OpenInTerminal").IsEffectivelyEnabled));
    }

    [AvaloniaFact]
    public void Open_in_terminal_copies_the_clients_command_and_the_inspector_says_turns_taken_there_are_not_recorded()
    {
        Install(_fakes, ClientId.Codex, Asks().WaitForFile(_gate).Print(ReplyLines(ClientId.Codex, "Done.")));
        var shell = OpenSayHi(ConversationMode.Chat);
        var copied = new List<string>();
        shell.Window.Copy = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        shell.RunOnItsOwn();
        shell.WaitUntil(() => string.IsNullOrEmpty(shell.Find<TextBlock>("SendProblem").Text), "the session is reported");
        Assert.False(shell.Find<Button>("OpenInTerminal").IsEffectivelyEnabled);
        File.WriteAllText(_gate, "");
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Waiting for you", "the task waits");

        Invoke(shell.InView<Button>("OpenInTerminal"));

        shell.WaitUntil(() => copied.Count == 1, "the command is copied");
        var command = OperatingSystem.IsWindows()
            ? $"Set-Location -LiteralPath '{_project}'; codex resume {Session}"
            : $"cd '{_project}' && codex resume {Session}";
        Assert.Equal([command], copied);
        Assert.Equal($"Copied {command}. Paste it in a terminal to continue this session in {_project}.", shell.Status);
        var note = shell.InView<TextBlock>("TerminalNote").Text!;
        Assert.Matches($@"^Opened in a terminal in {Regex.Escape(_project)} at .+\. Turns taken there are not in iDevelop's record\.$", note);
        Assert.Equal("Waiting for you", shell.InView<TextBlock>("LastRunStatus").Text);
    }

    [AvaloniaFact]
    public void When_the_clipboard_fails_Open_in_terminal_shows_the_command_to_run_and_still_records_the_hand_off()
    {
        Install(_fakes, ClientId.Codex, Asks().Print(ReplyLines(ClientId.Codex, "Done.")));
        var shell = OpenSayHi(ConversationMode.Chat);
        // What Avalonia's Windows clipboard throws while another program holds the clipboard.
        shell.Window.Copy = _ => Task.FromException(new TimeoutException("Timeout opening clipboard."));
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Waiting for you", "the task waits");

        Invoke(shell.InView<Button>("OpenInTerminal"));

        shell.WaitUntil(() => shell.Status.StartsWith("iDevelop could not copy", StringComparison.Ordinal), "the notice says the copy failed");
        var command = OperatingSystem.IsWindows()
            ? $"Set-Location -LiteralPath '{_project}'; codex resume {Session}"
            : $"cd '{_project}' && codex resume {Session}";
        Assert.Equal($"iDevelop could not copy the command. To continue this session in {_project}, run it in a terminal: {command}", shell.Status);
        Assert.Matches($@"^Opened in a terminal in {Regex.Escape(_project)} at ", shell.InView<TextBlock>("TerminalNote").Text);
    }

    [AvaloniaFact]
    public void A_May_ask_task_that_asks_shows_its_question_and_the_reply_finishes_it()
    {
        const string asking = "I need one answer.\n\n```idevelop\n{\"status\": \"asking\", \"question\": \"Which fruit?\"}\n```";
        Install(_fakes, ClientId.Codex, Answers("Wrote banana."), Asks().Print(ReplyLines(ClientId.Codex, asking)));
        var shell = OpenSayHi(ConversationMode.MayAsk);
        Assert.Equal("May ask", shell.Find<ComboBox>("TaskConversation").SelectedItem!.ToString());

        shell.RunOnItsOwn();

        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Waiting for you", "the task waits");
        Assert.Equal("Which fruit?", shell.InView<TextBlock>("PendingQuestion").Text);
        Assert.Equal("1 task waits for you", shell.Find<TextBlock>("WaitingCount").Text);
        Assert.True(shell.Find<Button>("NextWaiting").IsVisible);
        // Its Run starts a workflow run in a checkout of its own, which this conversation keeps out of (#90).
        Assert.False(shell.Find<TextBlock>("StartProblem").IsEffectivelyVisible);

        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("banana");
        shell.Click(shell.InView<Button>("SendMessage"));

        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the reply finishes the task");
        Assert.Equal("banana", File.ReadAllText(Path.Combine(_evidence, "resumed.txt")));
        Assert.False(shell.Find<Button>("NextWaiting").IsVisible);
        Assert.Equal("Wrote banana.", shell.InView<TextBox>("LastRunResult").Text);
    }

    [AvaloniaFact]
    public void Mark_done_finishes_a_Chat_task_with_its_last_reply()
    {
        Install(_fakes, ClientId.Codex, Asks().Print(ReplyLines(ClientId.Codex, "Here is a plan.")));
        var shell = OpenSayHi(ConversationMode.Chat);
        shell.RunOnItsOwn();
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Waiting for you", "the task waits");
        Assert.Equal("The agent replied. Write back, or mark the task done.", shell.InView<TextBlock>("PendingQuestion").Text);

        shell.Click(shell.InView<Button>("MarkDone"));

        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the task is done");
        Assert.Equal("Here is a plan.", shell.InView<TextBox>("LastRunResult").Text);
        Assert.False(shell.Find<Button>("MarkDone").IsVisible);
    }
}
