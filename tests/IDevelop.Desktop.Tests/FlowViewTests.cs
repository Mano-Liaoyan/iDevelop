using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>The Map's Flow view: its bands, its cards, the switch, and that it never writes a saved position.</summary>
[Collection(ProcessCollection.Name)]
public sealed class FlowViewTests
{
    private static readonly TaskId A = new(Guid.NewGuid()), B = new(Guid.NewGuid()), C = new(Guid.NewGuid()), D = new(Guid.NewGuid());

    private static NodeFixture Diamond() => new(
        NodeFixture.Task(A, "Plan the work", 105),
        NodeFixture.Task(B, "Build the left", 405),
        NodeFixture.Task(C, "Build the right", 405),
        NodeFixture.Task(D, "Review it all", 705),
        NodeFixture.Dependency(A, B), NodeFixture.Dependency(A, C), NodeFixture.Dependency(B, D), NodeFixture.Dependency(C, D));

    private static FlowView Pane(Shell shell) => shell.Find<FlowView>("FlowView");

    private static void ShowFlow(Shell shell)
    {
        shell.Window.ViewModel.Canvas!.Choose(MapMode.Flow);
        shell.Render();
    }

    private static string[] Bands(Shell shell) =>
        [.. Shell.ById<Button>(Pane(shell), "StageBand").Select(band => ((FlowBandViewModel)band.DataContext!).Stage.Title)];

    private static Control Card(Shell shell, string title) =>
        Shell.ById<Control>(Pane(shell), "FlowCard").Single(card => AutomationName(card) == title);

    private static string AutomationName(Control card) => Avalonia.Automation.AutomationProperties.GetName(card) ?? "";

    [AvaloniaFact]
    public void Flow_shows_a_band_for_each_stage_and_a_card_for_each_agent()
    {
        using var f = Diamond();
        var shell = f.Open();
        ShowFlow(shell);

        Assert.Equal(["Stage 1", "Stage 2", "Stage 3"], Bands(shell));
        Assert.Equal(4, Shell.ById<Control>(Pane(shell), "FlowCard").Count());
        Assert.Equal("0 of 1 done", Shell.ById<TextBlock>(Pane(shell), "StageBandCount").First().Text);
    }

    [AvaloniaFact]
    public void Flow_leaves_the_saved_positions_and_the_document_alone()
    {
        using var f = Diamond();
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        var saved = canvas.Workflow.Positions;
        var file = Directory.EnumerateFiles(Path.Combine(f.Project, ".idp", "workflows"), "*.json").Single();
        var bytes = File.ReadAllBytes(file);

        shell.Press(Key.G);
        shell.Render();
        Assert.True(canvas.IsFlow);
        shell.Click(Card(shell, "Build the left"));
        canvas.Flow.StepDensity(-1);
        canvas.Stages[0].ToggleFold();
        shell.Render();
        shell.Press(Key.G);
        shell.Render();

        Assert.Same(saved, canvas.Workflow.Positions);
        Assert.False(canvas.HasUnsavedChanges);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [AvaloniaFact]
    public void The_switch_saves_the_view_for_the_workflow_on_this_machine()
    {
        using var f = Diamond();
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        var file = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.json");
        try
        {
            shell.Click(shell.Find<RadioButton>("ViewFlow"));
            shell.Render();

            Assert.True(canvas.IsFlow);
            Assert.Equal(MapMode.Flow, canvas.ModeChoice);
            var workflow = new WorkflowRef(f.Project, canvas.Workflow.Id);
            (WorkspaceSession.Empty with { Views = [new(workflow, MapMode.Flow)] }).Write(file);
            var view = Assert.Single(WorkspaceSession.Read(file).Views);
            Assert.True(view.Workflow.Is(f.Project, canvas.Workflow.Id));
            Assert.Equal(MapMode.Flow, view.Mode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [AvaloniaFact]
    public void A_workflow_that_a_Graph_fit_cannot_name_opens_in_Flow_and_a_small_one_in_Graph()
    {
        Assert.True(ZoomTiers.HidesNames(ZoomTier.Block));
        Assert.False(ZoomTiers.HidesNames(ZoomTier.Plate));
        using var f = new NodeFixture(
            [.. Enumerable.Range(0, 60).Select(index => (WorkflowEdit)NodeFixture.Task(new TaskId(Guid.NewGuid()), $"Node {index}", 105 + 340 * index))]);
        var large = Shell.OpenFitted(f.Project).Window.ViewModel.Canvas!;
        Assert.True(large.IsFlow);
        Assert.Null(large.ModeChoice);

        using var small = Diamond();
        var graph = Shell.OpenFitted(small.Project).Window.ViewModel.Canvas!;
        Assert.True(graph.IsGraph);
    }

    [AvaloniaFact]
    public void A_click_selects_a_card_and_the_arrows_walk_the_stages()
    {
        using var f = Diamond();
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        ShowFlow(shell);

        shell.Click(Card(shell, "Plan the work"));
        Assert.Equal("Plan the work", canvas.SelectedNode?.Title);

        shell.Press(Key.Down);
        var build = canvas.SelectedNode!.Title;
        Assert.StartsWith("Build the", build);
        shell.Press(Key.Right);
        Assert.StartsWith("Build the", canvas.SelectedNode!.Title);
        Assert.NotEqual(build, canvas.SelectedNode!.Title);
        shell.Press(Key.Down);
        Assert.Equal("Review it all", canvas.SelectedNode?.Title);
    }

    [AvaloniaFact]
    public void Enter_opens_Chat_and_Shift_Enter_the_Room_from_a_selected_card()
    {
        using var f = Diamond();
        var shell = f.Open();
        ShowFlow(shell);
        shell.Click(Card(shell, "Plan the work"));
        shell.Render();

        shell.Press(Key.Enter, RawInputModifiers.Shift);
        shell.Render();

        Assert.True(shell.Window.ViewModel.AgentPanel.IsRoom);
    }

    [AvaloniaFact]
    public void A_folded_stage_is_a_wire_end_and_folds_with_the_Index()
    {
        using var f = Diamond();
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        ShowFlow(shell);
        canvas.Stages[1].ToggleFold();
        shell.Render();

        Assert.Equal(2, Shell.ById<Control>(Pane(shell), "FlowCard").Count());
        var band = canvas.Flow.BandOf(canvas.Stages[1])!;
        Assert.Contains(canvas.Flow.Wires, wire => wire.Points[^1] == new Point(band.X + band.Width / 2, band.Y));
        Assert.DoesNotContain(canvas.Flow.Cards, card => card.Node.Title == "Build the left");
    }

    [AvaloniaFact]
    public void When_a_stage_wraps_only_the_selected_card_s_wires_draw_and_other_cards_dim()
    {
        var ids = Enumerable.Range(0, 8).Select(_ => new TaskId(Guid.NewGuid())).ToList();
        var source = new TaskId(Guid.NewGuid());
        using var f = new NodeFixture(
            [NodeFixture.Task(source, "Start", 0), .. ids.Select((id, index) => (WorkflowEdit)NodeFixture.Task(id, $"Node {index}", 300 + index * 300)), .. ids.Select(id => (WorkflowEdit)NodeFixture.Dependency(source, id))]);
        var shell = f.Open();
        shell.Window.Width = 700;
        shell.Render();
        var canvas = shell.Window.ViewModel.Canvas!;
        ShowFlow(shell);
        Assert.True(canvas.Flow.Plan!.Grid.Wraps);
        Assert.Empty(canvas.Flow.Wires);

        shell.Click(Card(shell, "Node 3"));
        shell.Render();

        Assert.Single(canvas.Flow.Wires);
        Assert.Contains(canvas.Flow.Cards, card => card.Node.Title == "Node 4" && card.IsDimmed);
        Assert.Contains(canvas.Flow.Cards, card => card.Node.Title == "Start" && !card.IsDimmed);
    }

    [AvaloniaFact]
    public void Flow_shows_a_card_and_chip_with_no_clipped_title()
    {
        using var f = Diamond();
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        ShowFlow(shell);

        var card = Card(shell, "Plan the work");
        Assert.Equal(FlowLayout.CardHeight, card.Bounds.Height);
        canvas.Flow.StepDensity(-1);
        shell.Render();

        Assert.Equal(FlowLayout.ChipHeight, Card(shell, "Plan the work").Bounds.Height);
        Assert.False(canvas.Flow.CanStepDown);
        Assert.True(canvas.Flow.CanStepUp);
    }

    [AvaloniaFact]
    public void The_50_agent_workflow_shows_every_name_as_a_chip_at_1280x800_with_no_overlap()
    {
        int[] sizes = [4, 6, 8, 6, 8, 8, 6, 4];
        var stages = sizes.Select(size => Enumerable.Range(0, size).Select(_ => new TaskId(Guid.NewGuid())).ToList()).ToList();
        var places = stages.SelectMany((stage, depth) => stage.Select((id, at) => (WorkflowEdit)NodeFixture.Task(id, $"Agent {depth} {at} with a long working name", 105 + depth * 340 + at)));
        var wires = stages.Zip(stages.Skip(1)).SelectMany(pair => pair.Second.Select(to => (WorkflowEdit)NodeFixture.Dependency(pair.First[0], to)));
        using var f = new NodeFixture([.. places, .. wires]);
        var shell = f.Open();
        shell.Window.Width = 1280;
        shell.Window.Height = 800;
        shell.Render();
        var canvas = shell.Window.ViewModel.Canvas!;
        ShowFlow(shell);

        Assert.Equal(FlowDensity.Chip, canvas.Flow.Density);
        var cards = Shell.ById<Control>(Pane(shell), "FlowCard").ToList();
        Assert.Equal(50, cards.Count);
        Assert.All(cards, card => Assert.Equal(FlowLayout.ChipHeight, card.Bounds.Height));
        Assert.All(Shell.ById<Theme.EllipsisText>(Pane(shell), "FlowCardTitle"), title => Assert.False(string.IsNullOrEmpty(title.Text)));
        var boxes = cards.Select(shell.Bounds).ToList();
        for (var a = 0; a < boxes.Count; a++)
        {
            for (var b = a + 1; b < boxes.Count; b++)
            {
                Assert.False(boxes[a].Intersects(boxes[b]), $"{boxes[a]} overlaps {boxes[b]}");
            }
        }
    }
}
