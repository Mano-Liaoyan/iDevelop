using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Inspector;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.AgyTurn;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>Each row of the Action Dock's table, in the real window over the fake agent, and the routes that land on Chat.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ActionDockTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = TestTasks.Review;
    private const string Rejects =
        "One problem.\n\n```idevelop\n{\"status\": \"verdict\", \"verdict\": \"changes\", \"findings\": [{\"id\": \"1\", \"severity\": \"high\", " +
        "\"location\": \"a.txt:1\", \"text\": \"The line says A.\", \"change\": \"Write A fixed.\"}]}\n```";

    private static ActionDockViewModel Dock(Shell shell) => shell.Window.ViewModel.Conversation!.Dock;

    private static string[] Shown(Shell shell) =>
    [
        .. shell.Find<Control>("ActionDockActions").GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible)
            .Select(button => $"{(button.Classes.Contains("primary") ? "*" : "")}{Avalonia.Automation.AutomationProperties.GetName(button)}"),
    ];

    private static void AssertHeading(Shell shell, string heading)
    {
        Assert.True(shell.Find<Control>("ActionDock").IsEffectivelyVisible);
        Assert.Equal(heading, shell.Find<TextBlock>("ActionDockTitle").Text);
    }

    private static void Open(Shell shell, string title)
    {
        shell.NodeModel(title).OpenConversationCommand.Execute(null);
        shell.Render();
    }

    [AvaloniaFact]
    public void Question_shows_each_choice_and_a_digit_picks_one()
    {
        using var f = new NodeFixture(AppTempFolder.TaskAt(A, "Paint", 105, 90, new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" }, "Paint.", ConversationMode.MayAsk));
        FakeAgents.Install(f.Fakes, ClientId.ClaudeCode, On().Print(FakeAgents.SessionLine(ClientId.ClaudeCode, "s-1"))
            .Print(FakeAgents.ClaudeAsks("Which color?", "Red", "Blue")).ReadLine(Path.Combine(f.Evidence, "answer.json"))
            .Print(FakeAgents.ReplyLines(ClientId.ClaudeCode, "Painted.")));
        var shell = f.Open();
        shell.NodeModel("Paint").RunCommand.Execute(null);
        shell.WaitUntil(() => shell.NodeModel("Paint").IsAsking, "the agent asks");
        Open(shell, "Paint");

        AssertHeading(shell, "Needs you · Question");
        Assert.Equal(ActionDockKind.Question, Dock(shell).Kind);
        Assert.True(shell.Find<Control>("ActionDockQuestions").IsEffectivelyVisible);
        Assert.Equal(["1", "2"], shell.NodeModel("Paint").Questions.Single().Choices.Select(choice => choice.Digit));
        Assert.False(shell.Find<Control>("ActionDockActions").IsEffectivelyVisible);
        Assert.Empty(shell.NodeModel("Paint").HeaderActions);
        Assert.True(shell.NodeModel("Paint").StopIsInMenu);

        shell.Find<TextBox>("ConversationComposer").Focus();
        shell.Press(Key.D1);
        Assert.True(shell.NodeModel("Paint").Questions.Single().Choices[0].IsChosen);
    }

    [AvaloniaFact]
    public void Reply_shows_its_detail_and_Accept_and_the_composer_writes_back()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Pears are ripe."));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.InputRequired);
        Open(shell, "A");

        AssertHeading(shell, "Needs you · Reply");
        Assert.Equal("The agent replied. Write back, or approve its reply as the result.", shell.Find<SelectableTextBlock>("RunTaskDetail").Text);
        Assert.Equal(["Accept as result"], Shown(shell));
        Assert.True(shell.Find<TextBox>("ConversationComposer").IsEffectivelyVisible);
        Assert.Equal("Reply to Antigravity CLI", shell.Find<TextBox>("ConversationComposer").Watermark);

        Shell.Invoke(shell.Find<Button>("Approve"));
        shell.WaitForState("A", NodeState.Completed);
    }

    [AvaloniaFact]
    public void Approval_shows_what_it_offers_and_a_note_box_and_has_no_composer()
    {
        using var f = new NodeFixture(Plan(A, "A", 105), Approval(B, "Check", 405), Dependency(A, B));
        f.Install(Reply("s-a", "A ready."));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("Check", NodeState.InputRequired);
        Open(shell, "Check");

        AssertHeading(shell, "Needs you · Approval");
        Assert.Equal(ActionDockKind.Approval, Dock(shell).Kind);
        Assert.Equal(["*Approve", "Send back"], Shown(shell));
        Assert.True(shell.Find<TextBox>("SendBackNote").IsEffectivelyVisible);
        Assert.False(shell.Find<TextBox>("ConversationComposer").IsEffectivelyVisible);
        Assert.True(shell.Find<Control>("ChatInputs").IsEffectivelyVisible);
        Assert.False(shell.Find<Button>("SendBack").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Round_limit_shows_Approve_Send_back_and_keeps_the_composer()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Review(B, "Check", 405), Dependency(A, B), new WorkflowEdit.SetRoundLimit(B, 1));
        f.Install(
            Reply("s-a", "A fixed.", FakeAgents.Resuming(ClientId.Antigravity, "s-a")),
            On().Choose((Fresh("A"), Reply("s-a", "A ready.", On().Write("a.txt", "A\n"))), (Fresh("Check"), Reply("s-r", Rejects))));
        var shell = f.Open();
        shell.Click(shell.Find<Button>("RunWorkflow"));
        shell.WaitUntil(() => shell.NodeModel("Check").EngineState is Engine.NodeState.InputRequired { Why: Engine.NodeState.RoundLimit }, "the round limit");
        Open(shell, "Check");

        AssertHeading(shell, "Needs you · Round limit");
        Assert.Equal(["*Approve", "Send back"], Shown(shell));
        Assert.True(shell.Find<TextBox>("SendBackNote").IsEffectivelyVisible);
        Assert.True(shell.Find<TextBox>("ConversationComposer").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Conflict_shows_Mark_resolved_first_and_Open_worktree()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Task(B, "B", 405), Task(C, "C", 705), Dependency(A, C), Dependency(B, C));
        f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("x.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("x.txt", "B\n"))),
            (Fresh("C"), Reply("s-c", "Report C"))));
        var shell = f.Open();
        shell.Click(shell.Find<Button>("RunWorkflow"));
        shell.WaitUntil(() => shell.NodeModel("C").EngineState is Engine.NodeState.InputRequired { Why: Engine.NodeState.Conflict }, "the conflict", () => shell.NodeModel("C").StatusLabel);
        Open(shell, "C");

        AssertHeading(shell, "Needs you · Conflict");
        Assert.Equal(["*Mark resolved", "Open worktree"], Shown(shell));
    }

    [AvaloniaFact]
    public void Interrupted_shows_Continue_first_and_Retry()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(Reply("s-1", "Hello.", On().Print(FakeAgents.SessionLine(ClientId.Antigravity, "s-1")).WaitForFile(f.Gate("never"))));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.Running);
        var leaving = shell.Window.ViewModel.Leave();
        shell.WaitUntil(() => leaving.IsCompleted, "the window's nodes stop");
        var again = f.Open();
        again.WaitForState("A", NodeState.InputRequired);
        Open(again, "A");

        AssertHeading(again, "Needs you · Interrupted");
        Assert.Equal(["*Continue", "Retry"], Shown(again));
    }

    [AvaloniaFact]
    public void Failed_shows_the_reason_and_Retry_first_and_Open_in_terminal()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(On().Stderr("The model is not available.").Exit(2));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.Failed);
        Open(shell, "A");

        AssertHeading(shell, "Failed");
        Assert.Contains("exited with code 2", shell.Find<SelectableTextBlock>("RunTaskDetail").Text);
        Assert.Equal(["*Retry", "Open in terminal"], Shown(shell));
        Assert.Contains("failed", shell.Find<Border>("ActionDock").Classes);
    }

    [AvaloniaFact]
    public void Stale_shows_its_detail_and_Retry()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(On().Choose((Fresh("A"), Reply("s-a", "A ready.", On().Write("a.txt", "A\n")))));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.Completed);
        shell.Window.ViewModel.Canvas!.Edit(new WorkflowEdit.SetField(A, "instructions", "Build A again."));
        shell.WaitForState("A", NodeState.Stale);
        Open(shell, "A");

        AssertHeading(shell, "Stale");
        Assert.Contains("Run it again", shell.Find<SelectableTextBlock>("RunTaskDetail").Text);
        Assert.Equal(["*Retry"], Shown(shell));
        Assert.Contains("warning", shell.Find<Border>("ActionDock").Classes);
    }

    [AvaloniaFact]
    public void A_run_refusal_shows_its_words_with_the_Warning_mark_and_no_actions()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));
        var shell = f.Open();
        Open(shell, "B");

        Assert.Equal(ActionDockKind.Refusal, Dock(shell).Kind);
        Assert.Contains("waits for A", shell.Find<TextBlock>("StartProblem").Text);
        Assert.False(shell.Find<Control>("ActionDockActions").IsEffectivelyVisible);
        Assert.False(shell.Find<TextBlock>("ActionDockTitle").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_node_that_needs_nothing_shows_no_dock()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(Reply("s-1", "Hello."));
        var shell = f.Open();
        Open(shell, "A");

        Assert.False(shell.ShowsAny("ActionDock"));
        Assert.False(shell.Find<TextBox>("ConversationComposer").IsEnabled);
    }

    [AvaloniaFact]
    public void Ctrl_J_lands_on_Chat_with_the_keys_in_the_composer_and_Enter_sends()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Wrote banana.", FakeAgents.Resuming(ClientId.Antigravity, "s-1")), Reply("s-1", "Which fruit?"));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.InputRequired);
        shell.Window.ViewModel.AgentPanel.Show(AgentPage.Task);
        shell.Render();

        shell.Press(Key.J, RawInputModifiers.Control);
        shell.WaitUntil(() => shell.Has<TextBox>("ConversationComposer") && shell.Find<TextBox>("ConversationComposer").IsFocused, "the composer takes the keys");

        Assert.True(shell.Window.ViewModel.AgentPanel.IsChat);
        shell.Type("banana");
        shell.Press(Key.Enter);
        shell.WaitUntil(() => shell.Window.ViewModel.Conversation!.Items.OfType<MessageItemViewModel>().Any(message => message.Text == "Wrote banana."), "the reply sends");
    }

    [AvaloniaFact]
    public void Shift_Enter_makes_a_new_line_and_Enter_with_nothing_written_sends_nothing()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which fruit?"));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.InputRequired);
        Open(shell, "A");
        var composer = shell.Find<TextBox>("ConversationComposer");
        composer.Focus();

        shell.Press(Key.Enter);
        Assert.Equal("", composer.Text ?? "");

        shell.Type("a");
        shell.Press(Key.Enter, RawInputModifiers.Shift);
        shell.Type("b");
        Assert.Equal("a\nb", composer.Text);
    }

    [AvaloniaFact]
    public void Selecting_a_card_opens_Chat_without_taking_the_keys_from_the_Map()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which fruit?"));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.InputRequired);

        shell.Click(shell.Header(shell.Node("A")));
        shell.Render();

        Assert.True(shell.Window.ViewModel.AgentPanel.IsChat);
        Assert.NotNull(shell.Window.ViewModel.Conversation);
        Assert.False(shell.Find<TextBox>("ConversationComposer").IsFocused);
    }
}
