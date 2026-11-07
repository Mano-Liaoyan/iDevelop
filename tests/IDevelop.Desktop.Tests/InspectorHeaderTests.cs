using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Inspector;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.AgyTurn;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The pinned header's actions for each state, the Input and Output sections, the overview's states, and the inspector's keys.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class InspectorHeaderTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static string[] Actions(Shell shell) =>
    [
        .. shell.Find<ItemsControl>("NodeActions").GetVisualDescendants().OfType<Button>()
            .Select(button => $"{(button.Classes.Contains("primary") ? "*" : "")}{AutomationProperties.GetName(button)}"),
    ];

    private Shell Linked() => Shell.Open(_temp.Seed(
        TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90, Codex),
        new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));

    [AvaloniaFact]
    public void An_idle_node_keeps_Conversation_first_and_makes_Run_primary()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal(["Conversation", "*Run"], Actions(shell));
    }

    [AvaloniaFact]
    public void A_node_that_stops_running_says_so_to_its_spinning_glyph()
    {
        using var f = new NodeFixture(NodeFixture.Task(Design, "Say hi", 105));
        f.Install(Reply("s-1", "Hello."));
        var shell = f.Open();
        var node = shell.Window.ViewModel.Canvas!.Nodes.Single();
        var raised = 0;
        node.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(node.IsRunningNow) ? 1 : 0;

        shell.RunNode("Say hi");
        shell.WaitForState("Say hi", NodeState.Completed);
        shell.Render();

        Assert.False(node.IsRunningNow);
        Assert.True(raised > 0, "The glyph is never told that the node stopped.");
    }

    [AvaloniaFact]
    public void A_completed_node_leads_with_its_conversation_and_offers_Run_from_here()
    {
        using var f = new NodeFixture(NodeFixture.Task(Design, "Say hi", 105));
        f.Install(Reply("s-1", "Hello."));
        var shell = f.Open();
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.RunNode("Say hi");
        shell.WaitForState("Say hi", NodeState.Completed);
        shell.Render();

        Assert.Equal(["*Conversation", "Run from here"], Actions(shell));
    }

    [AvaloniaFact]
    public void A_node_that_waits_for_a_reply_leads_with_Reply_and_offers_the_message_as_its_result()
    {
        using var f = new NodeFixture(NodeFixture.Task(Design, "Say hi", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Pears are ripe."));
        var shell = f.Open();
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.RunNode("Say hi");
        shell.WaitForState("Say hi", NodeState.InputRequired);
        shell.Render();

        Assert.Equal(["*Reply", "Use as result"], Actions(shell));
        Assert.True(shell.NodeModel("Say hi").RetryIsInMenu);
    }

    [AvaloniaFact]
    public void An_open_question_is_no_result_so_the_header_offers_only_Reply()
    {
        using var f = new NodeFixture(NodeFixture.Task(Design, "Say hi", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which **fruit?**"));
        var shell = f.Open();
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.RunNode("Say hi");
        shell.WaitForState("Say hi", NodeState.InputRequired);
        shell.Render();

        Assert.Equal(["*Reply"], Actions(shell));
        Assert.False(shell.NodeModel("Say hi").ApproveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void An_open_conversation_leaves_the_header_no_action_and_a_question_stays_in_it()
    {
        using var f = new NodeFixture(NodeFixture.Task(Design, "Say hi", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which fruit?"));
        var shell = f.Open();
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.RunNode("Say hi");
        shell.WaitForState("Say hi", NodeState.InputRequired);
        var node = shell.NodeModel("Say hi");
        Assert.True(node.ShowsStateDetail);

        node.OpenConversationCommand.Execute(null);
        shell.Render();

        Assert.Empty(node.Actions);
        Assert.False(node.ShowsStateDetail);
    }

    [AvaloniaFact]
    public void The_input_lists_each_predecessor_and_a_click_selects_it()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Build")));

        var link = shell.InView<Button>("InputLink");
        Assert.Equal("Design", AutomationProperties.GetName(link));
        Assert.False(shell.Find<TextBlock>("NoInput").IsEffectivelyVisible);
        Assert.False(shell.ShowsAny("OutputLink"));

        shell.Click(link);

        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);
        Assert.True(shell.Find<TextBlock>("NoInput").IsEffectivelyVisible);
        Assert.Equal("Build", AutomationProperties.GetName(shell.InView<Button>("OutputLink")));
    }

    [AvaloniaFact]
    public void A_context_connection_shows_a_dashed_link_and_a_new_kind_shows_at_once()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Build")));
        var node = shell.NodeModel("Build");
        Assert.False(Assert.Single(node.Inputs).IsContext);

        shell.Window.ViewModel.Canvas!.Connections.Single().SetKindCommand.Execute(ConnectionKind.Context);
        shell.Render();

        Assert.True(Assert.Single(node.Inputs).IsContext);
        Assert.True(shell.Find<Control>("ContextLink").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_removed_connection_leaves_the_section_at_once()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Build")));

        shell.Window.ViewModel.Canvas!.Connections.Single().DeleteCommand.Execute(null);
        shell.Render();

        Assert.False(shell.ShowsAny("InputLink"));
        Assert.True(shell.Find<TextBlock>("NoInput").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Enter_in_the_filter_moves_to_the_first_match_and_Escape_clears_it()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        shell.FilterInspector("model");

        shell.Press(Key.Enter);

        Assert.Same(shell.Find<ComboBox>("TaskModel"), shell.Window.FocusManager!.GetFocusedElement());

        shell.Click(shell.Find<TextBox>("InspectorFilter"));
        shell.Press(Key.Escape);

        Assert.Equal("", shell.Find<TextBox>("InspectorFilter").Text);
    }

    [AvaloniaFact]
    public void Left_and_Right_fold_and_open_a_section_from_its_header()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        var header = shell.Section("Agent").GetVisualDescendants().OfType<Button>().First(button => button.Name == "PART_Header");
        header.Focus();

        shell.Press(Key.Left);
        Assert.True(shell.Section("Agent").IsFolded);

        shell.Press(Key.Right);
        Assert.False(shell.Section("Agent").IsFolded);
    }

    [AvaloniaFact]
    public void Tab_reaches_the_first_action_right_after_the_filter()
    {
        // No client is installed here, so Run is off, and the first action that takes the focus is the conversation.
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Find<TextBox>("InspectorFilter").Focus();

        shell.Press(Key.Tab);

        Assert.Same(shell.Find<Button>("OpenConversation"), shell.Window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void F2_in_the_inspector_edits_the_title()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Find<TextBox>("InspectorFilter").Focus();

        shell.Press(Key.F2);

        Assert.Same(shell.Find<TextBox>("TaskTitle"), shell.Window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void An_empty_box_says_what_goes_in_it()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("Add acceptance criteria", shell.Find<TextBox>("TaskAcceptanceCriteria").Watermark);
    }
}
