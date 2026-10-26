using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Clients;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.ConversationFixtures;
using static IDevelop.Desktop.Tests.NodeFixture;
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

    private static void Invoke(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IInvokeProvider>()!.Invoke();

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
        Invoke(shell.InView<Button>("OpenConversation"));
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
        string[] expected = ["[New session]", "Antigravity CLI: Which fruit?", "[Waiting for you]", "You: banana", "Antigravity CLI: Wrote banana.", "[Waiting for you]"];
        shell.WaitUntil(() => Lines(shell).SequenceEqual(expected), "the turns read in order", () => string.Join(" | ", Lines(shell)));

        var leaving = shell.Window.ViewModel.Leave();
        shell.WaitUntil(() => leaving.IsCompleted, "the window's nodes stop");
        var again = f.Open();
        OpenFromInspector(again, "Design");

        Assert.Equal(expected, Lines(again));
        Assert.Equal(NodeState.InputRequired, again.NodeModel("Design").State);
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
        Invoke(attention);
        shell.WaitUntil(() => shell.Has<ConversationView>("ConversationView") && Conversation(shell).Items.Count > 1, "the conversation opens");
        shell.Render();

        Assert.Equal("Failed", shell.Find<TextBlock>("ConversationStatus").Text);
        Assert.Contains(Conversation(shell).Items.OfType<MarkerItemViewModel>(), marker => marker.Title == "Failed" && marker.Detail.Contains("exited with code 2"));
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
            shell.Render();
            var watermark = shell.Find<TextBox>("ConversationComposer").Watermark ?? "";
            Invoke(shell.Find<Button>("CloseConversation"));
            shell.Render();
            return watermark;
        }

        Assert.Equal(("Message Codex", "Message the agent"), (Watermark("Design"), Watermark("Build")));
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
