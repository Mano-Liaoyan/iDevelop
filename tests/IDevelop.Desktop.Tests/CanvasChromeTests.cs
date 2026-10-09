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
        (1280, 800, 280), (1280, 800, 380), (1280, 800, 520),
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
    public void The_docked_conversation_keeps_a_few_lines_of_transcript_and_the_canvas_keeps_its_cards_clear_in_the_smallest_window()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        shell.Click(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Click(shell.Find<Button>("ConversationLayout"));
        Assert.NotNull(shell.Window.ViewModel.DockedConversation);

        Resize(shell, 900, 600, 320);

        // The conversation keeps a few lines of its transcript, its header on two lines, and its buttons in their boxes.
        var transcript = shell.Bounds(shell.Find<Control>("TranscriptScroller"));
        Assert.True(transcript.Height >= ConversationView.MinTranscriptHeight - 0.5, $"The transcript is {transcript.Height} px tall.");
        var header = Shell.Around(shell.Find<TextBlock>("ConversationTitle"), "conversationHeader");
        Assert.All(header.GetVisualDescendants().OfType<SpillRow>(), row => Assert.True(row.IsMeasureValid && row.IsArrangeValid));
        var lines = Lines(header.GetVisualDescendants().OfType<Control>()
            .Where(control => AutomationProperties.GetAutomationId(control) is "ConversationTitle" or "ConversationStatus" or "AttemptPicker"
                or "ConversationLayout" or "CloseConversation" or "ClientLimitationsInfo")
            .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0)
            .Select(control => shell.Bounds(control)));
        Assert.True(lines <= 2, $"The docked header has {lines} lines.");
        // The canvas's breadcrumb above names the project and the workflow, and the client's limitations sit behind a glyph.
        Assert.False(shell.Find<Control>("ConversationBreadcrumb").IsEffectivelyVisible);
        Assert.False(shell.Find<Control>("ClientLimitations").IsEffectivelyVisible);
        var info = shell.Find<Control>("ClientLimitationsInfo");
        Assert.True(info.IsEffectivelyVisible);
        var limitations = shell.Window.ViewModel.DockedConversation!.Limitations;
        Assert.Equal((limitations, limitations), (AutomationProperties.GetName(info), ToolTip.GetTip(info)));
        var box = shell.Bounds(Shell.Around(shell.Find<TextBox>("ConversationComposer"), "composer")).Deflate(1);
        foreach (var id in new[] { "ConversationComposer", "ComposerHint", "ConversationMarkDone", "ConversationCancel", "ConversationSend" })
        {
            Assert.True(box.Contains(shell.Bounds(shell.Find<Control>(id))), $"{id} at {shell.Bounds(shell.Find<Control>(id))} leaves the composer's box {box}.");
        }

        // The canvas above keeps its two rows apart and its cards clear of them.
        AssertTopRow(shell, "the dock", "Breadcrumb", "NextWaiting", "GenerateWorkflow");
        AssertBottomRow(shell, "the dock");
        var top = new[] { "Breadcrumb", "NextWaiting", "GenerateWorkflow" }.Max(id => shell.Bounds(shell.Find<Control>(id)).Bottom);
        var controls = new[] { Shell.Around(shell.Find<Button>("ZoomIn"), "floating"), shell.Find<Control>("WorkflowRunBar") }.Select(shell.Bounds).ToArray();
        Assert.True(controls.Min(control => control.Top) - top >= 12, $"The top row ends at {top} and the bottom row starts at {controls.Min(control => control.Top)}.");
        foreach (var node in shell.Nodes())
        {
            var card = shell.Bounds(node).WithWidth(node.Bounds.Width * shell.Editor.ViewportZoom).WithHeight(node.Bounds.Height * shell.Editor.ViewportZoom);
            Assert.All(controls, control => Assert.False(control.Intersects(card), $"{((TaskNodeViewModel)node.DataContext!).Title} at {card} is under {control}."));
        }

        // A taller window gives the canvas its 280 px before the dock grows to its default.
        Resize(shell, 900, 660);
        Assert.Equal(MainWindow.CanvasMinHeight, shell.Bounds(shell.Editor).Height, 0.5);
        Resize(shell, 1600, 1000);
        Assert.Equal(400, shell.Window.MainArea.RowDefinitions[2].ActualHeight, 0.5);
        Assert.True(shell.Bounds(shell.Editor).Height >= MainWindow.CanvasMinHeight);
    }

    [AvaloniaFact]
    public void Docking_moves_the_cards_and_the_proposals_ghosts_into_view_clear_of_the_floating_controls()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        Resize(shell, 1600, 1000, 320);
        shell.Click(shell.InCard<Button>("Write docs", "CardAttention"));

        shell.Click(shell.Find<Button>("ConversationLayout"));

        var controls = new[] { "Breadcrumb", "NextWaiting", "GenerateWorkflow", "WorkflowRunBar" }.Select(id => shell.Bounds(shell.Find<Control>(id)))
            .Concat(new[] { Shell.Around(shell.Find<Button>("ZoomIn"), "floating"), Shell.Around(shell.Find<Minimap>("Minimap"), "floating") }
                .Where(control => control.IsEffectivelyVisible).Select(shell.Bounds)).ToArray();
        var cards = Cards(shell);
        Assert.Equal(7, cards.Length);
        Assert.All(cards, card => Assert.All(controls, control => Assert.False(control.Intersects(card), $"The card at {card} is under {control}.")));

        // The window then narrows to its smallest, which cuts the cards at the canvas's edge, so the canvas fits them again.
        Resize(shell, 900, 600);

        var canvas = shell.Bounds(shell.Editor);
        var (top, bottom) = Rows(shell);
        Assert.All(Cards(shell), card => Assert.True(canvas.Contains(card) && card.Top >= top - 0.5 && card.Bottom <= bottom + 0.5,
            $"The card at {card} is not whole between {top} and {bottom} in {canvas}."));
    }

    [AvaloniaFact]
    public void Fit_to_view_uses_the_room_a_long_status_leaves_on_the_docked_canvas()
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Run(shell);
        shell.Pan(shell.InEditor(260, 700), new Vector(-100, 0));
        shell.Drag(shell.Center(shell.Output("Write docs")), shell.Center(shell.Input("Design the API")));
        Assert.StartsWith("That would create a cycle", shell.Status);
        shell.Click(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Click(shell.Find<Button>("ConversationLayout"));
        Resize(shell, 900, 660, 320);
        var (top, bottom) = Rows(shell);
        Assert.True(bottom - top < 48 + 2 * 24, $"The status leaves {bottom - top} px, room for the margins too.");
        shell.Editor.ViewportLocation = new Point(-2000, -2000);
        shell.Render();

        shell.Click(shell.Find<Button>("FitToScreen"));

        Assert.All(Cards(shell), card => Assert.True(card.Top >= top - 0.5 && card.Bottom <= bottom + 0.5, $"The card at {card} is not between {top} and {bottom}."));
    }

    /// <summary>How many lines the boxes take: boxes whose heights overlap share a line.</summary>
    private static int Lines(IEnumerable<Rect> boxes)
    {
        var (lines, bottom) = (0, double.NegativeInfinity);
        foreach (var box in boxes.OrderBy(box => box.Top))
        {
            if (box.Top >= bottom)
            {
                lines++;
            }

            bottom = Math.Max(bottom, box.Bottom);
        }

        return lines;
    }

    /// <summary>
    /// A fit keeps the cards and the proposal's ghost cards 24 px clear of both rows of floating controls, also below the
    /// waiting pill under the buttons of a compact canvas, and on the docked canvas of the smallest window, which has no
    /// room for those margins, right against them.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1600, 600, 280, false, 24)]
    [InlineData(900, 700, 320, true, 24)]
    [InlineData(900, 600, 320, true, 0)]
    public void Fit_to_view_keeps_the_cards_clear_of_the_top_row_and_the_bottom_row(double width, double height, double inspector, bool docked, double margin)
    {
        using var f = FanOut.Fixture();
        var shell = f.Window();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        if (docked)
        {
            shell.Click(shell.InCard<Button>("Write docs", "CardAttention"));
            shell.Click(shell.Find<Button>("ConversationLayout"));
        }

        Resize(shell, width, height, inspector);
        shell.Editor.ViewportLocation = new Point(-2000, -2000);
        shell.Render();
        shell.Click(shell.Find<Button>("FitToScreen"));

        var (top, bottom) = Rows(shell);
        Assert.All(Cards(shell), card => Assert.True(card.Top >= top + margin - 0.5 && card.Bottom <= bottom - margin + 0.5,
            $"The card at {card} is not {margin} px inside {top} and {bottom}."));
    }

    /// <summary>The lowest edge of the top row's controls and the highest edge of the bottom row's.</summary>
    private static (double Top, double Bottom) Rows(Shell shell) =>
        (new[] { "Breadcrumb", "NextWaiting", "GenerateWorkflow", "RunWorkflow", "Status" }.Where(shell.ShowsAny).Max(id => shell.Bounds(shell.Find<Control>(id)).Bottom),
         new Control[] { Shell.Around(shell.Find<Button>("ZoomIn"), "floating"), Shell.Around(shell.Find<Minimap>("Minimap"), "floating"), shell.Find<Control>("WorkflowRunBar") }
            .Where(control => control.IsEffectivelyVisible).Min(control => shell.Bounds(control).Top));

    /// <summary>The cards and the proposal's ghost cards as they show, at the editor's zoom.</summary>
    private static Rect[] Cards(Shell shell)
    {
        var zoom = shell.Editor.ViewportZoom;
        return [.. shell.Nodes().Select(node => shell.Bounds(node).WithWidth(node.Bounds.Width * zoom).WithHeight(node.Bounds.Height * zoom))
            .Concat(Shell.ById<Control>(shell.Window, "GhostCard").Select(ghost => shell.Bounds(ghost)
                .WithWidth(WorkflowCanvasViewModel.TaskCardWidth * zoom).WithHeight(WorkflowCanvasViewModel.TaskCardHeight * zoom)))];
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
    public void Tab_moves_through_the_breadcrumbs_buttons_in_the_order_they_show()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.PlaceInView(IDevelop.Workflows.BuiltInBlueprints.Implement);
        canvas.PlaceInView(IDevelop.Workflows.BuiltInBlueprints.Implement);
        shell.Window.ViewModel.UndoCommand.Execute(null);
        shell.Render();
        var (undo, redo, save) = (shell.Find<Button>("Undo"), shell.Find<Button>("Redo"), shell.Find<Button>("Save"));
        Assert.True(undo.IsEffectivelyEnabled && redo.IsEffectivelyEnabled && save.IsEffectivelyEnabled);
        Assert.True(shell.Bounds(undo).Right <= shell.Bounds(redo).Left && shell.Bounds(redo).Right <= shell.Bounds(save).Left);

        Assert.Same(redo, KeyboardNavigationHandler.GetNext(undo, NavigationDirection.Next));
        Assert.Same(save, KeyboardNavigationHandler.GetNext(redo, NavigationDirection.Next));
        Assert.Same(redo, KeyboardNavigationHandler.GetNext(save, NavigationDirection.Previous));
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
