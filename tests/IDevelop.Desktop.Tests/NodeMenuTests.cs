using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>The menu a right-click on a card opens, for the selection it acts on.</summary>
[Collection(ProcessCollection.Name)]
public sealed class NodeMenuTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Check = TestTasks.Review;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private string DesignThenBuild(params WorkflowEdit[] more) => _temp.Seed(
        [
            TaskAt(Design, "Design", 105, 90, Codex, "Write the parser."),
            TaskAt(Build, "Build", 505, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            .. more,
        ]);

    private static Workflow Workflow(Shell shell) => shell.Window.ViewModel.Canvas!.Workflow;

    [AvaloniaFact]
    public void One_node_offers_every_action_in_order_with_icons_and_hints_and_delete_last_in_red()
    {
        var shell = Shell.Open(DesignThenBuild());

        shell.RightClick(shell.Header(shell.Node("Design")));

        Assert.Equal(["Design"], shell.Window.ViewModel.Canvas!.SelectedNodes.Select(node => node.Title));
        Assert.Equal(
            ["Run", "Rename", "Duplicate", "Replace With", "Disconnect", "Derive Blueprint…", "Save as Blueprint…", "Delete"],
            shell.MenuHeaders());
        Assert.All(shell.OpenMenuItems(), item => Assert.IsType<PathIcon>(item.Icon));
        Assert.Equal(
            ["Ctrl+Enter", "F2", "Ctrl+D", "", "", "", "", "Del"],
            shell.OpenMenuItems().Select(Shell.MenuHint));
        var delete = shell.MenuItem("NodeMenuDelete");
        Assert.Same(delete, shell.OpenMenuItems().Last());
        Assert.Contains("destructive", delete.Classes);

        shell.Click(delete);

        Assert.Equal(["Build"], Workflow(shell).Tasks.Values.Select(task => task.Title));
        Assert.Empty(Workflow(shell).Connections);
    }

    [AvaloniaFact]
    public void Two_selected_nodes_offer_only_what_acts_on_a_selection()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.Press(Key.A, RawInputModifiers.Control);
        shell.Click(shell.Header(shell.Node("Design")), RawInputModifiers.Shift);
        shell.Click(shell.Header(shell.Node("Build")), RawInputModifiers.Shift);
        Assert.Equal(2, shell.Window.ViewModel.Canvas!.SelectedNodes.Count);

        shell.RightClick(shell.Header(shell.Node("Build")));

        Assert.Equal(2, shell.Window.ViewModel.Canvas!.SelectedNodes.Count);
        Assert.Equal(["Duplicate", "Disconnect", "Delete"], shell.MenuHeaders());
    }

    [AvaloniaFact]
    public void An_approval_offers_no_run_and_a_lone_node_offers_no_disconnect()
    {
        var shell = Shell.Open(_temp.Seed(new WorkflowEdit.PlaceNode(Check, BuiltInBlueprints.Approval, new CanvasPoint(105, 90)) { Title = "Sign off" }));

        shell.RightClick(shell.Header(shell.Node("Sign off")));

        Assert.Equal(["Rename", "Duplicate", "Replace With", "Derive Blueprint…", "Save as Blueprint…", "Delete"], shell.MenuHeaders());
    }

    [AvaloniaFact]
    public void A_node_that_ran_offers_no_replace_with()
    {
        var fakes = new FakeClients(_temp.Create("bin"));
        FakeAgents.Install(fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(DesignThenBuild(), fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Id == Design).State == NodeState.Succeeded, "the run succeeds");

        shell.RightClick(shell.Header(shell.Node("Design")));

        Assert.Equal(["Run", "Rename", "Duplicate", "Disconnect", "Derive Blueprint…", "Save as Blueprint…", "Delete"], shell.MenuHeaders());
    }

    [AvaloniaFact]
    public void Duplicate_copies_the_selection_and_the_connections_among_it_below_the_originals_and_selects_the_copies()
    {
        var shell = Shell.Open(DesignThenBuild(
            TaskAt(Check, "Check", 905, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Build, Check), ConnectionKind.Context)));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Header(shell.Node("Build")), RawInputModifiers.Shift);

        shell.RightClick(shell.Header(shell.Node("Build")));
        shell.Click(shell.MenuItem("NodeMenuDuplicate"));

        var workflow = Workflow(shell);
        var designCopy = workflow.Tasks.Values.Single(task => task.Title == "Design copy");
        var buildCopy = workflow.Tasks.Values.Single(task => task.Title == "Build copy");
        Assert.Equal(5, workflow.Tasks.Count);
        Assert.Equal(("Write the parser.", Codex), (designCopy.Field("instructions"), designCopy.Execution));
        Assert.Equal((new CanvasPoint(105, 184), new CanvasPoint(505, 184)), (workflow.Positions[designCopy.Id], workflow.Positions[buildCopy.Id]));
        Assert.Equal(ConnectionKind.Dependency, workflow.Connections[new ConnectionKey(designCopy.Id, buildCopy.Id)]);
        Assert.Equal(3, workflow.Connections.Count);
        Assert.Equal(["Build copy", "Design copy"], shell.Window.ViewModel.Canvas!.SelectedNodes.Select(node => node.Title).Order());
    }

    [AvaloniaFact]
    public void Duplicate_puts_the_copy_at_the_first_free_spot_below_its_original()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90, Codex),
            TaskAt(Build, "Build", 105, 184),
            TaskAt(Check, "Check", 305, 278)));

        shell.RightClick(shell.Header(shell.Node("Design")));
        shell.Click(shell.MenuItem("NodeMenuDuplicate"));

        var copy = Workflow(shell).Tasks.Values.Single(task => task.Title == "Design copy");
        Assert.Equal(new CanvasPoint(105, 372), Workflow(shell).Positions[copy.Id]);
        Assert.All(
            ["Design", "Build", "Check"],
            title => Assert.False(shell.CardRect("Design copy").Intersects(shell.CardRect(title)), $"The copy covers {title}."));
    }

    [AvaloniaFact]
    public void A_node_without_an_agent_offers_choose_agent_in_place_of_run_which_opens_its_client_picker()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.Click(shell.Header(shell.Node("Design")));

        shell.RightClick(shell.Header(shell.Node("Build")));

        Assert.Equal(
            ["Choose Agent…", "Rename", "Duplicate", "Replace With", "Disconnect", "Derive Blueprint…", "Save as Blueprint…", "Delete"],
            shell.MenuHeaders());
        Assert.Same(Application.Current!.FindResource("IconAgent"), ((PathIcon)shell.MenuItem("NodeMenuChooseAgent").Icon!).Data);

        shell.Click(shell.MenuItem("NodeMenuChooseAgent"));

        var picker = shell.Find<ComboBox>("TaskClient");
        var focused = Assert.IsType<ComboBoxItem>(shell.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(
            ("Build", "Build", true, "None"),
            (shell.Window.ViewModel.Canvas!.SelectedNode?.Title, ((TaskNodeViewModel)picker.DataContext!).Title, picker.IsDropDownOpen,
                ((ClientChoice)focused.DataContext!).Label));
        Assert.Same(picker, focused.FindLogicalAncestorOfType<ComboBox>());

        shell.Press(Key.Down);
        shell.Press(Key.Enter);

        Assert.Equal(ClientId.ClaudeCode, Workflow(shell).Tasks[Build].Execution?.Client);
        shell.RightClick(shell.Header(shell.Node("Build")));
        Assert.Equal("Run", shell.MenuHeaders()[0]);
    }

    [AvaloniaFact]
    public void Choose_agent_unfolds_the_agent_section_and_clears_a_filter_that_hides_the_picker()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.Click(shell.Header(shell.Node("Build")));
        shell.Fold("Agent");
        shell.FilterInspector("acc");
        Assert.False(shell.Find<ComboBox>("TaskClient").IsEffectivelyVisible);

        shell.RightClick(shell.Header(shell.Node("Build")));
        shell.Click(shell.MenuItem("NodeMenuChooseAgent"));

        var picker = shell.Find<ComboBox>("TaskClient");
        Assert.Equal(
            (true, true, false, ""),
            (picker.IsEffectivelyVisible, picker.IsDropDownOpen, shell.Section("Agent").IsFolded, shell.Find<TextBox>("InspectorFilter").Text));
    }

    [AvaloniaFact]
    public void Choose_agent_waits_while_the_blueprint_editor_holds_the_inspector()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.RightClick(shell.Header(shell.Node("Build")));
        shell.Click(shell.MenuItem("NodeMenuSaveAs"));
        Assert.True(shell.Find<StackPanel>("BlueprintEditor").IsEffectivelyVisible);

        shell.RightClick(shell.Header(shell.Node("Build")));
        var whileEditing = shell.MenuHeaders();
        shell.Press(Key.Escape);
        shell.Click(shell.InView<Button>("CancelBlueprint"));
        shell.RightClick(shell.Header(shell.Node("Build")));

        Assert.Equal(["Rename", "Duplicate", "Replace With", "Disconnect", "Derive Blueprint…", "Save as Blueprint…", "Delete"], whileEditing);
        Assert.Equal("Choose Agent…", shell.MenuHeaders()[0]);
    }

    [AvaloniaFact]
    public void Choose_agent_opens_the_client_list_against_the_picker_once_it_is_scrolled_into_view()
    {
        var shell = Shell.Open(DesignThenBuild());
        shell.Window.Height = shell.Window.MinHeight;
        shell.Click(shell.Header(shell.Node("Build")));
        var viewport = shell.Find<ComboBox>("TaskClient").FindAncestorOfType<ScrollViewer>()!;
        viewport.ScrollToEnd();
        shell.Render();
        Assert.False(shell.Bounds(viewport).Contains(shell.Bounds(shell.Find<ComboBox>("TaskClient"))), "The picker is already in view.");
        Rect? placedAgainst = null;

        // A window places the list against where the picker was last laid out. A headless list is an overlay that lays the
        // window out before placing it, so the test reads the picker's laid-out place as the list opens.
        using (ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>((picker, e) =>
        {
            if (AutomationProperties.GetAutomationId(picker) == "TaskClient" && e.NewValue is true)
            {
                placedAgainst = shell.Bounds(picker);
            }
        }))
        {
            shell.RightClick(shell.Header(shell.Node("Build")));
            shell.Click(shell.MenuItem("NodeMenuChooseAgent"));
        }

        Assert.True(shell.Find<ComboBox>("TaskClient").IsDropDownOpen);
        Assert.True(
            placedAgainst is { } picker && shell.Bounds(viewport).Contains(picker),
            $"The list opened against the picker at {placedAgainst}, outside the inspector's view {shell.Bounds(viewport)}.");
    }

    [AvaloniaFact]
    public void Replace_with_keeps_the_title_the_matching_fields_the_agent_and_the_connections()
    {
        var folder = DesignThenBuild(
            TaskAt(Check, "Check", 105, 400),
            new WorkflowEdit.Connect(new ConnectionKey(Check, Design), ConnectionKind.Context));
        var careful = new Blueprint(
            new BlueprintKey("careful-0a1b2c3d", 1), "Careful implement",
            new WorkSpec.Agent(AgentAccess.Edit, Proposes: false, PromptTemplate.Parse("{{instructions}} {{risks}}")),
            [new FieldSpec("instructions", "Instructions", FieldShape.Text, Required: true, ""), new FieldSpec("risks", "Risks", FieldShape.Text, Required: false, "")],
            new NodeSettings(null, ConversationMode.Autonomous));
        BlueprintLibrary.Project(folder).Save(careful);
        var shell = Shell.Open(folder);

        shell.RightClick(shell.Header(shell.Node("Design")));
        shell.Click(shell.MenuItem("NodeMenuReplace"));
        var choices = shell.Window.GetVisualDescendants().OfType<MenuItem>()
            .Where(item => Avalonia.Automation.AutomationProperties.GetAutomationId(item) == "ReplaceWithItem").ToList();
        Assert.Equal(
            ["Replace with Implement", "Replace with Plan", "Replace with Architect", "Replace with Review", "Replace with Approval", "Replace with Careful implement"],
            choices.Select(Avalonia.Automation.AutomationProperties.GetName));
        Assert.All(choices, item =>
        {
            var tile = Assert.IsType<KindTile>(item.Icon);
            var drawn = tile.TranslatePoint(new Point(tile.Bounds.Width, tile.Bounds.Height), shell.Window)!.Value - tile.TranslatePoint(default, shell.Window)!.Value;
            Assert.Equal(new Vector(18, 18), drawn);
        });
        shell.Click(choices.Last());

        var workflow = Workflow(shell);
        var replaced = workflow.Tasks.Values.Single(task => task.Title == "Design");
        Assert.NotEqual(Design, replaced.Id);
        Assert.Equal(careful.Key, replaced.Blueprint.Key);
        Assert.Equal(("Write the parser.", "", Codex), (replaced.Field("instructions"), replaced.Field("risks"), replaced.Execution));
        Assert.Equal(new CanvasPoint(105, 90), workflow.Positions[replaced.Id]);
        Assert.Equal(ConnectionKind.Dependency, workflow.Connections[new ConnectionKey(replaced.Id, Build)]);
        Assert.Equal(ConnectionKind.Context, workflow.Connections[new ConnectionKey(Check, replaced.Id)]);
        Assert.Equal(2, workflow.Connections.Count);
    }

    [AvaloniaFact]
    public void Replace_with_a_review_that_would_take_a_second_subject_changes_nothing()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            new WorkflowEdit.PlaceNode(Build, BuiltInBlueprints.Plan, new CanvasPoint(105, 400)) { Title = "Outline" },
            new WorkflowEdit.PlaceNode(Check, BuiltInBlueprints.Review, new CanvasPoint(505, 90)) { Title = "Check" },
            new WorkflowEdit.Connect(new ConnectionKey(Design, Check), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(Build, Check), ConnectionKind.Dependency)));
        var before = Workflow(shell);

        shell.RightClick(shell.Header(shell.Node("Outline")));
        shell.Click(shell.MenuItem("NodeMenuReplace"));
        shell.Click(shell.Window.GetVisualDescendants().OfType<MenuItem>()
            .Single(item => Avalonia.Automation.AutomationProperties.GetName(item) == "Replace with Implement"));

        Assert.Same(before, Workflow(shell));
        Assert.Equal("\"Check\" already reviews \"Design\". A review takes one task that edits the project.", shell.Status);
    }

    [AvaloniaFact]
    public void Disconnect_deletes_every_connection_of_the_selection_in_one_edit()
    {
        var shell = Shell.Open(DesignThenBuild(
            TaskAt(Check, "Check", 905, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Build, Check), ConnectionKind.Context)));

        shell.RightClick(shell.Header(shell.Node("Build")));
        shell.Click(shell.MenuItem("NodeMenuDisconnect"));

        Assert.Empty(Workflow(shell).Connections);
        Assert.Equal(3, Workflow(shell).Tasks.Count);
    }

    [AvaloniaFact]
    public void Rename_opens_the_title_on_the_card_with_its_text_selected()
    {
        var shell = Shell.Open(DesignThenBuild());

        shell.RightClick(shell.Header(shell.Node("Build")));
        shell.Click(shell.MenuItem("NodeMenuRename"));

        var title = shell.InCard<TextBox>("Build", "CardTitleBox");
        Assert.True(title.IsFocused);
        Assert.Equal("Build", title.SelectedText);
    }
}
