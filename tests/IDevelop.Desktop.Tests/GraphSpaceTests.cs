using Avalonia;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>Graph's display compaction: saved positions shown at 1:1 cards, and saved back only when a card moves (brief S2).</summary>
public sealed class GraphSpaceTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_fixture_spacing_compacts_to_its_scale()
    {
        var points = Enumerable.Range(0, 4).SelectMany(column => Enumerable.Range(0, 3).Select(row => new CanvasPoint(column * 340, row * 190))).ToArray();

        var space = GraphSpace.Of(points);

        Assert.Equal(0.776, space.Kx, 3);
        Assert.Equal(0.632, space.Ky, 3);
    }

    [Fact]
    public void A_scale_never_stretches_a_layout()
    {
        CanvasPoint[] points = [new(0, 0), new(100, 0), new(0, 50), new(100, 50)];

        Assert.Equal(GraphSpace.Identity, GraphSpace.Of(points));
    }

    [Fact]
    public void A_layout_with_nothing_to_compact_stays_at_its_own_scale()
    {
        CanvasPoint[] points = [new(0, 0), new(500, 500)];

        Assert.Equal(GraphSpace.Identity, GraphSpace.Of(points));
    }

    [Fact]
    public void A_pair_that_overlaps_on_neither_axis_stays_apart_after_compaction()
    {
        // The first three cards set both scales below the diagonal pair's own distance.
        CanvasPoint[] points = [new(0, 0), new(300, 0), new(0, 200), new(250, 110)];

        var space = GraphSpace.Of(points);

        var shown = points.Select(point => space.ToDisplay(point)).ToArray();
        for (var first = 0; first < shown.Length; first++)
        {
            for (var second = first + 1; second < shown.Length; second++)
            {
                var (dx, dy) = (Math.Abs(shown[first].X - shown[second].X), Math.Abs(shown[first].Y - shown[second].Y));
                Assert.True(dx >= 220 || dy >= 94, $"Cards {first} and {second} overlap at {dx}, {dy}.");
            }
        }
    }

    [Fact]
    public void Display_and_saved_positions_round_trip()
    {
        var space = new GraphSpace(0.776, 0.632);
        var saved = new CanvasPoint(341.5, 123.25);

        var back = space.ToSaved(space.ToDisplay(saved));

        Assert.Equal(saved.X, back.X, 8);
        Assert.Equal(saved.Y, back.Y, 8);
    }

    [AvaloniaFact]
    public void A_card_shows_at_its_saved_position_times_the_scale()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(TestTasks.Design, "Design", 100, 90),
            TaskAt(TestTasks.Build, "Build", 460, 90)));
        var canvas = shell.Window.ViewModel.Canvas!;
        var kx = canvas.Space.Kx;

        Assert.True(kx < 1);
        Assert.Equal(new Point(100 * kx, 90 * canvas.Space.Ky), shell.NodeModel("Design").Location);
    }

    [AvaloniaFact]
    public void A_drag_writes_the_display_position_divided_by_the_scale()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(TestTasks.Design, "Design", 100, 90),
            TaskAt(TestTasks.Build, "Build", 460, 90)));
        var canvas = shell.Window.ViewModel.Canvas!;
        var node = shell.NodeModel("Design");

        node.DisplayLocation = new Point(40, 60);
        canvas.CommitMovesCommand.Execute(null);

        var saved = canvas.Workflow.Positions[TestTasks.Design];
        Assert.Equal(40 / canvas.Space.Kx, saved.X, 6);
        Assert.Equal(60 / canvas.Space.Ky, saved.Y, 6);
        Assert.Equal(new CanvasPoint(460, 90), canvas.Workflow.Positions[TestTasks.Build]);
    }

    [AvaloniaFact]
    public void A_selected_cards_wires_carry_a_label_at_their_midpoint_that_names_the_target()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(TestTasks.Design, "Design", 100, 90),
            TaskAt(TestTasks.Build, "Build", 460, 90),
            new WorkflowEdit.Connect(new ConnectionKey(TestTasks.Design, TestTasks.Build), ConnectionKind.Dependency)));
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.DoesNotContain(canvas.Decorations, decoration => decoration is WirePillViewModel);

        canvas.Select(shell.NodeModel("Design"));
        shell.Render();

        var pill = Assert.Single(canvas.Decorations.OfType<WirePillViewModel>());
        Assert.Equal(("Result", "Result for Build"), (pill.Label, pill.Tip));
        Assert.True(shell.Has<Border>("WirePill"), "The label draws on the Map.");
    }

    [AvaloniaFact]
    public void A_workflow_file_stays_byte_identical_when_no_card_moved()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(TestTasks.Design, "Design", 105, 90.25),
            TaskAt(TestTasks.Build, "Build", 460.5, 90.25)));
        var canvas = shell.Window.ViewModel.Canvas!;
        var before = File.ReadAllBytes(canvas.Document.FilePath);

        // A drag that ends where it started writes nothing, so saving keeps every byte.
        canvas.CommitMovesCommand.Execute(null);
        shell.Render();
        Assert.False(canvas.HasUnsavedChanges);
        canvas.Document.Save();

        Assert.Equal(before, File.ReadAllBytes(canvas.Document.FilePath));
    }
}
