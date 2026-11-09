using System.Collections.Immutable;
using Avalonia;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>Flow's pure layout: depth, order, widths, wrapping, and the no-overlap guarantee.</summary>
public sealed class FlowLayoutTests
{
    private static readonly int[] Fixture50 = [4, 6, 8, 6, 8, 8, 6, 4];

    private static TaskId Id(int value) => new(new Guid(value, 0, 0, new byte[8]));

    private static Workflow Must(Workflow workflow, WorkflowEdit edit) => ((EditResult.Applied)workflow.Apply(edit)).Workflow;

    private static Workflow Build((int Id, double X, double Y)[] nodes, params (int From, int To)[] edges)
    {
        var workflow = IDevelop.Workflows.Workflow.Empty(new WorkflowId(Guid.NewGuid()));
        foreach (var node in nodes)
        {
            workflow = Must(workflow, TestNodes.Place(TestNodes.Implement(Id(node.Id), $"T{node.Id}"), new CanvasPoint(node.X, node.Y)));
        }

        foreach (var edge in edges)
        {
            workflow = Must(workflow, new WorkflowEdit.Connect(new ConnectionKey(Id(edge.From), Id(edge.To)), ConnectionKind.Dependency));
        }

        return workflow;
    }

    [Fact]
    public void A_node_stands_at_its_longest_path_from_a_node_with_no_predecessor()
    {
        var workflow = Build([(1, 0, 0), (2, 0, 100), (3, 0, 200), (4, 300, 0)], (1, 3), (2, 3), (1, 2), (3, 4));

        Assert.Equal([[Id(1)], [Id(2)], [Id(3)], [Id(4)]], FlowLayout.Stages(workflow));
    }

    [Fact]
    public void A_stage_orders_by_saved_Y_then_saved_X()
    {
        var workflow = Build([(1, 300, 200), (2, 0, 200), (3, 500, 50)]);

        Assert.Equal([[Id(3), Id(2), Id(1)]], FlowLayout.Stages(workflow));
    }

    [Fact]
    public void The_fixture_s_large_workflow_wraps_at_139_px_in_a_612_px_Map()
    {
        var grid = FlowLayout.Measure(612, Fixture50);

        Assert.Equal(new FlowGrid(139, 4, true), grid);
    }

    [Fact]
    public void Stages_that_fit_at_132_px_do_not_wrap()
    {
        var map = 24 + 8 * 132 + 7 * 10;
        Assert.Equal(new FlowGrid(132, 8, false), FlowLayout.Measure(map, Fixture50));
        Assert.Equal(new FlowGrid(131, 7, true), FlowLayout.Measure(map - 1, Fixture50) with { CardWidth = 131 });
    }

    [Fact]
    public void A_card_stops_at_220_px_wide()
    {
        Assert.Equal(220, FlowLayout.Measure(2000, [3]).CardWidth);
    }

    [Fact]
    public void An_unwrapped_row_centers_and_a_wrapped_row_starts_at_the_left_edge()
    {
        var centered = FlowLayout.Place(1000, FlowDensity.Card, [new(2, false)]);
        Assert.True(centered.Bands[0].Cards[0].Left > FlowLayout.SideMargin + 100);
        Assert.Equal(centered.Bands[0].Cards[0].Left, 1000 - centered.Bands[0].Cards[1].Right, 1);

        var wrapped = FlowLayout.Place(612, FlowDensity.Chip, [new(8, false), new(1, false)]);
        Assert.Equal(FlowLayout.SideMargin, wrapped.Bands[1].Cards[0].Left);
    }

    [Theory]
    [InlineData(336)]
    [InlineData(472)]
    [InlineData(612)]
    [InlineData(1236)]
    [InlineData(2000)]
    public void No_card_overlaps_a_card_or_a_header_at_any_width_or_density(double map)
    {
        foreach (var density in Enum.GetValues<FlowDensity>())
        {
            var plan = FlowLayout.Place(map, density, Fixture50.Select((count, index) => new FlowStage(count, index % 3 == 0)).ToArray());
            var rects = plan.Bands.SelectMany(band => band.Cards.Append(band.Header)).ToArray();
            for (var a = 0; a < rects.Length; a++)
            {
                Assert.True(rects[a].Right <= map - FlowLayout.SideMargin + 0.5 && rects[a].Left >= FlowLayout.SideMargin - 0.5, $"{rects[a]} leaves the map");
                for (var b = a + 1; b < rects.Length; b++)
                {
                    Assert.False(rects[a].Intersects(rects[b]), $"{rects[a]} and {rects[b]} overlap at {map} {density}");
                }
            }

            Assert.True(plan.Height >= plan.Bands[^1].Header.Bottom);
        }
    }

    [Fact]
    public void A_folded_band_has_only_its_header()
    {
        var plan = FlowLayout.Place(612, FlowDensity.Card, [new(6, true), new(2, false)]);

        Assert.Empty(plan.Bands[0].Cards);
        Assert.Equal(2, plan.Bands[1].Cards.Length);
        Assert.Equal(plan.Bands[0].Header.Bottom + FlowLayout.BandGap, plan.Bands[1].Header.Top);
    }

    [Fact]
    public void An_output_above_its_input_is_one_straight_line_and_wires_of_one_output_share_a_trunk()
    {
        var (source, left, right) = (new Point(100, 80), new Point(50, 130), new Point(150, 130));

        Assert.Equal([source, new Point(100, 130)], FlowLayout.WireRoute(source, new Point(100, 130), []).ToArray());

        var a = FlowLayout.WireRoute(source, left, []);
        var b = FlowLayout.WireRoute(source, right, []);
        Assert.Equal(source, a[0]);
        Assert.Equal(a[1], b[1]);
        Assert.Equal(left, a[^1]);
    }

    [Fact]
    public void Arrows_go_along_the_row_and_to_the_nearest_card_above_or_below_past_a_folded_band()
    {
        var plan = FlowLayout.Place(1000, FlowDensity.Card, [new(3, false), new(1, true), new(2, false)]);

        Assert.Equal((0, 1), FlowLayout.Neighbor(plan, 0, 0, FlowMove.Right));
        Assert.Null(FlowLayout.Neighbor(plan, 0, 0, FlowMove.Left));
        Assert.Equal(2, FlowLayout.Neighbor(plan, 0, 1, FlowMove.Down)!.Value.Stage);
        Assert.Equal((0, 1), FlowLayout.Neighbor(plan, 2, 1, FlowMove.Up));
    }

    [Fact]
    public void Fit_takes_Chip_when_it_shows_more_stages_from_the_run_front()
    {
        var stages = Fixture50.Select(count => new FlowStage(count, false)).ToArray();

        Assert.Equal(FlowDensity.Chip, FlowLayout.DensityToFit(1236, 500, stages, 0));
        Assert.Equal(FlowDensity.Card, FlowLayout.DensityToFit(1236, 5000, stages, 0));
    }

    [Fact]
    public void A_small_workflow_opens_in_Card_density_and_a_large_one_in_Chip()
    {
        Assert.Equal(FlowDensity.Card, FlowLayout.DefaultDensity(24));
        Assert.Equal(FlowDensity.Chip, FlowLayout.DefaultDensity(25));
    }
}
