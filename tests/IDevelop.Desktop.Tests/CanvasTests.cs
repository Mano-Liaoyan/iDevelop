using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

public sealed class CanvasTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void A_task_added_from_the_toolbar_keeps_its_typed_title_and_position_after_save_and_reopen()
    {
        var folder = _temp.Create("plan");
        var shell = Shell.Open(folder);

        shell.Click(shell.Find<Button>("AddTask"));

        Assert.Equal("New task", shell.Find<TextBox>("TaskTitle").Text);
        shell.Click(shell.Find<TextBox>("TaskTitle"));
        shell.Press(Key.A, RawInputModifiers.Control);
        shell.Type("Plan release");
        Assert.Equal("plan* - iDevelop", shell.Window.Title);
        Assert.True(shell.ShowsUnsavedChanges);

        shell.Press(Key.S, RawInputModifiers.Control);

        Assert.Equal("plan - iDevelop", shell.Window.Title);
        Assert.False(shell.ShowsUnsavedChanges);
        var reopened = Shell.Open(folder);
        var node = Assert.Single(reopened.Nodes());
        Assert.Equal("Plan release", ((TaskNodeViewModel)node.DataContext!).Title);
        Assert.Contains(node.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Plan release");
        Assert.Equal(new Point(60, 60), node.Location);
    }

    [AvaloniaFact]
    public void Tasks_added_from_the_toolbar_do_not_overlap()
    {
        var shell = Shell.Open(_temp.Create("plan"));

        for (var click = 0; click < 3; click++)
        {
            shell.Click(shell.Find<Button>("AddTask"));
        }

        var bounds = shell.Nodes().Select(node => node.Bounds).ToList();
        Assert.Equal(3, bounds.Count);
        Assert.Empty(bounds.SelectMany((a, i) => bounds.Skip(i + 1).Where(a.Intersects).Select(b => $"{a} overlaps {b}")));
    }

    [AvaloniaTheory]
    [InlineData(260)]
    [InlineData(310)]
    public void A_task_added_after_panning_does_not_overlap_a_task_with_a_long_title(double pan)
    {
        const string title = "Document the migration path for the new workflow file format";
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, title, 60, 60)));
        var canvasOrigin = shell.Window.GetVisualDescendants().OfType<NodifyEditor>().Single().TranslatePoint(default, shell.Window)!.Value;

        shell.Pan(canvasOrigin + new Vector(700, 400), new Vector(-pan, 0));
        shell.Click(shell.Find<Button>("AddTask"));

        var (existing, added) = (shell.Node(title), shell.Node("New task"));
        Assert.Equal(60 + pan, added.Location.X);
        Assert.False(existing.Bounds.Intersects(added.Bounds), $"{existing.Bounds} overlaps {added.Bounds}");
        Assert.True(existing.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == title).TextLayout.TextLines.Single().HasCollapsed);
        Assert.Equal(title, ToolTip.GetTip(existing.GetVisualDescendants().OfType<Panel>().Single(panel => AutomationProperties.GetAutomationId(panel) == "TaskCard")));
    }

    [AvaloniaFact]
    public void A_selected_card_shows_its_ring_in_the_selection_color()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        // The ring is 2 px wide and sits 2 px outside the card, so this pixel is on its straight top edge.
        Color Ring() => shell.ColorAt(shell.Node("Design"), new Point(130, -3));
        Assert.Equal(Color.Parse("#FBFBF9"), Ring());

        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal(Color.Parse("#2B7EC9"), Ring());

        shell.Click(shell.Find<RadioButton>("ThemeDark"));
        Assert.Equal(Color.Parse("#60AAF3"), Ring());
    }

    [AvaloniaFact]
    public void A_card_previews_its_instructions_and_says_when_there_are_none()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Build, "Build", 405, 90)));
        Assert.Equal(["Build", "Not run", "No agent", "No instructions yet."], Shell.Texts(shell.Node("Build")));

        shell.Click(shell.Header(shell.Node("Build")));
        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Compile");

        Assert.Equal(["Build", "Not run", "No agent", "Compile"], Shell.Texts(shell.Node("Build")));
    }

    [AvaloniaFact]
    public void The_dot_grid_moves_with_the_canvas_when_it_pans()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        // The 1 px dot is antialiased, so its pixel is the #E5E5E5 border color blended into the canvas.
        var (canvas, dot) = (Color.Parse("#FBFBF9"), Color.Parse("#E8E8E8"));
        Assert.Equal(dot, shell.ColorAt(shell.Editor, new Point(240, 240)));
        Assert.Equal(canvas, shell.ColorAt(shell.Editor, new Point(230, 240)));

        shell.Pan(shell.Editor.TranslatePoint(new Point(400, 300), shell.Window)!.Value, new Vector(-10, 0));

        Assert.Equal(dot, shell.ColorAt(shell.Editor, new Point(230, 240)));
        Assert.Equal(canvas, shell.ColorAt(shell.Editor, new Point(240, 240)));
    }

    [AvaloniaFact]
    public void Zoom_in_then_zoom_out_steps_the_canvas_zoom_and_back()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));

        shell.Click(shell.Find<Button>("ZoomIn"));
        Assert.Equal(1.26, Math.Round(shell.Editor.ViewportZoom, 2));

        shell.Click(shell.Find<Button>("ZoomOut"));
        Assert.Equal(1, Math.Round(shell.Editor.ViewportZoom, 2));
    }

    [AvaloniaFact]
    public void Fit_to_screen_brings_a_distant_task_into_view()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 2400, 1600)));
        Rect Card(string title) => new(shell.Node(title).TranslatePoint(default, shell.Editor)!.Value, shell.Node(title).Bounds.Size * shell.Editor.ViewportZoom);
        var editor = new Rect(shell.Editor.Bounds.Size);
        Assert.False(editor.Intersects(Card("Build")));

        shell.Click(shell.Find<Button>("FitToScreen"));

        Assert.True(editor.Contains(Card("Design")), $"{Card("Design")} is outside {editor}");
        Assert.True(editor.Contains(Card("Build")), $"{Card("Build")} is outside {editor}");
    }

    [AvaloniaFact]
    public void Dragging_a_task_moves_its_minimap_item()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 405, 90)));
        Point Item(string title) => shell.Find<Minimap>("Minimap").GetVisualDescendants().OfType<MinimapItem>()
            .Single(item => ((TaskNodeViewModel)item.DataContext!).Title == title).Bounds.Position;
        Assert.Equal((new Point(0, 0), new Point(300, 0)), (Item("Design"), Item("Build")));

        var from = shell.Header(shell.Node("Design"));
        shell.Drag(from, from + new Vector(150, 75));

        Assert.Equal((new Point(0, 75), new Point(150, 0)), (Item("Design"), Item("Build")));
    }

    [AvaloniaFact]
    public void Turning_the_wheel_over_the_minimap_zooms_the_canvas_one_step_per_notch()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        var minimap = shell.Center(shell.Find<Minimap>("Minimap"));

        shell.Window.MouseWheel(minimap, new Vector(0, 1));
        Assert.Equal(1.26, Math.Round(shell.Editor.ViewportZoom, 2));

        shell.Window.MouseWheel(minimap, new Vector(0, -1));
        shell.Window.MouseWheel(minimap, new Vector(0, -1));
        Assert.Equal(0.79, Math.Round(shell.Editor.ViewportZoom, 2));
    }

    [AvaloniaFact]
    public void Clicking_the_minimap_centers_the_canvas_on_that_point()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 2400, 1600)));
        var build = shell.Find<Minimap>("Minimap").GetVisualDescendants().OfType<MinimapItem>()
            .Single(item => ((TaskNodeViewModel)item.DataContext!).Title == "Build");

        shell.Click(build);

        var editor = shell.Editor;
        Assert.Equal(new Point(2530, 1672), Shell.Rounded(editor.ViewportLocation + new Vector(editor.ViewportSize.Width, editor.ViewportSize.Height) / 2));
    }

    [AvaloniaFact]
    public void The_canvas_menu_adds_a_task_where_the_canvas_was_right_clicked()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var canvasOrigin = shell.Window.GetVisualDescendants().OfType<NodifyEditor>().Single().TranslatePoint(default, shell.Window)!.Value;

        shell.RightClick(canvasOrigin + new Vector(300, 405));
        shell.Click(shell.Window.GetVisualDescendants().OfType<MenuItem>().Single(item => (string?)item.Header == "Add task"));

        var node = Assert.Single(shell.Nodes());
        Assert.Equal(new Point(300, 405), node.Location);
        Assert.Equal("New task", shell.Find<TextBox>("TaskTitle").Text);
        Assert.Equal("plan* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void The_inspector_asks_for_a_selection_until_a_task_is_selected()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));

        Assert.True(shell.Find<TextBlock>("InspectorHint").IsEffectivelyVisible);
        Assert.Equal("Select a task or connection to edit it.", shell.Find<TextBlock>("InspectorHint").Text);
        Assert.False(shell.Has<TextBox>("TaskTitle"));

        shell.Click(shell.Header(shell.Node("Design")));

        Assert.False(shell.Find<TextBlock>("InspectorHint").IsEffectivelyVisible);
        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);
    }

    [AvaloniaFact]
    public void Enter_in_the_instructions_box_stores_a_line_feed()
    {
        var folder = _temp.Seed(TaskAt(Design, "Design", 105, 90));
        var shell = Shell.Open(folder);
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskInstructions"));

        shell.Type("Draft");
        shell.Press(Key.Enter);
        shell.Type("Review");
        shell.Press(Key.S, RawInputModifiers.Control);

        Assert.Equal("Draft\nReview", shell.Find<TextBox>("TaskInstructions").Text);
        Assert.Equal("Draft\nReview", WorkflowDocument.Open(folder).Current.Tasks[Design].Instructions);
    }

    [AvaloniaFact]
    public void Deleting_a_selected_task_removes_it_and_its_connections()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            TaskAt(Build, "Build", 405, 90),
            TaskAt(Review, "Review", 405, 300),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Review),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context)));
        shell.Click(shell.Header(shell.Node("Build")));

        shell.Press(Key.Delete);

        Assert.Equal(["Design", "Review"], shell.Nodes().Select(node => ((TaskNodeViewModel)node.DataContext!).Title).Order());
        Assert.Equal([("Design", "Review", ConnectionKind.Context)], shell.Drawn());
        Assert.False(shell.Has<TextBox>("TaskTitle"));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Delete_in_the_title_box_deletes_a_character_and_keeps_the_task()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskTitle"));
        shell.Press(Key.Home);

        shell.Press(Key.Delete);

        Assert.Equal("esign", shell.Find<TextBox>("TaskTitle").Text);
        Assert.Equal("esign", ((TaskNodeViewModel)Assert.Single(shell.Nodes()).DataContext!).Title);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Dragging_a_task_commits_one_move_at_the_drop_that_survives_save_and_reopen()
    {
        var folder = _temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            TaskAt(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency));
        var shell = Shell.Open(folder);
        var from = shell.Header(shell.Node("Design"));
        string? titleDuringDrag = null;

        shell.Drag(from, from + new Vector(150, 75), beforeRelease: () => titleDuringDrag = shell.Window.Title);

        Assert.Equal("seed - iDevelop", titleDuringDrag);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        Assert.Equal(new Point(255, 165), shell.Node("Design").Location);
        Assert.Equal(
            (Shell.Rounded(shell.Thumb(shell.Output("Design"))), Shell.Rounded(shell.Thumb(shell.Input("Build")))),
            Shell.Rounded(shell.Ends(Assert.Single(shell.Connections()))));
        shell.Press(Key.S, RawInputModifiers.Meta);
        Assert.Equal("seed - iDevelop", shell.Window.Title);
        var reopened = Shell.Open(folder);
        Assert.Equal(new Point(255, 165), reopened.Node("Design").Location);
        Assert.Equal(new Point(405, 90), reopened.Node("Build").Location);
    }
}
