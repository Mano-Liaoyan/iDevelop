using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Inspector;
using IDevelop.Desktop.Theme;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The Agent panel's pages, its tab strip, its keys, and the pages for what else can be selected.</summary>
[Collection(ProcessCollection.Name)]
public sealed class AgentPanelTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private Shell Linked() => Shell.Open(_temp.Seed(
        TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90, Codex),
        new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));

    private static AgentPanelViewModel Panel(Shell shell) => shell.Window.ViewModel.AgentPanel;

    [AvaloniaTheory]
    [InlineData(Key.D1, AgentPage.Chat, "ChatPage")]
    [InlineData(Key.D2, AgentPage.Agent, "AgentPage")]
    [InlineData(Key.D3, AgentPage.Task, "TaskPage")]
    [InlineData(Key.D4, AgentPage.Input, "InputPage")]
    [InlineData(Key.D5, AgentPage.Output, "OutputPage")]
    public void Alt_and_a_digit_opens_the_page(Key key, AgentPage page, string pageId)
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Press(key, Avalonia.Input.RawInputModifiers.Alt);

        Assert.Equal(page, Panel(shell).Page);
        Assert.True(shell.Find<Control>(pageId).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_tabs_keep_one_order_for_a_node_and_the_conditional_tab_comes_last()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));

        var tabs = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<PageTab>().Where(tab => tab.IsVisible).Select(tab => tab.Label);

        Assert.Equal(["Chat", "Agent", "Task", "Input", "Output"], tabs);
    }

    [AvaloniaFact]
    public void Alt_6_does_nothing_without_a_review_or_a_proposal_and_its_tab_stays_away()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        var page = Panel(shell).Page;

        shell.Press(Key.D6, Avalonia.Input.RawInputModifiers.Alt);

        Assert.Equal(page, Panel(shell).Page);
        Assert.False(shell.Find<Button>("PageExtra").IsVisible);
    }

    [AvaloniaFact]
    public void A_tab_opens_its_page_and_the_active_tab_shows_it()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.Find<PageTab>("PageTask"));

        Assert.True(Panel(shell).IsTask);
        Assert.Contains("active", shell.Find<PageTab>("PageTask").Classes);
        Assert.DoesNotContain("active", shell.Find<PageTab>("PageAgent").Classes);
        Assert.Equal(52, shell.Find<PageTab>("PageTask").Bounds.Width);
        Assert.Equal(48, shell.Find<PageTab>("PageTask").Bounds.Height);
    }

    [AvaloniaFact]
    public void Selecting_another_node_keeps_the_page_and_the_workflow_page_has_no_tabs()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<PageTab>("PageInput"));

        shell.Click(shell.Header(shell.Node("Build")));

        Assert.True(Panel(shell).IsInput);
        Assert.True(shell.Find<Control>("InputPage").IsEffectivelyVisible);

        shell.Window.ViewModel.Canvas!.SelectedNode = null;
        shell.Render();
        Assert.False(shell.Has<PageTab>("PageTask"));
        Assert.True(shell.Find<Control>("WorkflowPage").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_panel_returns_to_the_last_page_of_a_kind_of_node()
    {
        var shell = Shell.Open(_temp.Seed(
            new WorkflowEdit.PlaceNode(Design, BuiltInBlueprints.Implement, new CanvasPoint(105, 90)) { Title = "Build it" },
            new WorkflowEdit.PlaceNode(Build, BuiltInBlueprints.Approval, new CanvasPoint(465, 90)) { Title = "Check it" }));
        shell.Click(shell.Header(shell.Node("Build it")));
        shell.Click(shell.Find<PageTab>("PageInput"));
        shell.Window.ViewModel.Canvas!.SelectedNode = null;
        shell.Render();

        shell.Click(shell.Header(shell.Node("Check it")));
        shell.Click(shell.Find<PageTab>("PageOutput"));
        shell.Window.ViewModel.Canvas!.SelectedNode = null;
        shell.Render();

        shell.Click(shell.Header(shell.Node("Build it")));
        Assert.True(Panel(shell).IsInput);
    }

    [AvaloniaFact]
    public void An_approval_node_has_no_Agent_page_and_falls_back_to_Chat()
    {
        var shell = Shell.Open(_temp.Seed(new WorkflowEdit.PlaceNode(Build, BuiltInBlueprints.Approval, new CanvasPoint(465, 90)) { Title = "Check it" }));

        shell.Click(shell.Header(shell.Node("Check it")));

        Assert.True(Panel(shell).IsChat);
        Assert.False(shell.Find<PageTab>("PageAgent").IsVisible);
    }

    [AvaloniaFact]
    public void The_header_is_56_px_and_Close_closes_the_panel_and_Ctrl_Alt_B_brings_it_back()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal(56, shell.Bounds(shell.Find<Control>("InspectorHeader").FindAncestorOfType<Border>()!).Height, 0.5);

        shell.Click(shell.Find<Button>("ClosePanel"));
        Assert.False(shell.Window.ViewModel.IsInspectorShown);

        shell.Window.ViewModel.ToggleInspectorCommand.Execute(null);
        shell.Render();
        Assert.True(shell.Find<Control>("Inspector").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void An_override_shows_a_dot_that_a_hover_makes_a_reset_with_the_default_named_in_its_tip()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5" })));
        shell.Click(shell.Header(shell.Node("Design")));
        var node = shell.NodeModel("Design");
        node.SetField("instructions", "Changed.");
        shell.Page(AgentPage.Task);
        var revert = shell.Find<Button>("RevertInstructions");

        Assert.True(revert.IsEffectivelyVisible);
        Assert.Equal("Reset to blueprint default: empty", ToolTip.GetTip(revert));
        Assert.True(node.TaskHasChanges);
        Assert.Contains("changed", shell.Find<PageTab>("PageTask").Classes);
        Assert.True(revert.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().IsVisible);

        shell.Window.MouseMove(shell.Center(revert));
        shell.Render();

        Assert.False(revert.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().IsVisible);
        Assert.True(revert.GetVisualDescendants().OfType<PathIcon>().Single().IsVisible);
    }

    [AvaloniaFact]
    public void The_Task_page_has_one_fold_panel_per_field_and_the_blueprint_folded()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Page(AgentPage.Task);

        var keys = shell.Find<Control>("TaskPage").GetVisualDescendants().OfType<InspectorSection>().Select(section => section.Key).ToArray();

        Assert.Equal(["TaskInstructions", "TaskAcceptanceCriteria", "Blueprint"], keys);
        Assert.True(shell.Section("Blueprint").IsFolded);
        Assert.False(shell.Section("TaskInstructions").IsFolded);
    }

    [AvaloniaFact]
    public void Every_field_of_the_panel_stays_editable_on_its_page()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));

        foreach (var (page, id) in new[] { (AgentPage.Agent, "TaskClient"), (AgentPage.Agent, "TaskModel"), (AgentPage.Agent, "TaskConversation"), (AgentPage.Task, "TaskInstructions"), (AgentPage.Task, "TaskAcceptanceCriteria") })
        {
            shell.Page(page);
            var editor = shell.Find<Control>(id);
            Assert.True(editor.IsEffectivelyVisible, id);
            Assert.True(editor.IsEffectivelyEnabled, id);
        }

        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Write it.");
        Assert.Equal("Write it.", shell.NodeModel("Design").FieldText("instructions"));
    }

    [AvaloniaFact]
    public void An_inputs_kind_changes_on_its_two_segments()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Build")));
        shell.Page(AgentPage.Input);

        shell.Click(shell.Find<Button>("LinkContext"));

        Assert.Equal(ConnectionKind.Context, shell.Window.ViewModel.Canvas!.Connections.Single().Kind);
        Assert.True(shell.Find<Control>("ContextLink").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_connections_page_has_no_tabs_and_names_both_ends()
    {
        var shell = Linked();

        shell.Click(shell.ConnectionInto("Build"));

        Assert.False(shell.Has<PageTab>("PageChat"));
        Assert.Equal(["Connection", "Dependency"], Shell.Texts(shell.Find<Control>("InspectorHeader")));
        Assert.Equal(["Design", "Result socket"], Shell.Texts(shell.Find<Button>("ConnectionFrom")));
        Assert.Equal(["Build", "Input socket"], Shell.Texts(shell.Find<Button>("ConnectionTo")));
    }

    [AvaloniaFact]
    public void Several_selected_nodes_show_their_count_and_Delete()
    {
        var shell = Linked();
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.SelectedNodes.Add(canvas.Nodes[0]);
        canvas.SelectedNodes.Add(canvas.Nodes[1]);
        shell.Render();

        Assert.Equal("2 agents selected", shell.Find<TextBlock>("SeveralSelected").Text);

        shell.Click(shell.Find<Button>("DeleteSelection"));

        Assert.Empty(canvas.Workflow.Tasks);
    }

    [AvaloniaFact]
    public void The_workflow_page_names_the_workflow_and_counts_its_kinds()
    {
        var shell = Linked();

        Assert.Equal(shell.Window.ViewModel.Canvas!.Name, shell.Find<TextBlock>("WorkflowTitle").Text);
        Assert.Equal(["Implement"], Shell.Texts(shell.Find<ItemsControl>("Kinds")).Take(1));
        Assert.Equal(2, shell.Window.ViewModel.Canvas!.Overview.Kinds.Single(kind => kind.Kind == NodeKind.Implement).Count);
    }

    [AvaloniaFact]
    public void The_conditional_tab_and_page_follow_a_nodes_findings()
    {
        var shell = Linked();
        shell.Click(shell.Header(shell.Node("Design")));
        var node = shell.NodeModel("Design");

        Assert.False(node.HasExtraPage);
        Assert.Equal("Findings", node.ExtraTitle);
        Assert.Equal("IconChecklist", node.ExtraGlyph);
        Assert.Null(node.ExtraBadge);
    }
}
