using System.Collections.Immutable;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;
using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using EngineState = IDevelop.Engine.NodeState;
using NodeState = IDevelop.Desktop.Canvas.NodeState;

namespace IDevelop.Desktop.Tests;

/// <summary>The rules that turn a run into the canvas's language: what a wire carries, how much a card shows, and which lane a wire takes.</summary>
public sealed class ExecutionLanguageTests
{
    private static readonly TaskId Source = TestTasks.Design;

    private static Basis On(string? result) => new([new BasisInput(Source, result)], "instruction", null);

    [Theory]
    [InlineData(ConnectionKind.Context, NodeState.Completed, NodeState.Running, WireFlow.Context)]
    [InlineData(ConnectionKind.Dependency, NodeState.Completed, NodeState.Running, WireFlow.Feeding)]
    [InlineData(ConnectionKind.Dependency, NodeState.Completed, NodeState.InputRequired, WireFlow.Delivered)]
    [InlineData(ConnectionKind.Dependency, NodeState.Completed, NodeState.Idle, WireFlow.Ready)]
    [InlineData(ConnectionKind.Dependency, NodeState.Failed, NodeState.Idle, WireFlow.Stopped)]
    [InlineData(ConnectionKind.Dependency, NodeState.Idle, NodeState.Idle, WireFlow.Idle)]
    [InlineData(ConnectionKind.Dependency, NodeState.Running, NodeState.Idle, WireFlow.Idle)]
    [InlineData(ConnectionKind.Dependency, NodeState.Stale, NodeState.Stale, WireFlow.Idle)]
    public void A_wire_takes_the_first_flow_whose_rule_matches_before_any_target_result(ConnectionKind kind, NodeState from, NodeState to, WireFlow flow) =>
        Assert.Equal(flow, WireFlows.Of(kind, from, to, Source, "r1", null));

    [Fact]
    public void A_target_result_says_whether_it_holds_the_latest_result_of_the_source()
    {
        Assert.Equal(WireFlow.Delivered, WireFlows.Of(ConnectionKind.Dependency, NodeState.Completed, NodeState.Completed, Source, "r2", On("r2")));
        Assert.Equal(WireFlow.Outdated, WireFlows.Of(ConnectionKind.Dependency, NodeState.Completed, NodeState.Stale, Source, "r2", On("r1")));
        // A connection added after the target ran is not in the target's basis.
        var other = new Basis([new BasisInput(TestTasks.Build, "r9")], "instruction", null);
        Assert.Equal(WireFlow.Outdated, WireFlows.Of(ConnectionKind.Dependency, NodeState.Completed, NodeState.Stale, Source, "r2", other));
    }

    [Fact]
    public void Feeding_is_the_heaviest_wire_and_a_wire_that_carries_nothing_is_the_lightest()
    {
        Assert.Equal(2.5, WireFlows.Stroke(WireFlow.Feeding));
        Assert.All(new[] { WireFlow.Delivered, WireFlow.Outdated, WireFlow.Ready, WireFlow.Context, WireFlow.Stopped, WireFlow.Idle }, flow => Assert.Equal(2, WireFlows.Stroke(flow)));
    }

    [Theory]
    [InlineData(1.0, ZoomTier.Full)]
    [InlineData(0.84, ZoomTier.Full)]
    [InlineData(0.83, ZoomTier.Detail)]
    [InlineData(0.6, ZoomTier.Detail)]
    [InlineData(0.59, ZoomTier.Compact)]
    [InlineData(0.5, ZoomTier.Compact)]
    [InlineData(0.49, ZoomTier.Label)]
    [InlineData(0.36, ZoomTier.Label)]
    [InlineData(0.35, ZoomTier.Title)]
    [InlineData(0.23, ZoomTier.Title)]
    [InlineData(0.22, ZoomTier.Block)]
    public void A_falling_zoom_leaves_a_tier_at_its_threshold_so_a_title_never_draws_under_10_px(double zoom, ZoomTier tier) =>
        Assert.Equal(tier, ZoomTiers.Next(ZoomTier.Full, zoom));

    [Theory]
    [InlineData(ZoomTier.Detail, 0.85, ZoomTier.Detail)]
    [InlineData(ZoomTier.Detail, 0.86, ZoomTier.Full)]
    [InlineData(ZoomTier.Compact, 0.61, ZoomTier.Compact)]
    [InlineData(ZoomTier.Compact, 0.62, ZoomTier.Detail)]
    [InlineData(ZoomTier.Label, 0.51, ZoomTier.Label)]
    [InlineData(ZoomTier.Label, 0.52, ZoomTier.Compact)]
    [InlineData(ZoomTier.Title, 0.37, ZoomTier.Title)]
    [InlineData(ZoomTier.Title, 0.39, ZoomTier.Label)]
    [InlineData(ZoomTier.Block, 0.24, ZoomTier.Block)]
    [InlineData(ZoomTier.Block, 0.26, ZoomTier.Title)]
    public void A_rising_zoom_enters_the_larger_tier_past_a_gap_so_a_zoom_near_a_threshold_does_not_flicker(ZoomTier current, double zoom, ZoomTier tier) =>
        Assert.Equal(tier, ZoomTiers.Next(current, zoom));

    private static WireLanes.Wire Wire(double order, double targetOrder, params (double X, double Y)[] points) =>
        new([.. points.Select(point => new Point(point.X, point.Y))], order, targetOrder);

    [Fact]
    public void Wires_on_one_run_take_lanes_22_px_apart_in_source_order()
    {
        var routes = WireLanes.Spread(
        [
            Wire(2, 0, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire(1, 0, (100, 50), (200, 50), (200, 150), (300, 150)),
        ], []);

        Assert.Equal(211, routes[0][1].X);
        Assert.Equal(189, routes[1][1].X);
        Assert.Equal(0, routes[0][1].Y);
    }

    [Fact]
    public void A_lane_that_would_run_along_a_card_s_top_edge_keeps_the_wire_where_the_router_put_it()
    {
        // Two wires share the run at row 100. The lower lane, 11 under it, would pass 9 above a card whose top is at 120.
        var routes = WireLanes.Spread(
        [
            Wire(1, 0, (0, 0), (50, 0), (50, 100), (400, 100), (400, 300), (450, 300)),
            Wire(2, 1, (0, 20), (60, 20), (60, 100), (390, 100), (390, 320), (450, 320)),
        ], [new Rect(150, 120, 100, 64)]);

        Assert.Equal(100, routes[1][2].Y);
    }

    [Fact]
    public void Lanes_and_port_points_widen_as_much_as_the_tier_thickens_the_wires_so_they_stay_apart_on_screen()
    {
        var routes = WireLanes.Spread(
        [
            Wire(2, 0, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire(1, 0, (100, 50), (200, 50), (200, 150), (300, 150)),
        ], [], scale: 2);

        Assert.Equal([222.0, 178.0], routes.Select(route => route[1].X));
        Assert.Equal(PortSlots.Of([0, 50], 80, 0, 154).Select(slot => slot * 2), PortSlots.Of([0, 50], 80, 0, 154, 2));
    }

    [Fact]
    public void Wires_that_share_a_port_meet_the_card_at_their_own_points_in_the_order_of_their_far_rows()
    {
        // Three wires out of a port at row 32 of a card from 0 to 64: one point each, the highest far row on top, 12 apart.
        Assert.Equal([12.0, -12.0, 0.0], PortSlots.Of([300, -100, 100], 32, 0, 64));
        // One wire keeps the port's own point, and so does the wire whose other end lies on the port's row.
        Assert.Equal([0.0], PortSlots.Of([300], 32, 0, 64));
        Assert.Equal([0.0, 12.0], PortSlots.Of([32, 300], 32, 0, 64));
        // Many wires squeeze into the card's edge, 10 from its top and its bottom.
        var squeezed = PortSlots.Of([.. Enumerable.Range(0, 9).Select(row => (double)row)], 32, 0, 64);
        Assert.Equal(-22, squeezed[0], 3);
        Assert.Equal(22, squeezed[^1], 3);
    }

    [Fact]
    public void Wires_from_one_output_or_into_one_input_keep_their_own_lanes_so_none_shares_a_trunk()
    {
        var (fanOut, fanIn) = (WireLanes.Spread(
        [
            Wire(1, 1, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire(1, 2, (100, 0), (200, 0), (200, 150), (300, 150)),
        ], []), WireLanes.Spread(
        [
            Wire(1, 1, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire(2, 1, (100, 50), (200, 50), (200, 100), (300, 100)),
        ], []));

        Assert.Equal([189.0, 211.0], fanOut.Select(route => route[1].X));
        Assert.Equal([189.0, 211.0], fanIn.Select(route => route[1].X));
    }

    [Fact]
    public void A_wire_stays_where_it_was_when_its_lane_would_cross_a_card()
    {
        var routes = WireLanes.Spread(
        [
            Wire(2, 0, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire(1, 0, (100, 50), (200, 50), (200, 150), (300, 150)),
        ], [new Rect(201, 20, 40, 40)]);

        Assert.Equal(200, routes[0][1].X);
        Assert.Equal(189, routes[1][1].X);
    }

    [AvaloniaTheory]
    [InlineData("A long title that a line limit cuts", 2)]
    [InlineData("Averyveryveryverylongwordwithoutanybreakopportunity at all", 1)]
    public void A_title_that_loses_text_ends_in_an_ellipsis_and_keeps_to_its_lines(string title, int lines)
    {
        var text = new EllipsisText { Whole = title, MaxLines = lines, TextWrapping = TextWrapping.Wrap, FontSize = 14 };

        text.Measure(new Size(90, double.PositiveInfinity));

        Assert.EndsWith("…", text.Text);
        Assert.StartsWith(text.Text![..^1], title);
        using var layout = new TextLayout(text.Text, Typeface.Default, 14, null, textWrapping: TextWrapping.Wrap, maxWidth: 90);
        Assert.True(layout.TextLines.Count <= lines);
    }

    [AvaloniaTheory]
    [InlineData("Aggregate hourly usage")]
    [InlineData("Add the usage_events schema for every tenant")]
    public void A_title_never_breaks_inside_a_word(string title)
    {
        var text = new EllipsisText { Whole = title, MaxLines = 3, TextWrapping = TextWrapping.WrapWithOverflow, FontSize = 44 };

        text.Measure(new Size(180, double.PositiveInfinity));

        using var layout = new TextLayout(text.Text, Typeface.Default, 44, null, textWrapping: TextWrapping.WrapWithOverflow, maxWidth: 180);
        Assert.All(layout.TextLines, line => Assert.True(line.Width <= 180.5, $"A line takes {line.Width} px."));
        Assert.Equal(text.Text, string.Concat(layout.TextLines.Select(line => text.Text!.Substring(line.FirstTextSourceIndex, line.Length))));
        Assert.True(layout.TextLines.Count <= 3);
    }

    [AvaloniaFact]
    public void A_title_that_fits_shows_whole()
    {
        var text = new EllipsisText { Whole = "Short", MaxLines = 2, TextWrapping = TextWrapping.Wrap, FontSize = 14 };

        text.Measure(new Size(200, double.PositiveInfinity));

        Assert.Equal("Short", text.Text);
    }

    [Theory]
    [InlineData(EngineState.Approval, WaitReason.Approval)]
    [InlineData(EngineState.Reply, WaitReason.Reply)]
    [InlineData(EngineState.Conflict, WaitReason.Conflict)]
    [InlineData(EngineState.Interrupted, WaitReason.Interrupted)]
    [InlineData(EngineState.RoundLimit, WaitReason.RoundLimit)]
    [InlineData("Which window?", WaitReason.Question)]
    public void A_waiting_node_names_what_it_needs(string why, WaitReason reason) =>
        Assert.Equal(reason, NodeStates.Why(new EngineState.InputRequired(why)));

    [AvaloniaFact]
    public void A_title_too_narrow_for_a_start_of_it_still_keeps_its_place()
    {
        var text = new EllipsisText { Whole = "Backfill ledger partition 2024-Q3", Key = new TitleKey(4, 1, 0, 0), MaxLines = 2, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        text.Measure(new Avalonia.Size(4, 100));
        Assert.True(text.IsCut);
    }
}
