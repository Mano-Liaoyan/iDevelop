using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.AgyTurn;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>The conversation's composer, opened from the inspector and used through the real main window, with the fake agent behind on-disk shims.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ConversationTests
{
    private static readonly TaskId SayHi = TestTasks.Design;

    private static FakeRule Resumed(NodeFixture f, string reply) =>
        Reply("s-1", reply, FakeAgents.Resuming(ClientId.Antigravity, "s-1").CapturePrompt(Path.Combine(f.Evidence, "resumed.txt")));

    private static string ResumedWith(NodeFixture f) => File.ReadAllText(Path.Combine(f.Evidence, "resumed.txt")).Trim();

    private static Shell OpenSayHi(NodeFixture f)
    {
        var shell = f.Open();
        shell.Click(shell.Header(shell.Node("Say hi")));
        return shell;
    }

    [AvaloniaFact]
    public void A_message_sent_during_a_turn_waits_for_it_and_the_next_turn_of_the_session_answers_it()
    {
        using var f = new NodeFixture(NodeFixture.Task(SayHi, "Say hi", 105));
        f.Install(Resumed(f, "Wrote hi."), Reply("s-1", "Hello.", On().WaitForFile(f.Gate("go"))));
        var shell = OpenSayHi(f);
        Shell.Invoke(shell.InView<Button>("RunTask"));
        shell.WaitForState("Say hi", NodeState.Running);

        shell.Talk();
        shell.InView<TextBox>("ConversationComposer").Text = "Also write hi.";
        Shell.Invoke(shell.InView<Button>("ConversationSend"));
        Assert.Equal("", shell.Find<TextBox>("ConversationComposer").Text);
        f.Release("go");

        var resumed = Path.Combine(f.Evidence, "resumed.txt");
        shell.WaitUntil(() => File.Exists(resumed) && shell.NodeModel("Say hi").State == NodeState.Completed, "the next turn ends");
        Assert.Equal("Also write hi.", ResumedWith(f));
        Assert.Equal("Wrote hi.", shell.NodeModel("Say hi").LastReply);
    }

    [AvaloniaFact]
    public void Stop_and_send_stops_the_running_turn_and_the_next_turn_answers_at_once()
    {
        using var f = new NodeFixture(NodeFixture.Task(SayHi, "Say hi", 105));
        f.Install(Resumed(f, "Wrote hi."), Reply("s-1", "Hello.", On().Print(FakeAgents.SessionLine(ClientId.Antigravity, "s-1")).WaitForFile(f.Gate("never"))));
        var shell = OpenSayHi(f);
        Shell.Invoke(shell.InView<Button>("RunTask"));
        shell.WaitForState("Say hi", NodeState.Running);
        shell.Talk();
        shell.WaitUntil(() => shell.InView<Button>("ConversationStop").IsEffectivelyVisible, "the turn runs");
        Assert.False(shell.InView<Button>("ConversationSend").IsEffectivelyVisible);

        shell.InView<TextBox>("ConversationComposer").Text = "Stop and write hi.";
        shell.Render();
        Assert.False(shell.InView<Button>("ConversationStop").IsEffectivelyVisible);
        Assert.True(shell.InView<Button>("ConversationSend").IsEffectivelyVisible);
        shell.Click(shell.InView<Button>("ConversationStopAndSend"));

        var resumed = Path.Combine(f.Evidence, "resumed.txt");
        shell.WaitUntil(() => File.Exists(resumed) && shell.NodeModel("Say hi").State == NodeState.Completed, "the next turn ends");
        Assert.Equal("Stop and write hi.", ResumedWith(f));
    }

    [AvaloniaFact]
    public void Ctrl_Enter_in_the_composer_continues_a_finished_session_and_Enter_starts_a_new_line()
    {
        using var f = new NodeFixture(NodeFixture.Task(SayHi, "Say hi", 105));
        f.Install(Resumed(f, "Wrote both."), Reply("s-1", "Hello."));
        var shell = OpenSayHi(f);
        Shell.Invoke(shell.InView<Button>("RunTask"));
        shell.WaitForState("Say hi", NodeState.Completed);
        shell.Talk();
        var composer = shell.InView<TextBox>("ConversationComposer");
        composer.Focus();
        shell.Type("one");
        shell.Press(Key.Enter);
        shell.Type("two");
        Assert.Equal("one\ntwo", composer.Text);

        shell.Press(Key.Enter, RawInputModifiers.Control);

        // The reply comes in through the log before the engine records the turn's result.
        shell.WaitUntil(() => shell.NodeModel("Say hi") is { LastReply: "Wrote both.", Results.Count: 2 }, "the continued turn ends");
        Assert.Equal("one\ntwo", ResumedWith(f));
        Assert.Equal(2, shell.NodeModel("Say hi").Results.Count);
    }

    [AvaloniaFact]
    public void A_node_that_never_ran_says_why_it_takes_no_message_and_offers_no_terminal()
    {
        using var f = new NodeFixture(NodeFixture.Task(SayHi, "Say hi", 105));
        f.Install();
        var shell = OpenSayHi(f);
        shell.Talk();
        shell.InView<TextBox>("ConversationComposer").Text = "Hello?";

        Assert.Equal("Run the node first. A message continues its session.", shell.InView<TextBlock>("ConversationEmpty").Text);
        Assert.True(shell.Find<Button>("ConversationRunNode").IsEffectivelyEnabled);
        Assert.False(shell.InView<Button>("ConversationSend").IsEffectivelyEnabled);
        Assert.False(shell.NodeModel("Say hi").OpenInTerminalCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Open_in_terminal_copies_the_command_that_continues_the_session_in_the_project_folder()
    {
        using var f = new NodeFixture(NodeFixture.Plan(SayHi, "Say hi", 105));
        f.Install(Reply("s-1", "Hello."));
        var shell = OpenSayHi(f);
        Shell.Invoke(shell.InView<Button>("RunTask"));
        shell.WaitForState("Say hi", NodeState.Completed);

        shell.Talk();
        var more = shell.Find<Button>("ConversationModel");
        shell.Click(more);
        Assert.True(shell.Has<Control>("ClientLimitations"));
        more.Flyout!.Hide();
        shell.Render();
        shell.NodeModel("Say hi").OpenInTerminalCommand.Execute(null);

        shell.WaitUntil(() => shell.Status.StartsWith("Copied the command", StringComparison.Ordinal), "the command is copied");
        Assert.EndsWith("agy --conversation s-1", shell.Status);
        Assert.Contains(f.Project, shell.Status);
    }

    [AvaloniaFact]
    public void Approve_finishes_a_Chat_node_with_its_last_reply()
    {
        using var f = new NodeFixture(NodeFixture.Task(SayHi, "Say hi", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Hello there."));
        var shell = OpenSayHi(f);
        Shell.Invoke(shell.InView<Button>("RunTask"));
        shell.WaitForState("Say hi", NodeState.InputRequired);
        Assert.Equal("Needs you · Reply", shell.NodeModel("Say hi").StatusLabel);

        Shell.Invoke(shell.InView<Button>("Approve"));

        shell.WaitForState("Say hi", NodeState.Completed);
        Assert.Equal("Hello there.", Assert.Single(shell.NodeModel("Say hi").Results).Summary);
    }
}
