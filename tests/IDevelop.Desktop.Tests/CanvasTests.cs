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
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

public sealed class CanvasTests : IDisposable
{
    private static readonly TaskId Design = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly TaskId Build = new(Guid.Parse("019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22"));
    private static readonly TaskId Review = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static CreateTask Task(TaskId id, string title, double x, double y) =>
        new(new TaskDefinition(id) { Title = title }, new CanvasPoint(x, y));

    private static Point Rounded(Point point) => new(Math.Round(point.X, 2), Math.Round(point.Y, 2));

    private static (Point, Point) Rounded((Point Source, Point Target) ends) => (Rounded(ends.Source), Rounded(ends.Target));

    private static (string From, string To, ConnectionKind Kind)[] Drawn(Shell shell) =>
        [.. shell.Connections()
            .Select(connection => (ConnectionViewModel)connection.DataContext!)
            .Select(connection => (connection.From.Title, connection.To.Title, connection.Kind))
            .Order()];

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
        var shell = Shell.Open(_temp.Seed(Task(Design, title, 60, 60)));
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
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90)));
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
        var shell = Shell.Open(_temp.Seed(Task(Build, "Build", 405, 90)));
        string[] CardTexts() =>
        [
            .. shell.Node("Build").GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))
                .Select(text => text.Text!),
        ];
        Assert.Equal(["Build", "Not run", "No agent", "No instructions yet."], CardTexts());

        shell.Click(shell.Header(shell.Node("Build")));
        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Compile");

        Assert.Equal(["Build", "Not run", "No agent", "Compile"], CardTexts());
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
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90)));

        shell.Click(shell.Find<Button>("ZoomIn"));
        Assert.Equal(1.26, Math.Round(shell.Editor.ViewportZoom, 2));

        shell.Click(shell.Find<Button>("ZoomOut"));
        Assert.Equal(1, Math.Round(shell.Editor.ViewportZoom, 2));
    }

    [AvaloniaFact]
    public void Fit_to_screen_brings_a_distant_task_into_view()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 2400, 1600)));
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
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 405, 90)));
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
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90)));
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
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 2400, 1600)));
        var build = shell.Find<Minimap>("Minimap").GetVisualDescendants().OfType<MinimapItem>()
            .Single(item => ((TaskNodeViewModel)item.DataContext!).Title == "Build");

        shell.Click(build);

        var editor = shell.Editor;
        Assert.Equal(new Point(2530, 1672), Rounded(editor.ViewportLocation + new Vector(editor.ViewportSize.Width, editor.ViewportSize.Height) / 2));
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
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90)));

        Assert.True(shell.Find<TextBlock>("InspectorHint").IsEffectivelyVisible);
        Assert.Equal("Select a task or connection to edit it.", shell.Find<TextBlock>("InspectorHint").Text);
        Assert.False(shell.Has<TextBox>("TaskTitle"));

        shell.Click(shell.Header(shell.Node("Design")));

        Assert.False(shell.Find<TextBlock>("InspectorHint").IsEffectivelyVisible);
        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);
    }

    [AvaloniaFact]
    public void Dragging_an_output_onto_an_input_adds_a_dependency()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 405, 90)));

        shell.Drag(shell.Center(shell.Output("Design")), shell.Center(shell.Input("Build")));

        Assert.Equal([("Design", "Build", ConnectionKind.Dependency)], Drawn(shell));
        Assert.Equal(
            (Rounded(shell.Thumb(shell.Output("Design"))), Rounded(shell.Thumb(shell.Input("Build")))),
            Rounded(shell.Ends(Assert.Single(shell.Connections()))));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        Assert.Equal("", shell.Status);
    }

    [AvaloniaFact]
    public void A_drop_that_would_close_a_cycle_shows_why_and_adds_nothing()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        string? preview = null;

        shell.Drag(
            shell.Center(shell.Output("Build")),
            shell.Center(shell.Input("Design")),
            beforeRelease: () => preview = shell.Window.GetVisualDescendants().OfType<PendingConnection>().Single().Content as string);

        Assert.Equal("That would create a cycle: Build → Design → Build.", preview);
        Assert.Equal("That would create a cycle: Build → Design → Build.", shell.Status);
        Assert.Equal([("Design", "Build", ConnectionKind.Dependency)], Drawn(shell));
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Clicking_a_connection_after_a_task_shows_its_kind_buttons_which_change_its_kind()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);

        shell.Click(shell.ConnectionInto("Build"));

        Assert.False(shell.Has<TextBox>("TaskTitle"));
        Assert.False(shell.Find<Button>("KindDependency").IsEffectivelyEnabled);
        Assert.True(shell.Find<Button>("KindReview").IsEffectivelyEnabled);

        shell.Click(shell.Find<Button>("KindReview"));

        Assert.Equal([("Design", "Build", ConnectionKind.Review)], Drawn(shell));
        Assert.False(shell.Find<Button>("KindReview").IsEffectivelyEnabled);
        Assert.True(shell.Find<Button>("KindDependency").IsEffectivelyEnabled);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Clicking_a_task_after_a_connection_shows_the_task_and_deselects_the_connection()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.Click(shell.ConnectionInto("Build"));
        Assert.True(shell.Has<Button>("KindDependency"));

        shell.Click(shell.Header(shell.Node("Build")));

        Assert.Equal("Build", shell.Find<TextBox>("TaskTitle").Text);
        Assert.False(shell.Has<Button>("KindDependency"));
        Assert.False(BaseConnection.GetIsSelected(Assert.Single(shell.Connections())));
    }

    [AvaloniaTheory]
    [InlineData("ThemeLight", "#5A6168")]
    [InlineData("ThemeDark", "#999893")]
    public void Each_connection_kind_draws_in_its_theme_color(string theme, string context)
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            Task(Review, "Review", 705, 250),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Review)));
        shell.Click(shell.Find<RadioButton>(theme));
        var (dependency, review, contextColor) = (Color.Parse("#2563EB"), Color.Parse("#EA580C"), Color.Parse(context));
        (string From, string To, Color Stroke, Color Arrow, bool Dashed)[] Strokes() =>
        [
            .. shell.Connections()
                .Select(connection => (Model: (ConnectionViewModel)connection.DataContext!, Connection: connection))
                .OrderBy(drawn => drawn.Model.From.Title)
                .ThenBy(drawn => drawn.Model.To.Title)
                .Select(drawn => (
                    drawn.Model.From.Title,
                    drawn.Model.To.Title,
                    ((ISolidColorBrush)drawn.Connection.Stroke!).Color,
                    ((ISolidColorBrush)drawn.Connection.Fill!).Color,
                    drawn.Connection.StrokeDashArray is { Count: > 0 })),
        ];

        Assert.Equal(
            [("Build", "Review", review, review, false), ("Design", "Build", dependency, dependency, false), ("Design", "Review", contextColor, contextColor, true)],
            Strokes());

        shell.Click(shell.ConnectionInto("Build"));
        shell.Click(shell.Find<Button>("KindContext"));

        Assert.Contains(("Design", "Build", contextColor, contextColor, true), Strokes());
    }

    [AvaloniaFact]
    public void A_connection_runs_under_the_cards_it_crosses()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            Task(Review, "Review", 705, 250),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Dependency)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));

        // The connection turns down at x = 535, halfway between its ends, and passes under Build's instructions box.
        // On the open canvas it is #2563EB at 56% opacity over #FBFBF9.
        Assert.Equal(Color.Parse("#83A6F1"), shell.ColorAt(shell.Editor, new Point(535, 280)));
        Assert.Equal(Color.Parse("#FCFCFA"), shell.ColorAt(shell.Editor, new Point(535, 200)));
    }

    [AvaloniaFact]
    public void Clicking_just_beside_a_connection_selects_it()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));

        shell.Click(shell.ConnectionInto("Build") + new Vector(0, 4));

        Assert.True(BaseConnection.GetIsSelected(Assert.Single(shell.Connections())));
        Assert.False(shell.Find<Button>("KindDependency").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void A_kind_change_that_would_close_a_cycle_shows_why_and_keeps_the_kind()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            Task(Review, "Review", 405, 300),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Review, Design), ConnectionKind.Context)));
        shell.Click(shell.ConnectionInto("Design"));
        Assert.False(shell.Find<Button>("KindContext").IsEffectivelyEnabled);

        shell.Click(shell.Find<Button>("KindReview"));

        Assert.Equal("That would create a cycle: Review → Design → Build → Review.", shell.Status);
        Assert.Contains(("Review", "Design", ConnectionKind.Context), Drawn(shell));
        Assert.False(shell.Find<Button>("KindContext").IsEffectivelyEnabled);
        Assert.True(shell.Find<Button>("KindReview").IsEffectivelyEnabled);
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Enter_in_the_instructions_box_stores_a_line_feed()
    {
        var folder = _temp.Seed(Task(Design, "Design", 105, 90));
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
    public void Right_clicking_a_connection_opens_its_menu_which_changes_its_kind()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.RightClick(shell.ConnectionInto("Build"));
        shell.Click(shell.Window.GetVisualDescendants().OfType<MenuItem>().Single(item => (string?)item.Header == "Context"));

        Assert.Equal([("Design", "Build", ConnectionKind.Context)], Drawn(shell));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Deleting_a_selected_task_removes_it_and_its_connections()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            Task(Review, "Review", 405, 300),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Review),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context)));
        shell.Click(shell.Header(shell.Node("Build")));

        shell.Press(Key.Delete);

        Assert.Equal(["Design", "Review"], shell.Nodes().Select(node => ((TaskNodeViewModel)node.DataContext!).Title).Order());
        Assert.Equal([("Design", "Review", ConnectionKind.Context)], Drawn(shell));
        Assert.False(shell.Has<TextBox>("TaskTitle"));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Delete_in_the_title_box_deletes_a_character_and_keeps_the_task()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90)));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskTitle"));
        shell.Press(Key.Home);

        shell.Press(Key.Delete);

        Assert.Equal("esign", shell.Find<TextBox>("TaskTitle").Text);
        Assert.Equal("esign", ((TaskNodeViewModel)Assert.Single(shell.Nodes()).DataContext!).Title);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Pressing_delete_on_a_selected_connection_removes_only_the_connection()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.Click(shell.ConnectionInto("Build"));

        shell.Press(Key.Delete);

        Assert.Empty(Drawn(shell));
        Assert.Equal(["Build", "Design"], shell.Nodes().Select(node => ((TaskNodeViewModel)node.DataContext!).Title).Order());
    }

    [AvaloniaFact]
    public void Alt_clicking_a_connection_removes_it()
    {
        var shell = Shell.Open(_temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            Task(Review, "Review", 405, 300),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context)));

        shell.Click(shell.ConnectionInto("Build"), RawInputModifiers.Alt);

        Assert.Equal([("Design", "Review", ConnectionKind.Context)], Drawn(shell));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Dragging_a_task_commits_one_move_at_the_drop_that_survives_save_and_reopen()
    {
        var folder = _temp.Seed(
            Task(Design, "Design", 105, 90),
            Task(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency));
        var shell = Shell.Open(folder);
        var from = shell.Header(shell.Node("Design"));
        string? titleDuringDrag = null;

        shell.Drag(from, from + new Vector(150, 75), beforeRelease: () => titleDuringDrag = shell.Window.Title);

        Assert.Equal("seed - iDevelop", titleDuringDrag);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        Assert.Equal(new Point(255, 165), shell.Node("Design").Location);
        Assert.Equal(
            (Rounded(shell.Thumb(shell.Output("Design"))), Rounded(shell.Thumb(shell.Input("Build")))),
            Rounded(shell.Ends(Assert.Single(shell.Connections()))));
        shell.Press(Key.S, RawInputModifiers.Meta);
        Assert.Equal("seed - iDevelop", shell.Window.Title);
        var reopened = Shell.Open(folder);
        Assert.Equal(new Point(255, 165), reopened.Node("Design").Location);
        Assert.Equal(new Point(405, 90), reopened.Node("Build").Location);
    }
}
