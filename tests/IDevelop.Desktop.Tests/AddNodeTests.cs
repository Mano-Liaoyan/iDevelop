using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Canvas.WorkflowCanvasViewModel;

namespace IDevelop.Desktop.Tests;

/// <summary>The Add popover, opened from every entry point of the real main window.</summary>
public sealed class AddNodeTests : IDisposable
{
    private static readonly string[] BuiltInRows = ["Add Implement", "Add Plan", "Add Architect", "Add Review", "Add Approval"];

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static NodeKind TileKind(Visual row) => row.GetVisualDescendants().OfType<KindTile>().Single().Kind;

    /// <summary>The popover's box in the window as it is drawn, so a zoom that scaled it would show.</summary>
    private static Rect Drawn(Shell shell, Visual visual) => new(
        visual.TranslatePoint(default, shell.Window)!.Value,
        visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), shell.Window)!.Value);

    private static Blueprint BugFix() => new(
        new BlueprintKey("bug-fix-0a1b2c3d", 1), "Bug fix",
        new WorkSpec.Agent(AgentAccess.Edit, Proposes: false, PromptTemplate.Parse("Fix {{bug}}.")),
        [new FieldSpec("bug", "Bug", FieldShape.Text, Required: true, "")],
        new NodeSettings(null, ConversationMode.Autonomous))
    {
        Description = "Fixes one reported bug.",
    };

    [AvaloniaFact]
    public void Right_clicking_empty_canvas_lists_the_five_built_ins_with_their_tiles_then_the_canvas_actions()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));

        shell.RightClick(shell.InEditor(400, 300));

        Assert.Equal([.. BuiltInRows, "Generate Workflow…", "Select All", "Fit to View", "Zoom to 100%"], shell.AddRows());
        Assert.Equal(
            [NodeKind.Implement, NodeKind.Plan, NodeKind.Architect, NodeKind.Review, NodeKind.Approval],
            BuiltInRows.Select(row => TileKind(shell.AddRow(row[4..]))));
        var tile = shell.AddRow("Plan").GetVisualDescendants().OfType<KindTile>().Single();
        Assert.Equal(Color.Parse("#007EAE"), shell.ColorAt(tile, new Point(3, 12)));
        Assert.Equal(["Built-in", "Actions"], Shell.Texts(shell.AddPopover).Where(text => text is "Built-in" or "Project" or "Personal" or "Actions"));
        Assert.Equal("Add Implement", shell.Highlighted());
        Assert.Equal("Carries out its instructions with the agent you choose, and may edit the project.", shell.Find<TextBlock>("AddNodeDescription").Text);
        Assert.True(shell.Find<TextBox>("AddNodeSearch").IsFocused);
        Assert.False(shell.ShowsAny("AddNodeProblems"));
    }

    [AvaloniaFact]
    public void Typing_rev_puts_review_first_and_enter_places_a_review_centered_on_the_click()
    {
        var folder = _temp.Create("plan");
        var shell = Shell.Open(folder);
        var click = shell.InEditor(400, 300);

        shell.RightClick(click);
        shell.Type("rev");

        Assert.Equal("Add Review", shell.AddRows()[0]);
        Assert.Equal("Add Review", shell.Highlighted());
        shell.Press(Key.Enter);

        Assert.False(shell.AddPopoverIsOpen);
        var at = shell.CanvasPointAt(click);
        var task = Assert.Single(shell.Window.ViewModel.Canvas!.Workflow.Tasks.Values);
        Assert.Equal((BuiltInBlueprints.Review.Key, "New task"), (task.Blueprint.Key, task.Title));
        Assert.Equal(new CanvasPoint(at.X - TaskCardWidth / 2, at.Y - TaskCardHeight / 2), shell.Window.ViewModel.Canvas!.Workflow.Positions[task.Id]);
        Assert.Equal("New task", shell.Find<TextBox>("TaskTitle").Text);
        Assert.Equal("plan* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void A_search_ranks_name_prefixes_then_word_prefixes_then_letters_in_order_then_descriptions()
    {
        var folder = _temp.Create("plan");
        BlueprintLibrary.Project(folder).Save(BugFix());
        var shell = Shell.Open(folder);
        shell.RightClick(shell.InEditor(400, 300));

        shell.Type("fix");
        Assert.Equal(["Add Bug fix"], shell.AddRows());

        shell.Find<TextBox>("AddNodeSearch").Text = "bfx";
        shell.Render();
        Assert.Equal(["Add Bug fix"], shell.AddRows());

        shell.Find<TextBox>("AddNodeSearch").Text = "pro";
        shell.Render();
        Assert.Equal(["Add Approval", "Add Implement", "Add Plan", "Add Architect"], shell.AddRows());

        shell.Find<TextBox>("AddNodeSearch").Text = "zoom";
        shell.Render();
        Assert.Equal(["Zoom to 100%"], shell.AddRows());
    }

    [AvaloniaFact]
    public void Double_click_and_n_on_empty_canvas_open_the_popover_at_the_pointer()
    {
        var shell = Shell.Open(_temp.Create("plan"));

        var doubleClick = shell.InEditor(300, 250);
        shell.DoubleClick(doubleClick);
        Assert.Equal(BuiltInRows, shell.AddRows().Take(5));
        Assert.True(Point.Distance(doubleClick, shell.Bounds(shell.AddPopover).TopLeft) <= 2, $"{shell.Bounds(shell.AddPopover)} is not at {doubleClick}");
        shell.Press(Key.Escape);
        Assert.False(shell.AddPopoverIsOpen);

        var pointer = shell.InEditor(200, 150);
        shell.Click(pointer);
        shell.PressWithText(Key.N, "n");

        Assert.Equal(BuiltInRows, shell.AddRows().Take(5));
        Assert.True(Point.Distance(pointer, shell.Bounds(shell.AddPopover).TopLeft) <= 2, $"{shell.Bounds(shell.AddPopover)} is not at {pointer}");
        Assert.Equal("", shell.Find<TextBox>("AddNodeSearch").Text);
        Assert.True(shell.Find<TextBox>("AddNodeSearch").IsFocused);
    }

    [AvaloniaFact]
    public void A_library_blueprint_appears_under_project_with_the_badge_and_offers_edit()
    {
        var folder = _temp.Create("plan");
        BlueprintLibrary.Project(folder).Save(BugFix());
        var shell = Shell.Open(folder);

        shell.RightClick(shell.InEditor(400, 300));

        Assert.Equal([.. BuiltInRows, "Add Bug fix"], shell.AddRows().Take(6));
        Assert.Equal(["Built-in", "Project", "Actions"], Shell.Texts(shell.AddPopover).Where(text => text is "Built-in" or "Project" or "Personal" or "Actions"));
        var tile = shell.AddRow("Bug fix").GetVisualDescendants().OfType<KindTile>().Single();
        Assert.Equal((NodeKind.Implement, true), (tile.Kind, tile.IsLibrary));
        Assert.Contains(":library", tile.Classes);
        Assert.DoesNotContain(":library", shell.AddRow("Implement").GetVisualDescendants().OfType<KindTile>().Single().Classes);

        foreach (var _ in shell.AddRows().SkipWhile(row => row != "Add Bug fix"))
        {
            shell.Press(Key.Up);
        }

        Assert.Equal("Add Bug fix", shell.Highlighted());
        Assert.Equal("Fixes one reported bug.", shell.Find<TextBlock>("AddNodeDescription").Text);
        Assert.Equal("Edit Bug fix", Avalonia.Automation.AutomationProperties.GetName(shell.Shown<Button>("AddNodeEdit")));

        shell.Click(shell.Shown<Button>("AddNodeEdit"));

        Assert.False(shell.AddPopoverIsOpen);
        Assert.Equal("Bug fix", shell.Find<TextBox>("BlueprintName").Text);
    }

    [AvaloniaFact]
    public void Derive_on_a_row_closes_the_popover_and_opens_the_blueprint_editor()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        shell.RightClick(shell.InEditor(400, 300));
        shell.Press(Key.Down);
        Assert.Equal("Add Plan", shell.Highlighted());
        Assert.Equal("Derive from Plan", Avalonia.Automation.AutomationProperties.GetName(shell.Shown<Button>("AddNodeDerive")));
        Assert.False(shell.ShowsAny("AddNodeEdit"));

        shell.Click(shell.Shown<Button>("AddNodeDerive"));

        Assert.False(shell.AddPopoverIsOpen);
        Assert.Equal("Derived from Plan, version 1. Saving makes version 1.", shell.Find<TextBlock>("BlueprintCaption").Text);
        Assert.Empty(shell.Nodes());
    }

    [AvaloniaFact]
    public void Escape_closes_the_popover_without_an_edit()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        shell.RightClick(shell.InEditor(400, 300));
        Assert.True(shell.AddPopoverIsOpen);

        shell.Press(Key.Escape);

        Assert.False(shell.AddPopoverIsOpen);
        Assert.Empty(shell.Nodes());
        Assert.Equal("plan - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void A_click_outside_closes_the_popover_without_an_edit()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        shell.RightClick(shell.InEditor(400, 300));

        shell.Click(shell.InEditor(100, 600));

        Assert.False(shell.AddPopoverIsOpen);
        Assert.Empty(shell.Nodes());
    }

    [AvaloniaFact]
    public void The_popover_keeps_its_size_at_any_zoom()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        Size SizeAt(double zoom)
        {
            shell.Editor.ViewportZoom = zoom;
            shell.Render();
            shell.RightClick(shell.InEditor(300, 200));
            var size = Drawn(shell, shell.AddPopover).Size;
            shell.Press(Key.Escape);
            return size;
        }

        var normal = SizeAt(1);

        Assert.Equal(300, normal.Width);
        Assert.Equal(normal, SizeAt(0.5));
        Assert.Equal(normal, SizeAt(2));
    }

    [AvaloniaFact]
    public void Opened_10_px_from_the_canvas_corner_the_popover_lies_inside_the_canvas()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var canvas = shell.Bounds(shell.Editor);

        shell.RightClick(canvas.BottomRight - new Vector(10, 10));

        var popover = shell.Bounds(shell.AddPopover);
        Assert.True(canvas.Contains(popover), $"{popover} is not inside {canvas}");
    }

    [AvaloniaFact]
    public void The_sidebar_button_opens_the_popover_under_itself_without_actions_and_places_the_node_in_view()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var button = shell.Bounds(shell.Find<Button>("AddTask"));

        shell.Click(shell.Find<Button>("AddTask"));

        var popover = shell.Bounds(shell.AddPopover);
        Assert.True(Math.Abs(popover.X - button.X) <= 2 && popover.Y >= button.Bottom && popover.Y - button.Bottom <= 8, $"{popover} is not under {button}");
        Assert.Equal(BuiltInRows, shell.AddRows());
        shell.Click(shell.AddRow("Plan"));

        var task = Assert.Single(shell.Window.ViewModel.Canvas!.Workflow.Tasks.Values);
        Assert.Equal(BuiltInBlueprints.Plan.Key, task.Blueprint.Key);
        Assert.Equal(new Point(60, 60), shell.Node("New task").Location);
    }

    [AvaloniaFact]
    public void Library_files_that_did_not_read_show_at_the_popover_foot()
    {
        var folder = _temp.Create("plan");
        var library = BlueprintLibrary.Project(folder).Folder;
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "broken.json"), "{");
        var shell = Shell.Open(folder);

        shell.RightClick(shell.InEditor(400, 300));

        Assert.True(shell.Find<TextBlock>("AddNodeProblems").IsEffectivelyVisible);
        Assert.Equal("1 library file did not read", shell.Find<TextBlock>("AddNodeProblems").Text);
    }

    [AvaloniaFact]
    public void A_wire_dropped_low_on_the_canvas_opens_the_popover_below_the_drop_when_its_list_fits_there()
    {
        var shell = Shell.Open(_temp.Seed(AppTempFolder.TaskAt(TestTasks.Design, "Design", 105, 90)));
        var canvas = shell.Bounds(shell.Editor);
        shell.Drag(shell.Thumb(shell.Output("Design")), shell.InEditor(150, 200));
        var height = shell.Bounds(shell.AddPopover).Height;
        // A click elsewhere closes the popover, and the next press on the port is not a second click.
        shell.Click(shell.InEditor(500, 250));

        // The popover's list fits with 4 px to spare above the 8 px it keeps from the canvas's bottom edge.
        var drop = new Point(canvas.X + 150, canvas.Bottom - 8 - height - 4);
        shell.Drag(shell.Thumb(shell.Output("Design")), drop);

        var popover = shell.Bounds(shell.AddPopover);
        Assert.Equal(height, popover.Height);
        Assert.True(Point.Distance(drop, popover.TopLeft) <= 2, $"{popover} does not open at {drop}");
    }

    [AvaloniaFact]
    public void Above_its_anchor_the_popover_keeps_its_search_field_in_place_while_a_query_shortens_the_list()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var click = shell.InEditor(150, shell.Editor.Bounds.Height - 20);

        shell.RightClick(click);
        var popover = shell.Bounds(shell.AddPopover);
        var search = shell.Bounds(shell.Find<TextBox>("AddNodeSearch")).Top;
        Assert.True(Math.Abs(popover.Bottom - click.Y) <= 2 && Math.Abs(popover.X - click.X) <= 2, $"{popover} does not end at {click}");

        shell.Type("rev");

        Assert.Equal(["Add Review"], shell.AddRows());
        Assert.Equal(search, shell.Bounds(shell.Find<TextBox>("AddNodeSearch")).Top);
        Assert.True(shell.Bounds(shell.AddPopover).Bottom < popover.Bottom - 100, $"{shell.Bounds(shell.AddPopover)} did not shorten from {popover}");
    }

    [AvaloniaFact]
    public void Every_action_stays_in_view_below_a_library_longer_than_the_popover()
    {
        var folder = _temp.Seed(
            AppTempFolder.TaskAt(TestTasks.Design, "Design", 105, 90), AppTempFolder.TaskAt(TestTasks.Build, "Build", 405, 90));
        foreach (var number in Enumerable.Range(1, 16))
        {
            BlueprintLibrary.Project(folder).Save(new Blueprint(
                new BlueprintKey($"fix-{number}", 1), $"Fix {number}", BugFix().Work, BugFix().Fields, BugFix().Defaults));
        }

        var shell = Shell.Open(folder);
        shell.Click(shell.Header(shell.Node("Build")));
        shell.Press(Key.Delete);
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Editor.ViewportZoom = 0.5;
        shell.Render();

        shell.RightClick(shell.InEditor(400, 300));

        string[] actions = ["Generate Workflow…", "Undo", "Select All", "Fit to View", "Zoom to 100%", "Delete Selection"];
        Assert.Equal(actions, shell.AddRows().SkipWhile(row => row.StartsWith("Add ", StringComparison.Ordinal)));
        var list = shell.Find<ItemsControl>("AddNodeList").FindAncestorOfType<ScrollViewer>()!;
        Assert.True(list.Extent.Height > list.Viewport.Height, $"The list shows all of its {list.Extent.Height} px in {list.Viewport.Height} px.");
        var popover = shell.Bounds(shell.AddPopover);
        Assert.True(shell.Bounds(shell.Editor).Contains(popover), $"{popover} is not inside the canvas");
        foreach (var action in actions)
        {
            var row = shell.Bounds(Action(shell, action));
            Assert.True(popover.Contains(row) && !shell.Bounds(list).Intersects(row), $"{action} at {row} is not in view in {popover}");
        }

        shell.Click(Action(shell, "Zoom to 100%"));

        Assert.False(shell.AddPopoverIsOpen);
        Assert.Equal(1, shell.Editor.ViewportZoom, 6);
    }

    private static Button Action(Shell shell, string name) =>
        shell.Window.GetVisualDescendants().OfType<Button>()
            .Single(button => Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "AddNodeAction" && Avalonia.Automation.AutomationProperties.GetName(button) == name);

    [AvaloniaFact]
    public void The_actions_select_all_and_zoom_to_100_percent()
    {
        var shell = Shell.Open(_temp.Seed(AppTempFolder.TaskAt(TestTasks.Design, "Design", 105, 90), AppTempFolder.TaskAt(TestTasks.Build, "Build", 405, 90)));
        shell.Editor.ViewportZoom = 0.5;
        shell.Render();

        shell.RightClick(shell.InEditor(600, 500));
        shell.Click(shell.Window.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Select All"));
        Assert.Equal(["Build", "Design"], shell.Window.ViewModel.Canvas!.SelectedNodes.Select(node => node.Title).Order());

        shell.RightClick(shell.InEditor(600, 500));
        Assert.Equal("Delete Selection", shell.AddRows().Last());
        shell.Click(shell.Window.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Zoom to 100%"));
        Assert.Equal(1, shell.Editor.ViewportZoom, 6);
    }
}
