using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The controls that float over the canvas never cover each other or leave it, at the smallest and a large window and with
/// the inspector at its least, default, and greatest width. The breadcrumb, Run Workflow, and Generate share the top row's
/// centre line, and the waiting pill sits between them or under the buttons. The zoom controls, the run bar, and the
/// minimap share the bottom edge, and the run bar never moves from it. A compact canvas shows the buttons as glyphs and
/// hides the minimap. The canvas keeps 336 px however wide the panels beside it are.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class CanvasChromeTests : IDisposable
{
    private static readonly (double Width, double Height, double Inspector)[] Sizes =
    [
        (900, 600, 280), (900, 600, 320), (900, 600, 520),
        (1280, 800, 280), (1280, 800, 520),
        (1600, 1000, 280), (1600, 1000, 320), (1600, 1000, 520),
    ];

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void The_side_panels_leave_the_canvas_336_px_and_take_their_chosen_widths_back_when_the_window_widens()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        shell.SizeInspector(520);
        Resize(shell, 900, 600);

        Assert.Equal((240, 322, 336), Widths(shell));

        SizeSidebar(shell, 400);
        Assert.Equal((282, 280, 336), Widths(shell));

        Resize(shell, 1600, 1000);
        Assert.Equal((400, 520, 678), Widths(shell));
    }

    [AvaloniaFact]
    public void The_inspectors_splitter_stops_where_the_canvas_would_get_narrower_than_336_px()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        Resize(shell, 900, 600);
        var splitter = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Single(control => Grid.GetColumn(control) == 3);

        shell.Drag(shell.Center(splitter), shell.Center(splitter) - new Vector(200, 0));

        Assert.Equal((240, 322, 336), Widths(shell));
    }

    [AvaloniaFact]
    public void An_open_proposal_and_a_waiting_planner_leave_the_breadcrumb_the_pill_and_both_buttons_apart_at_every_size()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Generate(shell);
        Assert.Equal(("1 task waits for you", "Review Proposal"), (shell.Text("WaitingCount"), AutomationProperties.GetName(shell.Find<Button>("GenerateWorkflow"))));
        Assert.True(shell.ShowsUnsavedChanges);
        // A refused connection's status sits under the breadcrumb, beside the pill when the pill moves under the buttons.
        // The view moves left first, so "Write docs"'s output is clear of the inspector's splitter.
        shell.Pan(shell.InEditor(260, 700), new Vector(-100, 0));
        shell.Drag(shell.Center(shell.Output("Write docs")), shell.Center(shell.Input("Design the API")));
        Assert.StartsWith("That would create a cycle", shell.Status);

        foreach (var (width, height, inspector) in Sizes)
        {
            Resize(shell, width, height, inspector);
            AssertTopRow(shell, $"{width}x{height}, inspector {inspector}", "Breadcrumb", "NextWaiting", "RunWorkflow", "GenerateWorkflow", "Status");
        }
    }

    [AvaloniaFact]
    public void A_running_workflow_keeps_its_run_bar_on_the_bottom_edge_between_the_zoom_controls_and_the_minimap_at_every_size()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        Assert.Equal("2 tasks wait for you", shell.Text("WaitingCount"));

        foreach (var (width, height, inspector) in Sizes)
        {
            Resize(shell, width, height, inspector);
            var at = $"{width}x{height}, inspector {inspector}";
            AssertTopRow(shell, at, "Breadcrumb", "NextWaiting", "GenerateWorkflow");
            AssertBottomRow(shell, at);
        }
    }

    [AvaloniaFact]
    public void The_docked_conversation_leaves_the_canvas_280_px_whose_two_rows_stay_apart_in_the_smallest_window()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Run(shell);
        shell.Click(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Click(shell.Find<Button>("ConversationLayout"));
        Assert.NotNull(shell.Window.ViewModel.DockedConversation);

        Resize(shell, 900, 600, 320);

        var canvas = shell.Bounds(shell.Editor);
        Assert.Equal(MainWindow.CanvasMinHeight, canvas.Height, 0.5);
        AssertTopRow(shell, "the dock", "Breadcrumb", "NextWaiting", "GenerateWorkflow");
        AssertBottomRow(shell, "the dock");
        var top = new[] { "Breadcrumb", "NextWaiting", "GenerateWorkflow" }.Max(id => shell.Bounds(shell.Find<Control>(id)).Bottom);
        var bottom = new[] { Shell.Around(shell.Find<Button>("ZoomIn"), "floating"), shell.Find<Control>("WorkflowRunBar") }.Min(control => shell.Bounds(control).Top);
        Assert.True(bottom - top >= 24, $"The top row ends at {top} and the bottom row starts at {bottom}.");

        Resize(shell, 1600, 1000);
        Assert.Equal(400, shell.Window.MainArea.RowDefinitions[2].ActualHeight, 0.5);
    }

    [AvaloniaFact]
    public void Fit_to_view_keeps_the_cards_below_the_top_row_and_above_the_run_bar_in_the_smallest_window()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        Resize(shell, 900, 600, 320);

        shell.Click(shell.Find<Button>("FitToScreen"));

        var top = new[] { "Breadcrumb", "NextWaiting", "GenerateWorkflow" }.Max(id => shell.Bounds(shell.Find<Control>(id)).Bottom);
        var bar = shell.Bounds(shell.Find<Control>("WorkflowRunBar"));
        foreach (var node in shell.Nodes())
        {
            var card = shell.Bounds(node).WithWidth(node.Bounds.Width * shell.Editor.ViewportZoom).WithHeight(node.Bounds.Height * shell.Editor.ViewportZoom);
            Assert.True(card.Top >= top + 12 && card.Bottom <= bar.Top - 12, $"{((TaskNodeViewModel)node.DataContext!).Title} at {card} is not between {top} and {bar.Top}");
        }
    }

    [AvaloniaFact]
    public void Ghost_cards_and_wires_shrink_with_the_zoom_as_the_cards_do()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Generate(shell);

        shell.Editor.ViewportZoom = 0.5;
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
            Assert.Contains(ports, port => Shell.Rounded(port + WorkflowCanvasViewModel.OutputPortCenter) == from);
            Assert.Contains(ports, port => Shell.Rounded(port + WorkflowCanvasViewModel.InputPortCenter) == to);
        }
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

    /// <summary>
    /// The top row's controls sit inside the canvas's inset, at least 8 px apart, those of the first row on one centre
    /// line, and none of their text is cut short. A compact canvas shows its buttons as 32 px glyphs that name themselves
    /// in their tooltips.
    /// </summary>
    private static void AssertTopRow(Shell shell, string at, params string[] ids)
    {
        var canvas = shell.Bounds(shell.Editor);
        var compact = canvas.Width < CanvasChrome.CompactWidth;
        var boxes = ids.Select(id => (Id: id, Box: shell.Bounds(shell.Find<Control>(id)))).ToArray();
        AssertInsideAndApart(canvas, boxes, at);
        var line = boxes.Single(box => box.Id == "Breadcrumb").Box.Center.Y;
        foreach (var (id, box) in boxes.Where(box => box.Box.Top < line))
        {
            Assert.True(Math.Abs(box.Center.Y - line) < 0.6, $"At {at}, {id} centres at {box.Center.Y}, off the row's line at {line}.");
        }

        foreach (var id in ids.Where(id => id is "RunWorkflow" or "GenerateWorkflow"))
        {
            var button = shell.Find<Button>(id);
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.True(compact != label.IsEffectivelyVisible, $"At {at}, {id}'s label shows: {label.IsEffectivelyVisible}.");
            if (compact)
            {
                Assert.Equal((32.0, 32.0), (button.Bounds.Width, button.Bounds.Height));
            }

            Assert.Equal(AutomationProperties.GetName(button), ((string)ToolTip.GetTip(button)!).Split('\n')[0]);
        }

        Assert.Equal(compact, !shell.Find<TextBlock>("BreadcrumbProject").IsEffectivelyVisible);
        var texts = ids.SelectMany(id => shell.Find<Control>(id).GetVisualDescendants().OfType<TextBlock>()).Where(text => text.IsEffectivelyVisible);
        Assert.Empty(texts.Where(Trimmed).Select(text => $"At {at}, \"{text.Text}\" is cut short."));
    }

    /// <summary>
    /// The zoom controls, the workflow's run bar, and the minimap, where it shows, end 12 px above the canvas's bottom edge,
    /// inside its inset and at least 12 px apart. The minimap shows only on a canvas that is neither compact nor short.
    /// </summary>
    private static void AssertBottomRow(Shell shell, string at)
    {
        var canvas = shell.Bounds(shell.Editor);
        var minimap = Shell.Around(shell.Find<Minimap>("Minimap"), "floating");
        Assert.Equal(canvas.Width >= CanvasChrome.CompactWidth && canvas.Height >= CanvasChrome.MinimapMinHeight, minimap.IsEffectivelyVisible);
        var boxes = new[] { ("Zoom", Shell.Around(shell.Find<Button>("ZoomIn"), "floating")), ("Minimap", minimap), ("WorkflowRunBar", shell.Find<Control>("WorkflowRunBar")) }
            .Where(pair => pair.Item2.IsEffectivelyVisible)
            .Select(pair => (Id: pair.Item1, Box: shell.Bounds(pair.Item2))).ToArray();
        Assert.Contains(boxes, box => box.Id == "WorkflowRunBar");
        AssertInsideAndApart(canvas, boxes, at, gap: 12);
        Assert.All(boxes, box => Assert.True(Math.Abs(box.Box.Bottom - (canvas.Bottom - CanvasChrome.Inset)) < 0.6, $"At {at}, {box.Id} ends at {box.Box.Bottom}, off {canvas.Bottom - 12}."));

        var bar = shell.Find<Control>("WorkflowRunBar");
        var stop = shell.Find<Button>("StopWorkflow");
        Assert.True(shell.Bounds(bar).Contains(shell.Bounds(stop)) && shell.Bounds(bar).Contains(shell.Bounds(Shell.Around(shell.Find<TextBlock>("RunStatus"), "pill"))));
        Assert.Equal(canvas.Width >= CanvasChrome.CompactWidth, stop.GetVisualDescendants().OfType<TextBlock>().Single().IsEffectivelyVisible);
        Assert.Empty(bar.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && Trimmed(text)).Select(text => $"At {at}, \"{text.Text}\" is cut short."));
    }

    private static void AssertInsideAndApart(Rect canvas, (string Id, Rect Box)[] boxes, string at, double gap = 8)
    {
        var room = canvas.Deflate(CanvasChrome.Inset);
        foreach (var (id, box) in boxes)
        {
            Assert.True(room.Inflate(0.5).Contains(box), $"At {at}, {id} at {box} leaves the canvas's inset {room}.");
        }

        foreach (var (first, second) in boxes.SelectMany((a, index) => boxes.Skip(index + 1).Select(b => (a, b))))
        {
            Assert.False(first.Box.Inflate(gap / 2 - 0.5).Intersects(second.Box.Inflate(gap / 2 - 0.5)),
                $"At {at}, {first.Id} at {first.Box} and {second.Id} at {second.Box} are less than {gap} px apart.");
        }
    }

    /// <summary>The control's drawn box in the window, through any transform.</summary>
    private static Rect Box(Shell shell, Visual visual) => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(shell.Window)!.Value);

    private static bool Trimmed(TextBlock text) => text.TextLayout.TextLines.Any(line => line.HasCollapsed);

    private static (double Sidebar, double Inspector, double Canvas) Widths(Shell shell)
    {
        var columns = (Grid)shell.Find<Control>("Inspector").Parent!;
        return (columns.ColumnDefinitions[0].ActualWidth, shell.Find<Control>("Inspector").Bounds.Width, columns.ColumnDefinitions[2].ActualWidth);
    }

    private static void SizeSidebar(Shell shell, double width)
    {
        ((Grid)shell.Find<Control>("Inspector").Parent!).ColumnDefinitions[0].Width = new GridLength(width);
        shell.Render();
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
