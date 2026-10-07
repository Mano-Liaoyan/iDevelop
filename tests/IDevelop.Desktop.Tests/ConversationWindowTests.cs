using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Conversation;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>The conversation in the real window over the project's own attempt logs, opened from the inspector and the card.</summary>
public sealed class ConversationWindowTests : IDisposable
{
    private const string Prompt = "# Design\n\nDraft it.";
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private string Project(params WorkflowEdit[] edits) =>
        _temp.Seed(edits.Length > 0 ? edits : [TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it.")]);

    private static void Invoke(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IInvokeProvider>()!.Invoke();

    private static ConversationViewModel Conversation(Shell shell) =>
        shell.Window.ViewModel.Conversation ?? throw new InvalidOperationException("No conversation is open.");

    private static string[] Messages(Shell shell) =>
        [.. Conversation(shell).Items.OfType<MessageItemViewModel>().Select(message => $"{message.AuthorLabel}: {message.Text}")];

    private static void OpenFromInspector(Shell shell, string title)
    {
        shell.Click(shell.Header(shell.Node(title)));
        Invoke(shell.InView<Button>("OpenConversation"));
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView") && Conversation(shell).Idle.IsCompleted && Conversation(shell).Items.Count > 0,
            "the conversation shows its history");
        shell.Render();
    }

    // Attempt A ran two turns, the second from a queued message. B ran on its own and failed. C continues A's session.
    private static void WriteThreeAttempts(string project)
    {
        WriteAttempt(project, Requested(A, TestTasks.Design, 0, Prompt), Launched(1), At(2, new AgentEvent.SessionStarted("thread-1")),
            At(3, new AgentEvent.Message("Which fruit?")), new AttemptEvent.MessageQueued(T0.AddSeconds(4), "banana", false) { Id = "q1" },
            At(5, new AgentEvent.Succeeded("Which fruit?")), Exited(6),
            new AttemptEvent.TurnRequested(T0.AddSeconds(7), "banana", "codex", ["exec", "resume"]) { Consumed = ["q1"] }, Launched(8),
            At(9, new AgentEvent.Message("Wrote banana.")), At(10, new AgentEvent.Succeeded("Wrote banana.")), Exited(11));
        WriteAttempt(project, Requested(B, TestTasks.Design, 100, Prompt), Launched(101),
            At(102, new AgentEvent.Failed("The model is not available.")), Exited(103, 1));
        WriteAttempt(project, Requested(C, TestTasks.Design, 200, "Use the fixture", new Continuation(A, "thread-1")), Launched(201),
            At(202, new AgentEvent.SessionStarted("thread-1")), At(203, new AgentEvent.Message("Used the fixture.")),
            At(204, new AgentEvent.Succeeded("Used the fixture.")), Exited(205));
    }

    [AvaloniaFact]
    public void Several_turns_and_a_continuation_read_in_order_with_every_attempt_listed_and_read_again_after_a_restart()
    {
        var project = Project();
        WriteThreeAttempts(project);
        string[] expected = ["iDevelop: " + Prompt, "Codex: Which fruit?", "You: banana", "Codex: Wrote banana.", "You: Use the fixture", "Codex: Used the fixture."];
        var shell = Shell.Open(project);
        OpenFromInspector(shell, "Design");

        Assert.Equal(expected, Messages(shell));
        Assert.Equal("Design", shell.Find<TextBlock>("ConversationTitle").Text);
        Assert.Equal(3, Conversation(shell).Attempts.Count);
        Assert.Equal("Attempt 3 (current) · Succeeded · Codex, continues 1", shell.Picked("AttemptPicker"));
        Assert.Equal(["iDevelop", "Codex", "You", "Codex", "You", "Codex"],
            Conversation(shell).Items.OfType<MessageItemViewModel>().Select(message => message.AuthorLabel));
        Assert.Contains("Wrote banana.", Blocks(shell.Find<ConversationView>("ConversationView")));
        shell.Window.ViewModel.Leave().Wait();

        var restarted = Shell.Open(project);
        OpenFromInspector(restarted, "Design");
        Assert.Equal(expected, Messages(restarted));
    }

    [AvaloniaFact]
    public void A_failed_card_opens_its_conversation_from_its_attention_glyph()
    {
        var project = Project();
        WriteAttempt(project, Requested(B, TestTasks.Design, 0, Prompt), Launched(1),
            At(2, new AgentEvent.Failed("The model is not available.")), Exited(3, 1));
        var shell = Shell.Open(project);
        var attention = shell.InCard<Button>("Design", "CardAttention");

        Assert.True(attention.IsEffectivelyVisible);
        Assert.Equal("Failed: The model is not available.", Avalonia.Automation.AutomationProperties.GetName(attention));
        Assert.Equal((240.0, 64.0), (shell.Node("Design").Bounds.Width - 20, shell.Node("Design").Bounds.Height));
        Invoke(attention);
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView") && Conversation(shell).Items.Count > 0, "the conversation opens");
        shell.Render();

        Assert.Equal("Failed", shell.Find<TextBlock>("ConversationStatus").Text);
        Assert.Contains(Conversation(shell).Items.OfType<MarkerItemViewModel>(), marker => (marker.Title, marker.Detail) == ("Failed", "The model is not available."));
        Assert.Equal("Failed: The model is not available.", shell.Window.ViewModel.Canvas!.Nodes.Single().AttentionLabel);
    }

    [AvaloniaFact]
    public void A_task_that_never_ran_names_its_chosen_agent_in_the_composer()
    {
        var shell = Shell.Open(Project(
            TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it."),
            TaskAt(TestTasks.Build, "Build", 105, 300)));
        string Watermark(string title)
        {
            shell.Click(shell.Header(shell.Node(title)));
            Invoke(shell.InView<Button>("OpenConversation"));
            shell.WaitUntil(() => Conversation(shell).Idle.IsCompleted, "the conversation finished reading");
            shell.Render();
            var watermark = shell.Find<TextBox>("ConversationComposer").Watermark ?? "";
            Invoke(shell.Find<Button>("CloseConversation"));
            shell.Render();
            return watermark;
        }

        Assert.Equal(("Message Codex", "Message the agent"), (Watermark("Design"), Watermark("Build")));
    }

    [AvaloniaFact]
    public void The_attempt_picker_shows_only_once_a_task_has_an_attempt()
    {
        var project = Project(
            TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it."),
            TaskAt(TestTasks.Build, "Build", 105, 300, CodexHigh, "Build it."));
        WriteAttempt(project, Requested(B, TestTasks.Design, 0, Prompt), Launched(1),
            At(2, new AgentEvent.Failed("The model is not available.")), Exited(3, 1));
        var shell = Shell.Open(project);
        bool PickerShown(string title)
        {
            shell.Click(shell.Header(shell.Node(title)));
            Invoke(shell.InView<Button>("OpenConversation"));
            shell.WaitUntil(() => Conversation(shell).Idle.IsCompleted, "the conversation finished reading");
            shell.Render();
            var shown = shell.Find<ComboBox>("AttemptPicker").IsEffectivelyVisible;
            Invoke(shell.Find<Button>("CloseConversation"));
            shell.Render();
            return shown;
        }

        Assert.Equal((true, false), (PickerShown("Design"), PickerShown("Build")));
    }

    [AvaloniaFact]
    public void Expanding_or_closing_the_dock_gives_its_height_back_to_the_main_area()
    {
        var shell = Shell.Open(Project());
        var canvas = shell.Window.CanvasHost.Bounds.Height;
        shell.Click(shell.Header(shell.Node("Design")));
        Invoke(shell.InView<Button>("OpenConversation"));
        shell.Render();
        var expanded = shell.Find<ConversationView>("ConversationView").Bounds.Height;

        Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        var again = shell.Find<ConversationView>("ConversationView").Bounds.Height;
        Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        Invoke(shell.Find<Button>("CloseConversation"));
        shell.Render();

        Assert.Equal((800.0, 800.0, 800.0, 800.0), (canvas, expanded, again, shell.Window.CanvasHost.Bounds.Height));
    }

    [AvaloniaFact]
    public void Each_task_keeps_its_draft_across_task_switches_and_the_dock()
    {
        var shell = Shell.Open(Project(
            TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it."),
            TaskAt(TestTasks.Build, "Build", 105, 300, CodexHigh, "Build it.")));
        shell.Click(shell.Header(shell.Node("Design")));
        Invoke(shell.InView<Button>("OpenConversation"));
        shell.Render();
        Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        Assert.True(shell.Window.ViewModel.IsConversationDocked);

        shell.Find<TextBox>("ConversationComposer").Focus();
        shell.Type("Check edge cases");
        shell.Click(shell.Header(shell.Node("Build")));
        Assert.Equal(TestTasks.Build, Conversation(shell).Target.Task);
        Assert.Equal("", shell.Find<TextBox>("ConversationComposer").Text ?? "");
        shell.Find<TextBox>("ConversationComposer").Focus();
        shell.Type("Review output");
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("Check edge cases", shell.Find<TextBox>("ConversationComposer").Text);
        Assert.Equal("Check edge cases", shell.Find<TextBox>("Composer").Text);
        Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        Assert.False(shell.Window.ViewModel.IsConversationDocked);
        Assert.Equal("Check edge cases", shell.Find<TextBox>("ConversationComposer").Text);
        shell.Press(Key.Escape);
        Assert.Null(shell.Window.ViewModel.Conversation);
        shell.Click(shell.Header(shell.Node("Build")));
        Invoke(shell.InView<Button>("OpenConversation"));
        shell.Render();
        Assert.Equal("Review output", shell.Find<TextBox>("ConversationComposer").Text);
    }
}
