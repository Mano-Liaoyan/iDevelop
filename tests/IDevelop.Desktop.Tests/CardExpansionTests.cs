using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Expanded cards (#94, ADR 0024): a card unfolds in place to 320 px and its content, its neighbours slide aside on screen
/// only, its ports stay on its header, it answers its agent through the conversation's own path, and it stays expanded
/// after a restart. A collapsed card's tooltip is a read-only peek.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class CardExpansionTests : IDisposable
{
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Docs = TestTasks.Review;
    private static readonly TaskId Checks = new(Guid.Parse("019a9d2e-5d10-7000-8000-000000000044"));
    private static readonly TaskId Ship = new(Guid.Parse("019a9d2e-5e20-7000-8000-000000000055"));
    private static readonly TaskId Notes = new(Guid.Parse("019a9d2e-5f30-7000-8000-000000000066"));

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The six-task diamond the design used: Design, then Build, Docs, and Checks in one column, Ship after Build and Docs,
    /// and Notes after Ship. Columns are 300 px apart and rows 120 px. Design's context wire to Ship passes Docs.
    /// </summary>
    private string Diamond() => _temp.Seed(
        TaskAt(Design, "Design", 60, 210, Codex, "Propose a JSON layout for tasks, connections, and canvas positions. Keep multi-line text readable in a plain text editor."),
        TaskAt(Build, "Build", 360, 90, Codex, "Parse the format into the workflow model and reject unknown fields with their line number."),
        TaskAt(Docs, "Docs", 360, 210, Codex, "Write the workflow file through a temporary file and a rename, so a crash never leaves half a file."),
        TaskAt(Checks, "Checks", 360, 330, Codex, "Kill the writer at each step of a save and check that the last good file still opens."),
        TaskAt(Ship, "Ship", 660, 150, Codex, "Use the parser and the atomic save in the app's open and save commands."),
        TaskAt(Notes, "Notes", 960, 150, Codex, "Write the release notes."),
        new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Design, Docs), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Design, Checks), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Build, Ship), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Docs, Ship), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Ship, Notes), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Design, Ship), ConnectionKind.Context));

    private static string WorkflowFile(string project) => Directory.EnumerateFiles(Path.Combine(project, ".idp", "workflows"), "*.json").Single();

    [AvaloniaFact]
    public void An_expanded_card_unfolds_to_320_px_and_its_neighbours_slide_aside_on_screen_while_the_saved_positions_stay()
    {
        var project = Diamond();
        var saved = File.ReadAllBytes(WorkflowFile(project));
        var shell = Shell.Open(project);
        var canvas = shell.Window.ViewModel.Canvas!;

        shell.Click(shell.InCard<ToggleButton>("Docs", "CardExpand"));

        var docs = shell.Node("Docs");
        Assert.Equal(WorkflowCanvasViewModel.ExpandedCardWidth, docs.Bounds.Width);
        Assert.True(docs.Bounds.Height > WorkflowCanvasViewModel.TaskCardHeight, $"Docs is {docs.Bounds.Height} px tall.");
        // The card below Docs in its column keeps its 56 px gap below Docs's new bottom; the card above stays.
        Assert.Equal(new Point(360, 90), shell.Node("Build").Location);
        Assert.Equal(new Point(360, 210 + docs.Bounds.Height + 56), shell.Node("Checks").Location);
        // The columns after the wider card slide right by its extra 80 px.
        Assert.Equal((new Point(740, 150), new Point(1040, 150)), (shell.Node("Ship").Location, shell.Node("Notes").Location));
        Assert.Equal(new Point(60, 210), shell.Node("Design").Location);
        Assert.Empty(Overlaps(shell));
        // The canvas's check for cards under its floating controls measures each card where and as large as it shows.
        var view = shell.Window.GetVisualDescendants().OfType<WorkflowCanvasView>().Single();
        Assert.Contains(new Rect(docs.Bounds.Position, docs.Bounds.Size), view.CardsInView());
        Assert.Contains(new Rect(shell.Node("Checks").Location, shell.Node("Checks").Bounds.Size), view.CardsInView());

        // The workflow keeps its positions, so nothing is unsaved and the file is as it was.
        Assert.Equal(new CanvasPoint(360, 330), canvas.Workflow.Positions[Checks]);
        Assert.Equal(new CanvasPoint(660, 150), canvas.Workflow.Positions[Ship]);
        Assert.False(canvas.Document.HasUnsavedChanges);
        Assert.False(shell.ShowsUnsavedChanges);

        shell.Click(shell.InCard<ToggleButton>("Docs", "CardExpand"));

        Assert.Equal(
            [new Point(60, 210), new Point(360, 90), new Point(360, 210), new Point(360, 330), new Point(660, 150), new Point(960, 150)],
            new[] { "Design", "Build", "Docs", "Checks", "Ship", "Notes" }.Select(title => shell.Node(title).Location));
        Assert.Equal(WorkflowCanvasViewModel.TaskCardWidth, shell.Node("Docs").Bounds.Width);
        Assert.Equal(saved, File.ReadAllBytes(WorkflowFile(project)));
    }

    [AvaloniaFact]
    public void Expand_All_unfolds_every_card_with_no_overlap_and_no_cut_text_and_a_new_card_still_starts_collapsed()
    {
        var shell = Shell.Open(Diamond());
        var canvas = shell.Window.ViewModel.Canvas!;
        var expandAll = shell.Find<Button>("ExpandAll");
        Assert.Equal("Expand All", Avalonia.Automation.AutomationProperties.GetName(expandAll));

        shell.Click(expandAll);

        Assert.All(shell.Nodes(), node => Assert.Equal(WorkflowCanvasViewModel.ExpandedCardWidth, node.Bounds.Width));
        Assert.Empty(Overlaps(shell));
        Assert.Empty(CutTexts(shell));
        Assert.Equal("Collapse All", Avalonia.Automation.AutomationProperties.GetName(expandAll));
        Assert.False(canvas.Document.HasUnsavedChanges);

        canvas.Edit(TestNodes.Place(TestNodes.Implement(new TaskId(Guid.Parse("019a9d2e-6040-7000-8000-000000000077")), "Later"), new CanvasPoint(60, 600)));
        shell.Render();
        Assert.Equal(WorkflowCanvasViewModel.TaskCardWidth, shell.Node("Later").Bounds.Width);
        Assert.False(canvas.Nodes.Single(node => node.Title == "Later").IsExpanded);
        Assert.Empty(Overlaps(shell));

        shell.Click(expandAll);

        Assert.All(shell.Nodes(), node => Assert.Equal(WorkflowCanvasViewModel.TaskCardWidth, node.Bounds.Width));
        Assert.Equal("Expand All", Avalonia.Automation.AutomationProperties.GetName(expandAll));
    }

    [AvaloniaFact]
    public void A_cards_ports_stay_on_its_header_line_so_its_wires_keep_their_rows_when_it_unfolds()
    {
        // Design's context wire to Archive, below and right of Docs, would pass under the collapsed Docs, through its body.
        var archive = new TaskId(Guid.Parse("019a9d2e-6150-7000-8000-000000000088"));
        var project = Diamond();
        var document = Projects.WorkflowDocument.OpenProject(project).Single();
        document.Apply(TaskAt(archive, "Archive", 660, 420, Codex, "Keep the old format readable."));
        document.Apply(new Connect(new ConnectionKey(Design, archive), ConnectionKind.Context));
        document.Save();
        var shell = Shell.Open(project);
        var docs = shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == "Docs");
        shell.Render();
        var (input, output) = (docs.Input.Anchor, docs.Output.Anchor);
        Assert.Equal((new Point(370, 242), new Point(610, 242)), (input, output));

        shell.Click(shell.InCard<ToggleButton>("Docs", "CardExpand"));

        Assert.Equal(input, docs.Input.Anchor);
        Assert.Equal(output + new Vector(WorkflowCanvasViewModel.ExpandedCardWidth - WorkflowCanvasViewModel.TaskCardWidth, 0), docs.Output.Anchor);
        // Nodify keeps a port's anchor current only while a wire uses the port.
        foreach (var node in shell.Nodes())
        {
            var model = (TaskNodeViewModel)node.DataContext!;
            if (model.Input.IsConnected)
            {
                Assert.Equal(node.Location + new Vector(10, 32), model.Input.Anchor);
            }

            if (model.Output.IsConnected)
            {
                Assert.Equal(node.Location + new Vector(node.Bounds.Width - 10, 32), model.Output.Anchor);
            }
        }

        // No wire runs through the unfolded card's body.
        var body = new Rect(shell.Node("Docs").Location + new Vector(10, WorkflowCanvasViewModel.TaskCardHeight), new Size(320, shell.Node("Docs").Bounds.Height - 64));
        Assert.DoesNotContain(shell.Window.ViewModel.Canvas!.Connections, connection => Crosses(connection, body));
    }

    [AvaloniaFact]
    public void Keys_typed_in_the_answer_box_and_on_the_agent_pickers_stay_with_them_and_never_reach_the_canvas()
    {
        using var f = new WorkflowRunFixture(Task(Design, "A", 105, ConversationMode.Chat));
        f.Answer("A", f.Says("Which file?"));
        var shell = f.Window();
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.Nodes.Single().RunOnItsOwn();
        shell.WaitForCard("A", "Waiting for you");
        shell.Click(shell.InCard<ToggleButton>("A", "CardExpand"));
        var zoom = canvas.ViewportZoom;

        shell.Click(shell.InCard<TextBox>("A", "CardAnswer"));
        shell.PressWithText(Key.N, "n");
        shell.PressWithText(Key.F, "f");
        shell.Press(Key.Back);
        shell.Press(Key.Delete);

        Assert.Equal("n", shell.InCard<TextBox>("A", "CardAnswer").Text);
        Assert.Null(canvas.AddNode);
        Assert.Equal(zoom, canvas.ViewportZoom);
        Assert.Single(canvas.Nodes);

        shell.InCard<ComboBox>("A", "CardPickModel").Focus();
        shell.Press(Key.Delete);
        shell.Press(Key.F);

        Assert.Single(canvas.Nodes);
        Assert.Equal(zoom, canvas.ViewportZoom);

        // A press and drag that starts in the answer box selects its text and leaves the card where it was.
        var box = shell.InCard<TextBox>("A", "CardAnswer");
        shell.Drag(shell.Center(box), shell.Center(box) + new Vector(120, 60));
        Assert.Equal(new CanvasPoint(105, 90), canvas.Workflow.Positions[Design]);
        Assert.False(canvas.Document.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void The_expanded_cards_pickers_change_the_tasks_agent_as_the_inspectors_do_and_a_press_on_them_never_moves_the_card()
    {
        var fakes = new FakeClients(_temp.Create("bin"));
        FakeAgents.Install(fakes, ClientId.Codex);
        FakeAgents.Install(fakes, ClientId.Antigravity);
        var folder = _temp.Seed(TaskAt(Design, "Design", 105, 90, Codex, "Propose a JSON layout."));
        var shell = Shell.Open(folder, fakes.DiscoverAsync().Result);
        var canvas = shell.Window.ViewModel.Canvas!;
        shell.Click(shell.InCard<ToggleButton>("Design", "CardExpand"));
        ComboBox Picker(string automationId) => shell.InCard<ComboBox>("Design", automationId);

        Assert.Equal(["GPT-6.1-Sol", "GPT-6-Sol", "GPT-5.5"], shell.Pick(Picker("CardPickModel"), "GPT-6-Sol"));
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], shell.Pick(Picker("CardPickReasoning"), "max"));

        // The card's choice is the task's, as the inspector's is: the workflow changes and the inspector shows it.
        Assert.Equal(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "max" }, canvas.Workflow.Tasks[Design].Execution);
        Assert.Equal("Codex · GPT-6-Sol · max", shell.CardAgent("Design"));
        Assert.True(canvas.Document.HasUnsavedChanges);
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal(["Codex", "GPT-6-Sol", "max"], new[] { "TaskClient", "TaskModel", "TaskReasoning" }.Select(shell.Picked));

        Assert.Equal(
            ["None", "Claude Code · not installed", "Codex", "Pi · not installed", "Antigravity CLI"],
            shell.Pick(Picker("CardPickClient"), "Antigravity CLI"));
        Assert.Equal(ClientId.Antigravity, canvas.Workflow.Tasks[Design].Execution!.Client);
        Assert.Equal("Antigravity CLI", shell.Picked("TaskClient"));

        // The picker keeps the focus after a choice. A wheel notch over it zooms the canvas and chooses nothing.
        var chosen = canvas.Workflow.Tasks[Design].Execution;
        var zoom = canvas.ViewportZoom;
        shell.Window.MouseWheel(shell.Center(Picker("CardPickClient")), new Vector(0, 1));
        shell.Render();
        Assert.Equal(chosen, canvas.Workflow.Tasks[Design].Execution);
        Assert.NotEqual(zoom, canvas.ViewportZoom);

        // A press that starts on a picker or on a button and drags leaves the card where it was.
        var placed = canvas.Workflow.Positions[Design];
        shell.Drag(shell.Center(Picker("CardPickModel")), shell.Center(Picker("CardPickModel")) + new Vector(120, 60));
        shell.Drag(shell.Center(shell.InCard<Button>("Design", "CardOpenConversation")),
            shell.Center(shell.InCard<Button>("Design", "CardOpenConversation")) + new Vector(120, 60));
        Assert.Equal(placed, canvas.Workflow.Positions[Design]);
        Assert.Equal(new Point(105, 90), shell.Node("Design").Location);
    }

    [AvaloniaFact]
    public void While_a_task_runs_a_change_on_its_cards_pickers_is_for_the_next_run_and_the_card_names_the_agent_that_runs_now()
    {
        using var f = new WorkflowRunFixture(Task(Design, "A", 105));
        f.Answer("A", f.Says("A ready.", gate: "go"));
        var shell = f.Window();
        shell.RunOnItsOwn("A");
        shell.WaitForCard("A", "Running");
        shell.Click(shell.InCard<ToggleButton>("A", "CardExpand"));
        Assert.Equal("A change applies to the next run.", shell.CardText("A", "CardNextRunNote"));

        shell.Pick(shell.InCard<ComboBox>("A", "CardPickModel"), "GPT-6-Sol");

        Assert.Equal("GPT-6-Sol · high", shell.CardText("A", "CardModel"));
        Assert.Equal("This run uses Codex · GPT-5.5 · high. A change applies to the next run.", shell.CardText("A", "CardNextRunNote"));
        f.Open("go");
        shell.WaitForCard("A", "Succeeded");
        Assert.False(shell.InCard<TextBlock>("A", "CardNextRunNote").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void An_approval_card_points_to_the_inspector_where_the_person_answers_its_request()
    {
        using var f = new WorkflowRunFixture(Task(Design, "A", 105), Approval(Build, "Approve", 405), Dependency(Design, Build));
        f.Answer("A", f.Says("A ready."));
        var shell = f.Window();
        shell.Click(shell.InCard<ToggleButton>("Approve", "CardExpand"));
        Assert.False(shell.InCard<Button>("Approve", "CardShowInspector").IsEffectivelyVisible);
        shell.StartRun();
        shell.WaitForCard("Approve", "Waiting for approval");

        Assert.Equal("Waits for your approval", shell.CardText("Approve", "CardNowHeading"));
        shell.Click(shell.InCard<Button>("Approve", "CardShowInspector"));

        Assert.Equal("Approve", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.True(shell.InView<Button>("ApproveGate").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_waiting_cards_list_counts_what_a_listed_task_waits_for_so_the_listed_title_keeps_one_line()
    {
        const string first = "Design the workflow file format";
        using var f = new WorkflowRunFixture(Task(Design, first, 105), Task(Build, "Check the storage format", 405),
            TaskAt(Docs, "C", 405, 250, Codex, "Build C."),
            Dependency(Design, Build), Dependency(Build, Docs));
        f.Answer(first, f.Says("Designed.", gate: "go")).Answer("Check the storage format", f.Says("Checked.")).Answer("C", f.Says("C ready."));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard(first, "Running");

        shell.Click(shell.InCard<ToggleButton>("C", "CardExpand"));

        // The card's own status names the task it waits for; the row beside a title only counts it.
        Assert.Equal($"Waits for \"{first}\"", ((TaskNodeViewModel)shell.Node("Check the storage format").DataContext!).StatusLabel);
        var waits = shell.InCard<ItemsControl>("C", "CardWaits");
        Assert.Equal(["Check the storage format", "Waits for 1 task"], Shell.Texts(waits));
        Assert.Single(waits.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Check the storage format").TextLayout.TextLines);
        f.Open("go");
        shell.WaitForStatus("Completed");
    }

    [AvaloniaFact]
    public void A_waiting_cards_list_follows_a_listed_task_whose_change_leaves_the_cards_own_status_as_it_was()
    {
        using var f = new WorkflowRunFixture(Task(Design, "A", 105, ConversationMode.Chat), TaskAt(Build, "B", 105, 250, Codex, "Build B."),
            Task(Docs, "C", 405), Dependency(Design, Docs), Dependency(Build, Docs));
        f.Answer("A", f.Says("Which file?", gate: "a")).Answer("B", f.Says("B ready.", gate: "b")).Answer("C", f.Says("C ready."));
        var shell = f.Window();
        shell.Click(shell.InCard<ToggleButton>("C", "CardExpand"));
        shell.StartRun();
        shell.WaitUntil(() => Shell.Texts(shell.InCard<ItemsControl>("C", "CardWaits")) is ["A", "Running", "B", "Running"], "C lists both running",
            () => string.Join(", ", Shell.Texts(shell.InCard<ItemsControl>("C", "CardWaits"))));

        // A stops to wait for the person while B still runs, so C still waits for 2 tasks, and its list says where A is now.
        f.Open("a");
        shell.WaitForCard("A", "Waiting for you");

        Assert.Equal("Waits for 2 tasks", shell.CardText("C", "CardStatus"));
        shell.WaitUntil(() => Shell.Texts(shell.InCard<ItemsControl>("C", "CardWaits")) is ["A", "Waiting for you", "B", "Running"], "C's list follows A",
            () => string.Join(", ", Shell.Texts(shell.InCard<ItemsControl>("C", "CardWaits"))));
        f.Open("b");
    }

    [AvaloniaFact]
    public void Ctrl_Enter_in_the_answer_box_sends_the_answer_as_the_agents_next_turn()
    {
        using var f = new WorkflowRunFixture(Task(Design, "A", 105, ConversationMode.Chat));
        f.Answer("A", f.Says("Which file?"), f.Says("Done.", "session-1")).Route("Use the fixture", "A");
        var shell = f.Window();
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.Nodes.Single().RunOnItsOwn();
        shell.WaitForCard("A", "Waiting for you");
        shell.Click(shell.InCard<ToggleButton>("A", "CardExpand"));
        Assert.Equal(("The agent replied", "Which file?"), (shell.CardText("A", "CardNowHeading"), shell.CardText("A", "CardNowText")));

        shell.Click(shell.InCard<TextBox>("A", "CardAnswer"));
        shell.Type("Use the fixture");
        shell.Press(Key.Enter, RawInputModifiers.Control);

        shell.WaitUntil(() => f.Launches("A") == 2 && shell.CardText("A", "CardNowText") == "Done.", "the answer's turn ends");
        Assert.Equal("Use the fixture", f.Prompt("A", 2));
        Assert.Equal("", shell.InCard<TextBox>("A", "CardAnswer").Text);
        Assert.Null(shell.Window.ViewModel.Conversation);
        Assert.Single(canvas.Nodes);
    }

    [AvaloniaFact]
    public void The_card_answers_a_task_that_a_run_owns_through_the_run()
    {
        using var f = new WorkflowRunFixture(Task(Design, "A", 105, ConversationMode.Chat), Task(Build, "B", 405), Dependency(Design, Build));
        f.Answer("A", f.Says("Which file?"), f.Says("Done.", "session-1")).Answer("B", f.Says("B ready.")).Route("Use the fixture", "A");
        var shell = f.Window();
        // Expanded before the run starts, the card follows the task into the run.
        shell.Click(shell.InCard<ToggleButton>("A", "CardExpand"));
        shell.StartRun();
        shell.WaitForCard("A", "Waiting for you");
        shell.WaitUntil(() => shell.CardText("A", "CardNowText") == "Which file?", "the card shows the run's reply");

        shell.Click(shell.InCard<TextBox>("A", "CardAnswer"));
        shell.Type("Use the fixture");
        shell.WaitUntil(() => shell.InCard<Button>("A", "CardSend").IsEffectivelyEnabled, "the run takes a reply");
        shell.Click(shell.InCard<Button>("A", "CardSend"));

        shell.WaitUntil(() => f.Launches("A") == 2 && shell.CardText("A", "CardNowText") == "Done.", "the reply's turn ends in the run");
        Assert.Equal("Use the fixture", f.Prompt("A", 2));
        Assert.Null(shell.Window.ViewModel.Conversation);
    }

    [AvaloniaFact]
    public void Expanded_cards_stay_expanded_after_a_restart_and_the_workflow_file_never_changes()
    {
        var project = Diamond();
        var saved = File.ReadAllBytes(WorkflowFile(project));
        var shell = Shell.Open(project);
        shell.Click(shell.InCard<ToggleButton>("Docs", "CardExpand"));
        shell.Click(shell.InCard<ToggleButton>("Build", "CardExpand"));
        shell.Window.Close();
        shell.Render();

        var reopened = Shell.Show();
        reopened.Window.ViewModel.Restore(null);
        reopened.Render();

        Assert.Equal(["Build", "Docs"], reopened.Window.ViewModel.Canvas!.Nodes.Where(node => node.IsExpanded).Select(node => node.Title).Order());
        Assert.Equal(WorkflowCanvasViewModel.ExpandedCardWidth, reopened.Node("Docs").Bounds.Width);
        Assert.False(reopened.ShowsUnsavedChanges);
        Assert.Equal(saved, File.ReadAllBytes(WorkflowFile(project)));
    }

    [AvaloniaFact]
    public void Resting_the_pointer_on_a_collapsed_card_opens_its_peek_and_leaving_closes_it()
    {
        var shell = Shell.Open(Diamond());
        var card = shell.InCard<Panel>("Docs", "TaskCard");

        shell.Window.MouseMove(shell.Header(shell.Node("Docs")));
        shell.WaitUntil(() => ToolTip.GetIsOpen(card), "the peek opens");

        var peek = Assert.IsType<CardPeek>(ToolTip.GetTip(card));
        Assert.Equal(
            ["Docs", "Implement · Codex · gpt-5.5 · high", "Instructions",
             "Write the workflow file through a temporary file and a rename, so a crash never leaves half a file.",
             // Run starts nothing before Design has a result, which the peek says last.
             WorkflowRunText.Unbroken("Runs after \"Design\". Run \"Design\"\u00A0first.")],
            Shell.Texts(peek));

        shell.Window.MouseMove(new Point(5, 5));
        shell.WaitUntil(() => !ToolTip.GetIsOpen(card), "the peek closes");

        shell.Click(shell.InCard<ToggleButton>("Docs", "CardExpand"));
        Assert.Null(ToolTip.GetTip(shell.InCard<Panel>("Docs", "TaskCard")));
    }

    /// <summary>Every pair of cards whose visible boxes, the containers less their 10 px port gutters, overlap.</summary>
    private static (string, string)[] Overlaps(Shell shell)
    {
        var boxes = shell.Nodes().Select(node => (((TaskNodeViewModel)node.DataContext!).Title,
            Box: new Rect(node.Location + new Vector(10, 0), new Size(node.Bounds.Width - 20, node.Bounds.Height)))).ToList();
        return [.. boxes.SelectMany((a, i) => boxes.Skip(i + 1).Where(b => a.Box.Intersects(b.Box)).Select(b => (a.Title, b.Title)))];
    }

    /// <summary>The texts on cards that end in an ellipsis or run past their own box.</summary>
    private static string[] CutTexts(Shell shell) =>
        [.. shell.Nodes().SelectMany(node => node.GetVisualDescendants().OfType<TextBlock>())
            .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))
            .Where(text => text.TextLayout.TextLines.Any(line => line.HasCollapsed) || text.TextLayout.Width > text.Bounds.Width + 0.5)
            .Select(text => text.Text!)];

    private static bool Crosses(ConnectionViewModel connection, Rect body)
    {
        Point[] corners = [connection.From.Output.Anchor, .. connection.Route, connection.To.Input.Anchor];
        return corners.Zip(corners.Skip(1)).Any(segment => new Rect(segment.First, segment.Second).Normalize().Intersects(body));
    }
}
