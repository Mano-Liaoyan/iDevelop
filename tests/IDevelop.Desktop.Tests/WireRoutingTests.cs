using Avalonia;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Where a wire runs between its cards. Each card below is a container at a canvas location: 260 by 64, its output port
/// centered at (250, 32) and its input port at (10, 32). A wire's route starts 26 px right of the output and ends 26 px
/// left of the input.
/// </summary>
public sealed class WireRoutingTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_wire_with_nothing_in_its_way_keeps_the_step_across_the_middle()
    {
        var route = WireRouting.Route(new Point(370, 122), new Point(610, 282), [Card(120, 90), Card(600, 250)]);

        Assert.Equal<Point>([new Point(396, 122), new Point(490, 122), new Point(490, 282), new Point(584, 282)], route);
    }

    [Fact]
    public void A_wire_whose_step_would_pass_behind_a_card_turns_at_its_own_stub_instead()
    {
        // The sample project: Design to Review, with Implement between them in Design's row.
        var route = WireRouting.Route(new Point(370, 122), new Point(730, 279.5), [Card(120, 90), Card(420, 90), Card(720, 247.5)]);

        Assert.Equal<Point>([new Point(396, 122), new Point(396, 279.5), new Point(704, 279.5)], route);
    }

    [Fact]
    public void A_wire_past_a_card_in_its_row_goes_below_it_and_keeps_its_clearance()
    {
        var route = WireRouting.Route(new Point(370, 122), new Point(730, 122), [Card(120, 90), Card(420, 90), Card(720, 90)]);

        Assert.Equal<Point>([new Point(396, 122), new Point(396, 170), new Point(704, 170), new Point(704, 122)], route);
    }

    [Fact]
    public void A_wire_back_to_a_card_on_the_left_goes_around_both_cards_rather_than_behind_them()
    {
        var route = WireRouting.Route(new Point(670, 122), new Point(70, 152), [Card(420, 90), Card(60, 120)]);

        Assert.Equal<Point>([new Point(696, 122), new Point(696, 200), new Point(44, 200), new Point(44, 152)], route);
    }

    [Fact]
    public void A_wire_back_to_a_card_in_another_row_keeps_the_step_when_the_middle_row_is_clear()
    {
        var route = WireRouting.Route(new Point(670, 122), new Point(70, 352), [Card(420, 90), Card(60, 320)]);

        Assert.Equal<Point>([new Point(696, 122), new Point(696, 237), new Point(44, 237), new Point(44, 352)], route);
    }

    [Fact]
    public void A_card_over_the_end_of_a_stub_leaves_the_wire_on_its_step()
    {
        var route = WireRouting.Route(new Point(370, 122), new Point(610, 282), [Card(120, 90), Card(600, 250), Card(560, 260)]);

        Assert.Equal<Point>([new Point(396, 122), new Point(490, 122), new Point(490, 282), new Point(584, 282)], route);
    }

    [AvaloniaFact]
    public void The_wire_goes_around_a_card_in_its_way_and_takes_the_step_again_once_that_card_moves()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 120, 90),
            TaskAt(Build, "Build", 420, 90),
            TaskAt(Review, "Review", 720, 247.5),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency)));
        var canvas = shell.Window.ViewModel.Canvas!;
        ConnectionViewModel? Clicked(Point point)
        {
            shell.Click(OnScreen(shell, point));
            return canvas.SelectedConnection;
        }

        // Below Build, on the step the wire would take through it, and on the wire's own stub column.
        Assert.Null(Clicked(new Point(550, 215)));
        Assert.Equal((Design, Review), Clicked(new Point(396, 215)) is { } around ? (around.Key.From, around.Key.To) : default);

        shell.Drag(shell.Header(shell.Node("Build")), shell.Header(shell.Node("Build")) + new Vector(0, 330));
        Assert.True(canvas.Nodes.Single(node => node.Title == "Build").Location.Y > 330, "Build moved below the wire");

        Assert.Equal((Design, Review), Clicked(new Point(550, 215)) is { } step ? (step.Key.From, step.Key.To) : default);
    }

    private static Rect Card(double x, double y) => new(x, y, 260, 64);

    private static Point OnScreen(Shell shell, Point canvas)
    {
        var editor = shell.Editor;
        var local = new Point(
            (canvas.X - editor.ViewportLocation.X) * editor.ViewportZoom,
            (canvas.Y - editor.ViewportLocation.Y) * editor.ViewportZoom);
        return editor.TranslatePoint(local, shell.Window) ?? throw new InvalidOperationException("The editor is not in the window.");
    }
}
