using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Theme;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The Command Bar and the Map Footer never cover each other or a card, at the smallest and a large window and with the
/// Agent panel at its least, default, and greatest width. The Command Bar's controls share one 44 px line. The Map keeps 336 px
/// however wide the sheets beside it are.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class CanvasChromeTests : IDisposable
{
    private static readonly (double Width, double Height, double Inspector)[] Sizes =
    [
        (900, 600, 372),
        (1280, 800, 360), (1280, 800, 400), (1280, 800, 560),
        (1700, 1000, 360), (1700, 1000, 520), (1700, 1000, 720),
        (2560, 1440, 880), (2560, 1440, 1008),
    ];

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void The_Agent_panel_gives_up_width_first_so_the_Map_keeps_336_px_at_any_dragged_width()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        Resize(shell, 1180, 600);
        shell.SizeInspector(560);
        Assert.Equal((252, 560, 352), Widths(shell));

        shell.SizeSidebar(400);
        Assert.Equal((400, 428, 336), Widths(shell));
    }

    [AvaloniaFact]
    public void The_Agent_panels_splitter_stops_where_the_Map_would_get_narrower_than_336_px()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        Resize(shell, 1180, 600);
        shell.SizeSidebar(400);
        var splitter = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Single(control => Grid.GetColumn(control) == 3);

        var grip = shell.Center(splitter);
        shell.Drag(grip, grip - new Vector(400, 0));

        Assert.Equal((400, 428, 336), Widths(shell));
    }

    [AvaloniaFact]
    public void An_open_proposal_and_a_waiting_planner_leave_the_breadcrumb_and_both_buttons_apart_at_every_size()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Generate(shell);
        Assert.Equal((1, "Review Proposal"), (shell.Window.ViewModel.Canvas!.WaitingCount, AutomationProperties.GetName(shell.Find<Button>("GenerateWorkflow"))));
        Assert.True(shell.ShowsUnsavedChanges);
        // A refused connection's status sits over the Map. The window is wide enough for both of the ports the drag joins.
        Resize(shell, 1500, 800);
        shell.AtOrigin();
        shell.Pan(shell.InEditor(260, 600), new Vector(-100, 0));
        shell.Drag(shell.Center(shell.Output("Write docs")), shell.Center(shell.Input("Design the API")));
        Assert.StartsWith("That would create a cycle", shell.Status);

        foreach (var (width, height, inspector) in Sizes)
        {
            Resize(shell, width, height, inspector);
            AssertTopRow(shell, $"{width}x{height}, inspector {inspector}");
        }
    }

    [AvaloniaFact]
    public void A_Run_Strip_that_lacks_room_for_its_pills_words_keeps_their_marks_and_counts()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        var strip = shell.Find<Control>("RunStrip");

        foreach (var (width, height, inspector) in Sizes)
        {
            Resize(shell, width, height, inspector);
            AssertTopRow(shell, $"{width}x{height}");
            Assert.True(strip.Bounds.Width <= 480.5, $"The strip at {width}x{height} is {strip.Bounds.Width} px wide.");
        }
    }

    [AvaloniaFact]
    public void A_workflow_of_many_states_keeps_every_run_chip_whole_at_every_window_size_and_panel_width()
    {
        var (failed, asks, replies, stopped, alsoStopped, running) = (TestTasks.Design, TestTasks.Build, TestTasks.Review, new TaskId(Guid.NewGuid()), new TaskId(Guid.NewGuid()), new TaskId(Guid.NewGuid()));
        using var f = new NodeFixture(
            NodeFixture.Task(failed, "Failed", 105), NodeFixture.Task(asks, "Asks", 405), NodeFixture.Task(replies, "Replies", 705),
            NodeFixture.Task(stopped, "Stopped", 1005), NodeFixture.Task(alsoStopped, "Also stopped", 1305), NodeFixture.Task(running, "Runs", 1605));
        var started = """{"entry":"turnStarted","basis":{"inputs":[],"instruction":"","model":null},"session":null,"round":false}""";
        void Log(TaskId node, params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Engine.NodeLog.PathOf(f.Project, node))!);
            File.WriteAllLines(Engine.NodeLog.PathOf(f.Project, node), [started, .. lines]);
        }

        Log(failed, """{"entry":"failed","reason":"The model is not available."}""");
        Log(asks, """{"entry":"waiting","why":"Which window?","text":"Which window?"}""");
        Log(replies, """{"entry":"waiting","why":"reply","text":null}""");
        Log(stopped);
        Log(alsoStopped);
        f.Install(FakeRule.On().WaitForFile(f.Gate("runs")));
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.Equal(NodeState.InputRequired, canvas.Rollup);
        Assert.Equal(new Theme.StateCounts(0, 0, 4, 1, 0, 6), canvas.Counts);

        foreach (var width in new[] { 900.0, 1000, 1180, 1280, 1700, 2560 })
        {
            Resize(shell, width, 800);
            AssertTopRow(shell, $"{width}x800");
            AssertBottomRow(shell, $"{width}x800");
            var pills = new[] { "RunPillNeedsYou", "RunPillFailed" }.Select(id => shell.Bounds(shell.Find<Control>(id))).ToArray();
            Assert.All(pills, pill => Assert.True(shell.Bounds(shell.Find<Control>("RunStrip")).Inflate(0.5).Contains(pill), $"A pill at {pill} leaves the strip at {width}."));
            if (width >= 1180)
            {
                Assert.Equal(["4", "need you"], shell.Find<Control>("RunPillNeedsYou").GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text));
            }
        }

        shell.RunNode("Runs");
        shell.WaitForState("Runs", NodeState.Running);
        Resize(shell, 900, 800);
        AssertTopRow(shell, "900x800 while a node runs");
        Assert.True(shell.Find<Control>("RunPillRunning").IsEffectivelyVisible);
        f.Release("runs");
        shell.WaitUntil(() => canvas.RunningCount == 0, "the node ends");
    }

    [AvaloniaFact]
    public void A_running_workflow_keeps_the_Command_Bar_and_the_Map_Footer_apart_at_every_size()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        Assert.Equal(2, shell.Window.ViewModel.Canvas!.WaitingCount);

        foreach (var (width, height, inspector) in Sizes)
        {
            Resize(shell, width, height, inspector);
            var at = $"{width}x{height}, inspector {inspector}";
            AssertTopRow(shell, at);
            AssertBottomRow(shell, at);
        }
    }

    [AvaloniaTheory]
    [InlineData(1600, 600, 280, 8)]
    [InlineData(900, 700, 320, 8)]
    [InlineData(900, 600, 320, 8)]
    public void Fit_to_view_keeps_the_cards_inside_the_editor_between_the_bands(double width, double height, double inspector, double margin)
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        Resize(shell, width, height, inspector);
        shell.Editor.ViewportLocation = new Point(-2000, -2000);
        shell.Render();
        shell.Click(shell.Find<Button>("FitToScreen"));

        var (top, bottom) = Rows(shell);
        var zoom = shell.Editor.ViewportZoom;
        Assert.All(shell.Nodes().Select(node => shell.Bounds(node).WithHeight(node.Bounds.Height * zoom)), card => Assert.True(card.Top >= top + margin - 0.5 && card.Bottom <= bottom - margin + 0.5,
            $"The card at {card} is not {margin} px inside {top} and {bottom}."));
    }

    /// <summary>The editor's top and bottom edges, between the two bands.</summary>
    private static (double Top, double Bottom) Rows(Shell shell) => (shell.Bounds(shell.Editor).Top, shell.Bounds(shell.Editor).Bottom);

    /// <summary>The cards and the proposal's ghost cards as they show, at the editor's zoom.</summary>
    private static Rect[] Cards(Shell shell)
    {
        var zoom = shell.Editor.ViewportZoom;
        return [.. shell.Nodes().Select(node => shell.Bounds(node).WithWidth(node.Bounds.Width * zoom).WithHeight(node.Bounds.Height * zoom))
            .Concat(Shell.ById<Control>(shell.Window, "GhostCard").Select(ghost => Box(shell, ghost)
                .WithWidth(WorkflowCanvasViewModel.TaskCardWidth * zoom).WithHeight(WorkflowCanvasViewModel.TaskCardHeight * zoom)))];
    }

    [AvaloniaFact]
    public void Ghost_cards_and_wires_shrink_with_the_zoom_as_the_cards_do()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Generate(shell);

        shell.Editor.ZoomAtPosition(0.5 / shell.Editor.ViewportZoom, new Point(0, 0));
        shell.Render();

        var card = shell.CardRect("Design the API");
        Assert.Equal(WorkflowCanvasViewModel.TaskCardWidth * 0.5, card.Width, 0.5);
        var ghosts = Shell.ById<Control>(shell.Window, "GhostCard").Select(ghost => Box(shell, ghost)).ToArray();
        Assert.Equal(3, ghosts.Length);
        Assert.All(ghosts, ghost => Assert.Equal((card.Width, card.Height), (ghost.Width, ghost.Height)));
        // Each ghost wire still runs from an output port's centre to an input port's centre.
        var canvas = shell.Window.ViewModel.Canvas!;
        var ports = canvas.Workflow.Positions.Values.Select(WorkflowCanvasViewModel.ToPoint)
            .Concat(shell.Window.ViewModel.Canvas!.Ghosts.OfType<GhostCardViewModel>().Select(ghost => ghost.Location)).ToArray();
        foreach (var wire in shell.Window.GetVisualDescendants().OfType<StepConnection>().Where(wire => AutomationProperties.GetAutomationId(wire) == "GhostWire"))
        {
            var (source, target) = shell.Ends(wire);
            var (from, to) = (Shell.Rounded(shell.CanvasPointAt(source)), Shell.Rounded(shell.CanvasPointAt(target)));
            // An anchor sits on a whole screen pixel, which is 2 canvas units at this zoom.
            Assert.Contains(ports, port => Point.Distance(port + WorkflowCanvasViewModel.OutputPortCenter, from) <= 2);
            Assert.Contains(ports, port => Point.Distance(port + WorkflowCanvasViewModel.InputPortCenter, to) <= 2);
        }
    }

    /// <summary>A pan, a zoom, and a fit each move a ghost with the cards, so the ghost keeps its place among them.</summary>
    [AvaloniaFact]
    public void A_ghost_keeps_its_place_among_the_cards_at_each_zoom_and_location()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Generate(shell);
        var card = shell.Nodes().First(node => ((TaskNodeViewModel)node.DataContext!).Title == "Design the API");
        var ghost = Shell.ById<Control>(shell.Window, "GhostCard").First();
        void Check()
        {
            shell.Render();
            var zoom = shell.Editor.ViewportZoom;
            var apart = ((GhostCardViewModel)ghost.DataContext!).Location - ((TaskNodeViewModel)card.DataContext!).DisplayLocation;
            var (shown, at) = (Box(shell, ghost), Box(shell, card));
            // Layout places a box on a whole pixel, so a ghost may land half a pixel away.
            Assert.InRange(shown.X - at.X - apart.X * zoom, -1, 1);
            Assert.InRange(shown.Y - at.Y - apart.Y * zoom, -1, 1);
        }

        Check();
        shell.Editor.ViewportZoom = 0.5;
        Check();
        shell.Editor.ViewportLocation = new Point(37, -21);
        Check();
        shell.Editor.ZoomAtPosition(2, new Point(300, 200));
        Check();
        shell.Window.GetVisualDescendants().OfType<WorkflowCanvasView>().First().FitToView();
        Check();
    }

    [AvaloniaTheory]
    [InlineData(0.7)]
    [InlineData(0.3)]
    public void A_ghost_shows_a_cards_far_form_while_the_cards_do(double zoom)
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Generate(shell);
        shell.Editor.ViewportZoom = zoom;
        shell.Render();

        var ghost = Shell.ById<Control>(shell.Window, "GhostCard").First();
        Assert.True(ghost.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("ghostFar")).IsEffectivelyVisible);
        Assert.False(Shell.ById<TextBlock>(ghost, "GhostTitle").Single().IsEffectivelyVisible);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5000, 3000)]
    [InlineData(5000, 0)]
    [InlineData(0, 3000)]
    public void Reveal_holds_the_found_card_inside_the_room_wherever_the_view_starts(double x, double y)
    {
        var room = new Rect(24, 24, 652, 452);
        const double zoom = 0.6;
        var keep = new Rect(2000, 1000, 260, 90);
        Rect[] cards = [keep, new Rect(1700, 1000, 260, 90), new Rect(2300, 1000, 260, 90), new Rect(2000, 800, 260, 90), new Rect(2000, 1200, 260, 90)];

        var at = CanvasChrome.ClearOfCards(new Point(x, y), new Size(700, 500), room, zoom, cards, keep);

        var seen = new Rect(at + (Vector)room.Position / zoom, room.Size / zoom);
        Assert.True(seen.Contains(keep), $"{keep} is not inside {seen}.");
    }

    [Fact]
    public void Reveal_keeps_the_near_edge_of_a_card_wider_than_the_room()
    {
        var room = new Rect(24, 24, 100, 100);
        var keep = new Rect(500, 500, 400, 400);

        var at = CanvasChrome.ClearOfCards(new Point(0, 0), new Size(148, 148), room, 1, [keep], keep);

        Assert.Equal(new Point(476, 476), at);
    }

    [AvaloniaFact]
    public void Tab_moves_through_the_breadcrumbs_buttons_in_the_order_they_show()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.PlaceInView(Workflows.BuiltInBlueprints.Implement);
        canvas.PlaceInView(Workflows.BuiltInBlueprints.Implement);
        shell.Window.ViewModel.UndoCommand.Execute(null);
        shell.Render();
        var (undo, redo, save) = (shell.Find<Button>("Undo"), shell.Find<Button>("Redo"), shell.Find<Button>("Save"));
        Assert.True(undo.IsEffectivelyEnabled && redo.IsEffectivelyEnabled && save.IsEffectivelyEnabled);
        Assert.True(shell.Bounds(undo).Right <= shell.Bounds(redo).Left && shell.Bounds(redo).Right <= shell.Bounds(save).Left);

        undo.Focus();
        shell.Press(Key.Tab);
        Assert.Same(redo, shell.Window.FocusManager!.GetFocusedElement());
        shell.Press(Key.Tab);
        Assert.Same(save, shell.Window.FocusManager!.GetFocusedElement());
        shell.Press(Key.Tab, RawInputModifiers.Shift);
        Assert.Same(redo, shell.Window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void The_empty_workflows_card_fits_the_narrowest_canvas()
    {
        var shell = Shell.Open(_temp.Create("seed"));
        Resize(shell, 900, 600, 520);

        var canvas = shell.Bounds(shell.Editor);
        var card = shell.Bounds(shell.Find<Control>("GenerateEmptyState"));
        Assert.True(canvas.Deflate(24).Contains(card), $"The card at {card} does not fit {canvas}");
        Assert.Equal(canvas.Center.X, card.Center.X, 0.5);
        Assert.DoesNotContain(shell.Find<Control>("GenerateEmptyState").GetVisualDescendants().OfType<TextBlock>(), Trimmed);
    }

    /// <summary>The Command Bar's controls share one centre line, stay inside the window, and keep 8 px apart, with no name cut short but the breadcrumb's.</summary>
    private static void AssertTopRow(Shell shell, string at)
    {
        var bar = shell.Bounds(shell.Find<Control>("CommandBar"));
        var ids = new[] { "ToggleSidebar", "Breadcrumb", "Undo", "RunStrip", "AddTask", "GenerateWorkflow", "RunWorkflow", "StopAll" }.Where(shell.ShowsAny).ToArray();
        var boxes = ids.Select(id => (Id: id, Box: shell.Bounds(shell.Find<Control>(id)))).ToArray();
        foreach (var (id, box) in boxes)
        {
            Assert.True(bar.Inflate(0.5).Contains(box), $"At {at}, {id} at {box} leaves the bar {bar}.");
            Assert.True(Math.Abs(box.Center.Y - bar.Center.Y) < 1, $"At {at}, {id} centres at {box.Center.Y}, off the bar's line at {bar.Center.Y}.");
        }

        foreach (var (first, second) in boxes.SelectMany((a, index) => boxes.Skip(index + 1).Select(b => (a, b))))
        {
            Assert.False(first.Box.Inflate(1.5).Intersects(second.Box), $"At {at}, {first.Id} at {first.Box} and {second.Id} at {second.Box} touch.");
        }

        var compact = shell.Window.ViewModel.IsCompact;
        Assert.Equal(compact, !shell.Find<TextBlock>("BreadcrumbProject").IsEffectivelyVisible);
        foreach (var id in ids.Where(id => id is "RunWorkflow" or "GenerateWorkflow"))
        {
            var button = shell.Find<Button>(id);
            var labels = button.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
            if (id == "RunWorkflow")
            {
                Assert.Equal(compact ? ["Run"] : ["Run Workflow"], labels);
                Assert.Equal(compact ? 84 : 124, button.Bounds.Width);
            }
            else if (compact)
            {
                Assert.Empty(labels);
                Assert.Equal((28.0, 28.0), (button.Bounds.Width, button.Bounds.Height));
            }
            else
            {
                Assert.Contains("Generate", labels);
                Assert.Equal(96, button.Bounds.Width);
            }

            Assert.Equal(AutomationProperties.GetName(button), ((string)ToolTip.GetTip(button)!).Split('\n')[0]);
        }

        var texts = new[] { "RunStrip", "RunWorkflow", "GenerateWorkflow" }.Where(shell.ShowsAny).SelectMany(id => shell.Find<Control>(id).GetVisualDescendants().OfType<TextBlock>()).Where(text => text.IsEffectivelyVisible);
        Assert.Empty(texts.Where(Trimmed).Select(text => $"At {at}, \"{text.Text}\" is cut short."));
    }

    [AvaloniaTheory]
    [InlineData(300, 100, 150)]
    [InlineData(240, 90, 150)]
    [InlineData(200, 0, 150)]
    [InlineData(120, 0, 120)]
    public void The_project_s_name_gives_up_its_width_before_the_workflow_s_name(double room, double projectWidth, double workflowWidth)
    {
        var (project, workflow) = (new Border { Child = new Border { Width = 100, Height = 10 } }, new Border { Child = new Border { Width = 150, Height = 10 } });
        var names = new CrumbNames { Children = { project, workflow } };
        names.Measure(new Size(room, 30));
        names.Arrange(new Rect(names.DesiredSize));
        Assert.Equal((projectWidth, workflowWidth), (project.Bounds.Width, workflow.Bounds.Width));
    }

    /// <summary>The Map Footer sits under the editor, and its zoom controls keep inside the Map.</summary>
    private static void AssertBottomRow(Shell shell, string at)
    {
        var (canvas, editor, zoom) = (shell.Bounds(shell.Window.CanvasArea), shell.Bounds(shell.Editor), shell.Bounds(shell.Find<Control>("ZoomPanel")));
        Assert.True(canvas.Inflate(0.5).Contains(zoom), $"At {at}, the zoom panel at {zoom} leaves the Map {canvas}.");
        Assert.True(zoom.Top >= editor.Bottom - 0.5, $"At {at}, the zoom panel at {zoom} is not under the editor {editor}.");
        Assert.Equal(canvas.Bottom, shell.Bounds(shell.Find<Control>("MapFooter")).Bottom, 0.5);
    }

/// <summary>The control's drawn box in the window, through any transform.</summary>
    private static Rect Box(Shell shell, Visual visual) => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(shell.Window)!.Value);

    private static bool Trimmed(TextBlock text) => text.TextLayout.TextLines.Any(line => line.HasCollapsed);

    private static (double Sidebar, double Inspector, double Canvas) Widths(Shell shell)
    {
        var columns = (Grid)((Control)shell.Find<Control>("Inspector").Parent!).Parent!;
        return (columns.ColumnDefinitions[0].ActualWidth, columns.ColumnDefinitions[4].ActualWidth, columns.ColumnDefinitions[2].ActualWidth);
    }

    private static void Resize(Shell shell, double width, double height, double? inspector = null)
    {
        shell.Window.Width = width;
        shell.Window.Height = height;
        shell.Render();
        if (inspector is { } chosen)
        {
            shell.SizeInspector(chosen);
        }
    }
}
