using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Theme;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.ConversationFixtures;
using static IDevelop.TestSupport.AgyTurn;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>The conversation in the real window over the node's own log, opened from the inspector and the card.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ConversationWindowTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private string Project(params WorkflowEdit[] edits) =>
        _temp.Seed(edits.Length > 0 ? edits : [TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it.")]);

    private static ConversationViewModel Conversation(Shell shell) =>
        shell.Window.ViewModel.Conversation ?? throw new InvalidOperationException("No conversation is open.");

    private static string[] Lines(Shell shell) => [.. Conversation(shell).Items.Select(item => item switch
    {
        MessageItemViewModel message => $"{message.AuthorLabel}: {message.Text}",
        MarkerItemViewModel marker => $"[{marker.Title}]",
        _ => "",
    })];

    private static void OpenFromInspector(Shell shell, string title)
    {
        shell.Click(shell.Header(shell.Node(title)));
        Shell.Invoke(shell.InView<Button>("OpenConversation"));
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView") && Conversation(shell).Items.Count > 0, "the conversation shows its history");
        shell.Render();
    }

    [AvaloniaFact]
    public void A_continued_session_reads_in_order_and_reads_the_same_after_the_project_opens_again()
    {
        using var f = new NodeFixture(NodeFixture.Task(TestTasks.Design, "Design", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Wrote banana.", FakeAgents.Resuming(ClientId.Antigravity, "s-1")), Reply("s-1", "Which fruit?"));
        var shell = f.Open();
        shell.NodeModel("Design").RunCommand.Execute(null);
        shell.WaitForState("Design", NodeState.InputRequired);
        OpenFromInspector(shell, "Design");
        shell.Find<TextBox>("ConversationComposer").Focus();
        shell.Type("banana");
        shell.Press(Key.Enter, RawInputModifiers.Control);
        shell.WaitUntil(() => Lines(shell).Contains("Antigravity CLI: Wrote banana."), "the second turn ends");
        string[] expected = ["[New session]", "Antigravity CLI: Which fruit?", "[Needs you]", "You: banana", "Antigravity CLI: Wrote banana.", "[Needs you]"];
        shell.WaitUntil(() => Lines(shell).SequenceEqual(expected), "the turns read in order", () => string.Join(" | ", Lines(shell)));

        var leaving = shell.Window.ViewModel.Leave();
        shell.WaitUntil(() => leaving.IsCompleted, "the window's nodes stop");
        var again = f.Open();
        OpenFromInspector(again, "Design");

        Assert.Equal(expected, Lines(again));
        Assert.Equal(NodeState.InputRequired, again.NodeModel("Design").State);
    }

    [AvaloniaFact]
    public void A_failed_card_opens_its_conversation_from_its_badge_at_the_zoom_a_fit_rests_at()
    {
        using var f = new NodeFixture(NodeFixture.Task(TestTasks.Design, "Design", 105));
        f.Install(On().Stderr("The model is not available.").Exit(2));
        var shell = f.Open();
        shell.NodeModel("Design").RunCommand.Execute(null);
        shell.WaitForState("Design", NodeState.Failed);
        shell.Editor.ViewportZoom = ZoomTiers.ActualSize;
        shell.Render();
        var attention = shell.InCard<Button>("Design", "CardFarAttention");

        Assert.True(attention.IsEffectivelyVisible);
        Shell.Invoke(attention);
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView"), "the conversation opens");
    }

    [AvaloniaFact]
    public void A_failed_card_opens_its_conversation_from_its_attention_glyph()
    {
        using var f = new NodeFixture(NodeFixture.Task(TestTasks.Design, "Design", 105));
        f.Install(On().Stderr("The model is not available.").Exit(2));
        var shell = f.Open();
        shell.NodeModel("Design").RunCommand.Execute(null);
        shell.WaitForState("Design", NodeState.Failed);
        var attention = shell.InCard<Button>("Design", "CardAttention");

        Assert.True(attention.IsEffectivelyVisible);
        Assert.StartsWith("Failed: ", Avalonia.Automation.AutomationProperties.GetName(attention));
        Assert.Equal((240.0, 64.0), (shell.Node("Design").Bounds.Width - 20, shell.Node("Design").Bounds.Height));
        Shell.Invoke(attention);
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView") && Conversation(shell).Items.Count > 1, "the conversation opens");
        shell.Render();

        Assert.Equal("Failed", shell.Find<TextBlock>("ConversationStatus").Text);
        Assert.Contains(Conversation(shell).Items.OfType<MarkerItemViewModel>(), marker => marker.Title == "Failed" && marker.Detail.Contains("exited with code 2"));
    }

    [AvaloniaFact]
    public void A_conversation_that_switches_to_another_node_follows_that_node_and_no_longer_the_first()
    {
        using var f = new NodeFixture(NodeFixture.Task(TestTasks.Design, "Design", 105), NodeFixture.Task(TestTasks.Build, "Build", 300));
        f.Install(On().Stderr("The model is not available.").Exit(2));
        var shell = f.Open();
        var (design, build) = (shell.NodeModel("Design"), shell.NodeModel("Build"));
        design.OpenConversationCommand.Execute(null);
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView"), "the first conversation opens");
        shell.Render();
        var opened = Conversation(shell);

        build.OpenConversationCommand.Execute(null);
        shell.WaitUntil(() => Conversation(shell) != opened, "the conversation switches");
        shell.Render();
        Assert.Equal("Build", shell.Find<TextBlock>("ConversationTitle").Text);

        design.Title = "Sketch";
        build.Title = "Assemble";
        build.RunCommand.Execute(null);
        shell.WaitForState("Assemble", NodeState.Failed);
        shell.Render();

        Assert.Equal("Assemble", shell.Find<TextBlock>("ConversationTitle").Text);
        Assert.Equal("Failed", shell.Find<TextBlock>("ConversationStatus").Text);
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
            Shell.Invoke(shell.InView<Button>("OpenConversation"));
            shell.Render();
            var watermark = shell.Find<TextBox>("ConversationComposer").Watermark ?? "";
            Shell.Invoke(shell.Find<Button>("CloseConversation"));
            shell.Render();
            return watermark;
        }

        Assert.Equal(("Message Codex", "Message the agent"), (Watermark("Design"), Watermark("Build")));
    }

    [AvaloniaFact]
    public void A_double_click_on_a_card_opens_its_conversation_with_the_keys_in_the_composer()
    {
        var shell = Shell.Open(Project());

        shell.DoubleClick(shell.Header(shell.Node("Design")));
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView"), "the conversation opens");
        shell.Render();

        Assert.Equal("Design", Conversation(shell).Node.Title);
        Assert.True(shell.Find<TextBox>("ConversationComposer").IsFocused);
    }

    [AvaloniaFact]
    public void Enter_on_a_selected_card_opens_its_conversation_with_the_keys_in_the_composer()
    {
        var shell = Shell.Open(Project());
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Press(Key.Enter);
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView"), "the conversation opens");
        shell.Render();

        Assert.True(shell.Find<TextBox>("ConversationComposer").IsFocused);
    }

    [AvaloniaFact]
    public void The_docked_header_shows_a_title_whole_while_the_row_has_room()
    {
        const string title = "Route poison events to the DLQ";
        var shell = Shell.Open(Project(TaskAt(TestTasks.Design, title, 105, 90, CodexHigh, "Draft it.")));
        shell.Click(shell.Header(shell.Node(title)));
        Shell.Invoke(shell.InView<Button>("OpenConversation"));
        shell.Render();
        if (!shell.Window.ViewModel.IsConversationDocked)
        {
            Shell.Invoke(shell.Find<Button>("ConversationLayout"));
            shell.Render();
        }

        // The title is measured at its natural width, and the cut keeps a pixel of slack, so the row gives that pixel back.
        var shown = shell.Find<EllipsisText>("ConversationTitle");
        Assert.Equal(title, shown.Text);
    }

    [AvaloniaFact]
    public void Expanding_or_closing_the_dock_gives_its_height_back_to_the_main_area()
    {
        var shell = Shell.Open(Project());
        var canvas = shell.Window.CanvasArea.Bounds.Height;
        shell.Click(shell.Header(shell.Node("Design")));
        Shell.Invoke(shell.InView<Button>("OpenConversation"));
        shell.Render();
        var expanded = shell.Find<ConversationView>("ConversationView").Bounds.Height;

        Shell.Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        Shell.Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        var again = shell.Find<ConversationView>("ConversationView").Bounds.Height;
        Shell.Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        Shell.Invoke(shell.Find<Button>("CloseConversation"));
        shell.Render();

        Assert.Equal((canvas, canvas, canvas), (expanded, again, shell.Window.CanvasArea.Bounds.Height));
    }

    [AvaloniaFact]
    public void Each_task_keeps_its_draft_across_task_switches_and_the_dock()
    {
        var shell = Shell.Open(Project(
            TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it."),
            TaskAt(TestTasks.Build, "Build", 105, 300, CodexHigh, "Build it.")));
        shell.Click(shell.Header(shell.Node("Design")));
        Shell.Invoke(shell.InView<Button>("OpenConversation"));
        shell.Render();
        Shell.Invoke(shell.Find<Button>("ConversationLayout"));
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
        Shell.Invoke(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        Assert.False(shell.Window.ViewModel.IsConversationDocked);
        Assert.Equal("Check edge cases", shell.Find<TextBox>("ConversationComposer").Text);
        shell.Press(Key.Escape);
        Assert.Null(shell.Window.ViewModel.Conversation);
        shell.Click(shell.Header(shell.Node("Build")));
        Shell.Invoke(shell.InView<Button>("OpenConversation"));
        shell.Render();
        Assert.Equal("Review output", shell.Find<TextBox>("ConversationComposer").Text);
    }
}
