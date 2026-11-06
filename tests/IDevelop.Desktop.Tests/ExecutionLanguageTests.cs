using System.Collections.Immutable;
using Avalonia;
using IDevelop.Desktop.Canvas;
using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
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
        Assert.Equal(3, WireFlows.Stroke(WireFlow.Feeding));
        Assert.All(new[] { WireFlow.Delivered, WireFlow.Outdated, WireFlow.Ready }, flow => Assert.Equal(2, WireFlows.Stroke(flow)));
        Assert.All(new[] { WireFlow.Context, WireFlow.Stopped, WireFlow.Idle }, flow => Assert.Equal(1.5, WireFlows.Stroke(flow)));
    }

    [Theory]
    [InlineData(1.0, ZoomTier.Full)]
    [InlineData(0.84, ZoomTier.Full)]
    [InlineData(0.83, ZoomTier.Compact)]
    [InlineData(0.5, ZoomTier.Compact)]
    [InlineData(0.49, ZoomTier.Label)]
    [InlineData(0.385, ZoomTier.Label)]
    [InlineData(0.38, ZoomTier.Block)]
    public void A_falling_zoom_leaves_a_tier_at_its_threshold_so_a_title_never_draws_under_10_px(double zoom, ZoomTier tier) =>
        Assert.Equal(tier, ZoomTiers.Next(ZoomTier.Full, zoom));

    [Theory]
    [InlineData(ZoomTier.Compact, 0.85, ZoomTier.Compact)]
    [InlineData(ZoomTier.Compact, 0.86, ZoomTier.Full)]
    [InlineData(ZoomTier.Label, 0.51, ZoomTier.Label)]
    [InlineData(ZoomTier.Label, 0.52, ZoomTier.Compact)]
    [InlineData(ZoomTier.Block, 0.4, ZoomTier.Block)]
    [InlineData(ZoomTier.Block, 0.41, ZoomTier.Label)]
    public void A_rising_zoom_enters_the_larger_tier_past_a_gap_so_a_zoom_near_a_threshold_does_not_flicker(ZoomTier current, double zoom, ZoomTier tier) =>
        Assert.Equal(tier, ZoomTiers.Next(current, zoom));

    private static WireLanes.Wire Wire(object source, object target, double order, params (double X, double Y)[] points) =>
        new([.. points.Select(point => new Point(point.X, point.Y))], source, target, order);

    [Fact]
    public void Wires_from_different_sources_to_different_targets_take_lanes_6_px_apart_in_source_order()
    {
        var routes = WireLanes.Spread(
        [
            Wire("b", "x", 2, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire("a", "y", 1, (100, 50), (200, 50), (200, 150), (300, 150)),
        ], []);

        Assert.Equal(203, routes[0][1].X);
        Assert.Equal(197, routes[1][1].X);
        Assert.Equal(0, routes[0][1].Y);
    }

    [Fact]
    public void Wires_from_one_output_or_into_one_input_stay_on_one_line()
    {
        var (fanOut, fanIn) = (WireLanes.Spread(
        [
            Wire("a", "x", 1, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire("a", "y", 1, (100, 0), (200, 0), (200, 150), (300, 150)),
        ], []), WireLanes.Spread(
        [
            Wire("a", "x", 1, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire("b", "x", 2, (100, 50), (200, 50), (200, 100), (300, 100)),
        ], []));

        Assert.All(fanOut, route => Assert.Equal(200, route[1].X));
        Assert.All(fanIn, route => Assert.Equal(200, route[1].X));
    }

    [Fact]
    public void A_wire_stays_where_it_was_when_its_lane_would_cross_a_card()
    {
        var routes = WireLanes.Spread(
        [
            Wire("b", "x", 2, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire("a", "y", 1, (100, 50), (200, 50), (200, 150), (300, 150)),
        ], [new Rect(201, 20, 40, 40)]);

        Assert.Equal(200, routes[0][1].X);
        Assert.Equal(197, routes[1][1].X);
    }
}
