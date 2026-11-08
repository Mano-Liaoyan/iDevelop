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
/// The bars docked around the canvas never cover each other or a card, at the smallest and a large window and with the
/// inspector at its least, default, and greatest width. The breadcrumb, Run Workflow, and Generate share the top band's
/// centre line. The zoom controls and the run bar share the bottom band. A compact canvas shows the buttons as glyphs and
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
        Resize(shell, 1000, 600);

        Assert.Equal((272, 390, 336), Widths(shell));

        shell.SizeSidebar(400);
        Assert.Equal((382, 280, 336), Widths(shell));

        // A narrow window closes the inspector, and it gives its width back to the canvas.
        Resize(shell, 900, 600);
        Assert.Equal((240, 0, 640), Widths(shell));

        Resize(shell, 1600, 1000);
        Assert.Equal((400, 520, 678), Widths(shell));
    }

    [AvaloniaFact]
    public void The_inspectors_splitter_stops_where_the_canvas_would_get_narrower_than_336_px()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        Resize(shell, 1000, 600);
        var splitter = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Single(control => Grid.GetColumn(control) == 3);

        // The panel's tab sits on the splitter's centre.
        var grip = shell.Center(splitter) + new Vector(0, 150);
        shell.Drag(grip, grip - new Vector(200, 0));

        Assert.Equal((272, 390, 336), Widths(shell));
    }

    [AvaloniaFact]
    public void An_open_proposal_and_a_waiting_planner_leave_the_breadcrumb_and_both_buttons_apart_at_every_size()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Generate(shell);
        Assert.Equal((1, "Review Proposal"), (shell.Window.ViewModel.Canvas!.WaitingCount, AutomationProperties.GetName(shell.Find<Button>("GenerateWorkflow"))));
        Assert.True(shell.ShowsUnsavedChanges);
        // A refused connection's status sits under the breadcrumb.
        // The view moves left first, so "Write docs"'s output is clear of the inspector's splitter.
        shell.AtOrigin();
        shell.Pan(shell.InEditor(260, 600), new Vector(-100, 0));
        shell.Drag(shell.Center(shell.Output("Write docs")), shell.Center(shell.Input("Design the API")));
        Assert.StartsWith("That would create a cycle", shell.Status);

        foreach (var (width, height, inspector) in Sizes)
        {
            Resize(shell, width, height, inspector);
            AssertTopRow(shell, $"{width}x{height}, inspector {inspector}", "Breadcrumb", "RunWorkflow", "GenerateWorkflow", "Status");
        }
    }

    [AvaloniaFact]
    public void A_run_bar_that_lacks_room_for_its_words_sums_the_rest_and_cuts_no_word()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        var bar = shell.Find<Control>("WorkflowRunBar");

        foreach (var (width, height, inspector) in Sizes)
        {
            Resize(shell, width, height, inspector);
            Assert.True(bar.DesiredSize.Width <= bar.GetVisualParent<Control>()!.Bounds.Width + 0.5, $"The bar at {width}x{height} wants {bar.DesiredSize.Width} px.");
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
        Assert.Equal(["1 failed", "2 waiting", "2 interrupted"], canvas.BarChips.Select(chip => chip.Label));
        // Every group keeps its words in the bar and in the folded row, and the waiting chip's tooltip names its reasons.
        Assert.Equal("1 question, 1 reply", canvas.BarChips[1].Tip);
        // The folded row shows the most urgent group with the bar's own count, and its tooltip and name list every group in the bar's words.
        var row = Assert.Single(canvas.RowChips);
        Assert.Equal((NodeState.Failed, 1, "1 failed, 2 waiting, 2 interrupted"), (row.State, row.Count, row.Label));
        Assert.Equal("1 failed\n1 question, 1 reply\n2 interrupted", row.Tip);

        void Sweep()
        {
            var windows = new[] { 900.0, 1000, 1100, 1180, 1280, 1400, 1600, 2560 };
            foreach (var width in windows.Concat(windows.Reverse()))
            {
                foreach (var inspector in new double[] { 280, 320, 400, 520 })
                {
                    Resize(shell, width, 800, inspector);
                    AssertBottomRow(shell, $"{width}x800, inspector {inspector}, canvas {shell.Window.CanvasArea.Bounds.Width}, {canvas.BarSummary}");
                }
            }
        }

        Sweep();

        // A bar with room takes no step it does not need: it sums only the reasons to wait, and it keeps the rest. The line that
        // says what Run Workflow runs takes its room too.
        Resize(shell, 1600, 800, 320);
        Assert.True(shell.ShowsAny("FitToScreen"), "Fit left a bar with room.");
        Assert.True(shell.ShowsAny("RunStatus"), "The progress left a bar with room.");
        Assert.True(shell.ShowsAny("ShowMinimap"), "The view controls left a bar with room.");
        Assert.Equal(["1 failed", "2 waiting", "2 interrupted"], shell.Find<Control>("RunChips").GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == "RunChip").Select(AutomationProperties.GetName));

        // Another workflow's chips reach the bar with no change to the canvas's width.
        Resize(shell, 1180, 800, 320);
        shell.Window.ViewModel.NewWorkflow(shell.Window.ViewModel.Projects[0]);
        shell.Render();
        shell.Window.ViewModel.Select(canvas);
        shell.Render();
        AssertBottomRow(shell, "the return to the workflow");

        shell.RunNode("Runs");
        shell.WaitForState("Runs", NodeState.Running);
        Sweep();
        f.Release("runs");
        shell.WaitUntil(() => canvas.RunningCount == 0, "the node ends");
    }

    [AvaloniaFact]
    public void A_running_workflow_keeps_its_run_bar_in_the_bottom_band_beside_the_zoom_controls_at_every_size()
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
            AssertTopRow(shell, at, "Breadcrumb", "GenerateWorkflow");
            AssertBottomRow(shell, at);
        }
    }

    [AvaloniaFact]
    public void The_docked_conversation_keeps_a_few_lines_of_transcript_and_the_canvas_keeps_its_cards_clear_in_the_smallest_window()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        Shell.Invoke(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Render();
        Resize(shell, 900, 600, 320);

        shell.Click(shell.Find<Button>("ConversationLayout"));
        Assert.NotNull(shell.Window.ViewModel.DockedConversation);

        // The conversation keeps a few lines of its transcript, its header on two lines, and its buttons in their boxes.
        var transcript = shell.Bounds(shell.Find<Control>("TranscriptScroller"));
        Assert.True(transcript.Height >= ConversationView.MinTranscriptHeight - 0.5, $"The transcript is {transcript.Height} px tall.");
        var header = Shell.Around(shell.Find<TextBlock>("ConversationTitle"), "conversationHeader");
        Assert.All(header.GetVisualDescendants().OfType<SpillRow>(), row => Assert.True(row is { IsMeasureValid: true, IsArrangeValid: true }));
        var lines = Lines(header.GetVisualDescendants().OfType<Control>()
            .Where(control => AutomationProperties.GetAutomationId(control) is "ConversationTitle" or "ConversationStatus"
                or "ConversationLayout" or "CloseConversation")
            .Where(control => control is { IsEffectivelyVisible: true, Bounds.Width: > 0 })
            .Select(shell.Bounds));
        Assert.True(lines <= 2, $"The docked header has {lines} lines.");
        // The canvas's breadcrumb above names the project and the workflow, and the client's limitations sit behind the model chip.
        Assert.False(shell.Find<Control>("ConversationBreadcrumb").IsEffectivelyVisible);
        Assert.False(shell.Has<Control>("ClientLimitations"));
        Assert.True(shell.Find<Control>("ConversationModel").IsEffectivelyVisible);
        var box = shell.Bounds(Shell.Around(shell.Find<TextBox>("ConversationComposer"), "composer")).Deflate(1);
        foreach (var id in new[] { "ConversationComposer", "ConversationModel", "ConversationSend" })
        {
            Assert.True(box.Contains(shell.Bounds(shell.Find<Control>(id))), $"{id} at {shell.Bounds(shell.Find<Control>(id))} leaves the composer's box {box}.");
        }

        // The canvas above keeps its two bands apart, and the editor between them holds its cards.
        AssertTopRow(shell, "the dock", "Breadcrumb", "GenerateWorkflow");
        AssertBottomRow(shell, "the dock");
        var zoom = shell.Editor.ViewportZoom;
        Assert.All(shell.Nodes().Select(node => shell.Bounds(node).WithWidth(node.Bounds.Width * zoom).WithHeight(node.Bounds.Height * zoom)),
            card => Assert.True(shell.Bounds(shell.Editor).Inflate(0.5).Contains(card), $"The card at {card} leaves the editor."));

        // A taller window gives the canvas its 280 px before the dock grows to its default.
        Resize(shell, 900, 660);
        Assert.Equal(MainWindow.CanvasMinHeight, shell.Window.CanvasArea.Bounds.Height, 0.5);
        Resize(shell, 1600, 1000);
        Assert.Equal(400, shell.Window.MainArea.RowDefinitions[2].ActualHeight, 0.5);
        Assert.True(shell.Window.CanvasArea.Bounds.Height >= MainWindow.CanvasMinHeight);
    }

    [AvaloniaFact]
    public void Docking_fits_a_fitted_view_so_the_cards_and_the_proposals_ghosts_stay_in_the_editor()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        Resize(shell, 1600, 1000, 320);
        Shell.Invoke(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Render();

        shell.Click(shell.Find<Button>("ConversationLayout"));

        var cards = Cards(shell);
        Assert.Equal(7, cards.Length);
        Assert.All(cards, card => Assert.True(shell.Bounds(shell.Editor).Contains(card), $"The card at {card} leaves the editor."));
    }

    [AvaloniaFact]
    public void Docking_frames_the_node_s_branch_and_resizing_keeps_it_in_view()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        Resize(shell, 1600, 1000, 320);
        Shell.Invoke(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Render();
        // Zoomed in on "Design the API" in the upper middle, with the other two cards just showing at the canvas's right edge.
        shell.Editor.ViewportZoom = 1.5;
        shell.Editor.ViewportLocation = new Point(-220, 90);
        shell.Render();
        var zoom = shell.Editor.ViewportZoom;

        shell.Click(shell.Find<Button>("ConversationLayout"));
        shell.Render();
        // The conversation's node and its direct neighbours show whole, never closer than the person's zoom (ADR 0065).
        var node = shell.NodeModel("Write docs");
        var branch = shell.Window.ViewModel.Canvas!.Connections.Where(wire => wire.To == node).Select(wire => wire.From.Title).Append("Write docs");
        var editor = new Rect(shell.Editor.Bounds.Size);
        Assert.All(branch, title => Assert.True(editor.Contains(shell.CardRect(title)), $"{title} at {shell.CardRect(title)} leaves {editor}."));
        Assert.True(shell.Editor.ViewportZoom <= zoom);
        void BranchShows()
        {
            shell.Render();
            var room = new Rect(shell.Editor.Bounds.Size);
            Assert.All(branch, title => Assert.True(room.Contains(shell.CardRect(title)), $"{title} at {shell.CardRect(title)} leaves {room}."));
        }

        // A view that the dock framed fits again when the window or a splitter changes the canvas, so the branch stays whole.
        Resize(shell, 1590, 1000);
        BranchShows();
        var inspector = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Single(control => Grid.GetColumn(control) == 3);
        shell.Drag(shell.Center(inspector) + new Vector(0, 150), shell.Center(inspector) + new Vector(-40, 150));
        BranchShows();
        shell.Drag(shell.Center(shell.Window.DockSplitter), shell.Center(shell.Window.DockSplitter) - new Vector(0, 30));
        Assert.True(shell.Window.MainArea.RowDefinitions[2].ActualHeight > 400, "The dock's splitter did not move.");
        BranchShows();
    }

    [AvaloniaFact]
    public void Docking_that_cuts_a_card_fits_the_whole_graph_where_its_titles_read()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        Resize(shell, 1280, 800, 320);
        Shell.Invoke(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Render();
        var view = shell.Window.GetVisualDescendants().OfType<WorkflowCanvasView>().First();
        view.FitToView();
        shell.Render();
        // A view on the graph's left part, whose cards show whole before the dock takes the canvas's height.
        shell.Editor.ViewportLocation -= new Vector(60, 0);
        shell.Render();
        Assert.NotEmpty(view.CardsInView());

        shell.Click(shell.Find<Button>("ConversationLayout"));
        shell.Render();

        Assert.True(shell.Editor.ViewportZoom >= ZoomTiers.ReadableZoom, $"The docked view stays at {shell.Editor.ViewportZoom}.");
        var editor = shell.Bounds(shell.Editor);
        Assert.All(Cards(shell), card => Assert.True(editor.Contains(card), $"The card at {card} leaves {editor}."));
    }

    [AvaloniaFact]
    public void Docking_a_view_too_far_out_to_read_frames_the_node_s_branch_where_its_titles_read()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        Resize(shell, 1600, 1000, 320);
        Shell.Invoke(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Render();
        shell.Editor.ViewportZoom = 0.14;
        shell.Render();

        shell.Click(shell.Find<Button>("ConversationLayout"));
        shell.Render();

        // The view frames the branch closer, up to the bar's 100%, so the titles read beside the conversation (ADR 0069).
        Assert.True(shell.Editor.ViewportZoom >= ZoomTiers.ReadableZoom, $"The docked view stays at {shell.Editor.ViewportZoom}.");
        Assert.True(shell.Editor.ViewportZoom <= ZoomTiers.ActualSize + 0.001);
        var node = shell.NodeModel("Write docs");
        var branch = shell.Window.ViewModel.Canvas!.Connections.Where(wire => wire.To == node).Select(wire => wire.From.Title).Append("Write docs");
        var editor = new Rect(shell.Editor.Bounds.Size);
        Assert.All(branch, title => Assert.True(editor.Contains(shell.CardRect(title)), $"{title} at {shell.CardRect(title)} leaves {editor}."));
    }

    [AvaloniaFact]
    public void Fit_to_view_uses_the_room_a_long_status_leaves_on_the_docked_canvas()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        shell.AtOrigin();
        shell.Pan(shell.InEditor(260, 600), new Vector(-100, 0));
        shell.Drag(shell.Center(shell.Output("Write docs")), shell.Center(shell.Input("Design the API")));
        Assert.StartsWith("That would create a cycle", shell.Status);
        Shell.Invoke(shell.InCard<Button>("Write docs", "CardAttention"));
        shell.Render();
        shell.Click(shell.Find<Button>("ConversationLayout"));
        Resize(shell, 900, 660, 320);
        var (top, bottom) = Rows(shell);
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

    /// <summary>A fit keeps the cards 8 px inside the editor between the two bands.</summary>
    [AvaloniaTheory]
    [InlineData(1600, 600, 280, false, 8)]
    [InlineData(900, 700, 320, true, 8)]
    [InlineData(900, 600, 320, true, 8)]
    public void Fit_to_view_keeps_the_cards_inside_the_editor_between_the_bands(double width, double height, double inspector, bool docked, double margin)
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        FanOut.Generate(shell);
        if (docked)
        {
            Shell.Invoke(shell.InCard<Button>("Write docs", "CardAttention"));
            shell.Render();
            shell.Click(shell.Find<Button>("ConversationLayout"));
        }

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

    /// <summary>
    /// The top band's controls sit inside the canvas's inset, at least 8 px apart, those of the first row on one centre
    /// line, and none of their text is cut short. Add and Generate are 32 px glyphs that name themselves in their tooltips.
    /// </summary>
    private static void AssertTopRow(Shell shell, string at, params string[] ids)
    {
        var canvas = shell.Bounds(shell.Window.CanvasArea);
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
            var labels = button.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
            if (id == "RunWorkflow")
            {
                // The workflow's primary action keeps its one name at every width.
                Assert.Contains("Run Workflow", labels);
            }
            else if (compact)
            {
                Assert.Empty(labels);
                Assert.Equal((32.0, 32.0), (button.Bounds.Width, button.Bounds.Height));
            }
            else
            {
                Assert.Contains("Generate", labels);
            }

            Assert.Equal(AutomationProperties.GetName(button), ((string)ToolTip.GetTip(button)!).Split('\n')[0]);
        }

        Assert.Equal(compact, !shell.Find<TextBlock>("BreadcrumbProject").IsEffectivelyVisible);
        var texts = ids.SelectMany(id => shell.Find<Control>(id).GetVisualDescendants().OfType<TextBlock>()).Where(text => text.IsEffectivelyVisible);
        Assert.Empty(texts.Where(Trimmed).Select(text => $"At {at}, \"{text.Text}\" is cut short."));
    }

    /// <summary>
    /// The zoom controls and the workflow's run bar sit in the bottom band, under the editor and at least 12 px apart side by side,
    /// or 8 px apart when the bar takes its own row, with their text whole.
    /// </summary>
    private static void AssertBottomRow(Shell shell, string at)
    {
        var canvas = shell.Bounds(shell.Window.CanvasArea);
        var boxes = new[] { "ZoomPanel", "WorkflowRunBar" }.Select(id => (Id: id, Box: shell.Bounds(shell.Find<Control>(id)))).ToArray();
        // Beside the zoom controls the bar keeps 12 px from them. On its own row above them it keeps 8 px.
        var ownRow = boxes[1].Box.Bottom <= boxes[0].Box.Top + 0.5;
        AssertInsideAndApart(canvas, boxes, at, gap: ownRow ? 8 : 12);
        Assert.All(boxes, box => Assert.True(box.Box.Top >= shell.Bounds(shell.Editor).Bottom, $"At {at}, {box.Id} at {box.Box} is not under the editor."));

        // The bar holds its status, its chips, and whichever of its buttons show, its text whole.
        var bar = shell.Find<Control>("WorkflowRunBar");
        var inside = new[] { "RunStatus", "NextWaiting" }.Where(shell.ShowsAny).Select(shell.Find<Control>);
        Assert.All(inside, control => Assert.True(shell.Bounds(bar).Contains(shell.Bounds(control)), $"At {at}, {AutomationProperties.GetAutomationId(control)} leaves the run bar."));
        var chips = bar.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == "RunChip").ToArray();
        var limit = shell.ShowsAny("NextWaiting") ? shell.Bounds(shell.Find<Control>("NextWaiting")).Left : shell.Bounds(bar).Right;
        Assert.All(chips, chip => Assert.True(shell.Bounds(bar).Contains(shell.Bounds(chip)) && shell.Bounds(chip).Right <= limit, $"At {at}, the chip \"{AutomationProperties.GetName(chip)}\" at {shell.Bounds(chip)} is cut by the bar at {shell.Bounds(bar)}."));
        // Every count keeps its word at every width: a bar without room takes a second line, never a bare number.
        Assert.All(chips, chip => Assert.Equal([AutomationProperties.GetName(chip)],
            chip.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text)));
        if (shell.ShowsAny("StopWorkflow"))
        {
            Assert.Equal(canvas.Width >= CanvasChrome.CompactWidth, shell.Find<Button>("StopWorkflow").GetVisualDescendants().OfType<TextBlock>().Single().IsEffectivelyVisible);
        }

        var cut = bar.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && (Trimmed(text) || text.TextLayout.Width > text.Bounds.Width + 0.5))
            .Select(text => $"\"{text.Text}\" is {text.Bounds.Width} px wide and needs {text.TextLayout.Width}").ToList();
        Assert.True(cut.Count == 0, $"At {at}: {string.Join("; ", cut)}.");
    }

    private static void AssertInsideAndApart(Rect canvas, (string Id, Rect Box)[] boxes, string at, double gap = 8)
    {
        var room = canvas.Deflate(8);
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
