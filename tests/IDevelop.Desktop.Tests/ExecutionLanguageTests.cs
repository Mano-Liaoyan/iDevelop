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
    [InlineData(ConnectionKind.Context, NodeState.Completed, NodeState.Running, WireFlow.Feeding)]
    [InlineData(ConnectionKind.Context, NodeState.Idle, NodeState.Idle, WireFlow.Context)]
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
    public void A_wire_is_quiet_until_it_is_highlighted_or_feeds_a_running_agent()
    {
        Assert.Equal(2, WireFlows.Stroke(WireFlow.Feeding, highlighted: false));
        Assert.All(new[] { WireFlow.Delivered, WireFlow.Outdated, WireFlow.Ready, WireFlow.Context, WireFlow.Stopped, WireFlow.Idle }, flow =>
        {
            Assert.Equal(1.5, WireFlows.Stroke(flow, highlighted: false));
            Assert.Equal(2, WireFlows.Stroke(flow, highlighted: true));
        });
    }

    [Fact]
    public void A_wire_keeps_its_weight_on_screen_under_100_percent()
    {
        Assert.Equal(1, ZoomTiers.WireScale(1));
        Assert.Equal(1, ZoomTiers.WireScale(1.6));
        Assert.Equal(2, ZoomTiers.WireScale(0.5));
        Assert.Equal(4, ZoomTiers.WireScale(0.25));
    }

    [Theory]
    [InlineData(1.0, ZoomTier.Full)]
    [InlineData(0.75, ZoomTier.Full)]
    [InlineData(0.74, ZoomTier.Plate)]
    [InlineData(0.3, ZoomTier.Plate)]
    [InlineData(0.29, ZoomTier.Block)]
    public void A_falling_zoom_leaves_a_tier_at_its_threshold(double zoom, ZoomTier tier) =>
        Assert.Equal(tier, ZoomTiers.Next(ZoomTier.Full, zoom));

    [Theory]
    [InlineData(ZoomTier.Plate, 0.76, ZoomTier.Plate)]
    [InlineData(ZoomTier.Plate, 0.77, ZoomTier.Full)]
    [InlineData(ZoomTier.Block, 0.305, ZoomTier.Block)]
    [InlineData(ZoomTier.Block, 0.32, ZoomTier.Plate)]
    public void A_rising_zoom_enters_the_larger_tier_past_a_gap_so_a_zoom_near_a_threshold_does_not_flicker(ZoomTier current, double zoom, ZoomTier tier) =>
        Assert.Equal(tier, ZoomTiers.Next(current, zoom));

    [Theory]
    [InlineData(0.75)]
    [InlineData(0.8)]
    public void The_full_tier_keeps_its_caption_at_the_type_ramp_size_on_screen(double zoom) =>
        Assert.Equal(11.5, ZoomTiers.Metrics(zoom).CaptionSize * zoom, 3);

    [Fact]
    public void The_plate_tier_keeps_a_12_px_title_and_6_px_sockets_on_screen()
    {
        var metrics = ZoomTiers.Metrics(0.5);

        Assert.Equal(12, metrics.PlateTitleSize * 0.5, 3);
        Assert.Equal(6, metrics.SocketSize * 0.5, 3);
        Assert.Equal(2, metrics.PlateLines);
    }

    [Fact]
    public void The_plate_tier_takes_one_title_line_where_two_lines_would_leave_the_card()
    {
        Assert.Equal(1, ZoomTiers.Metrics(0.3).PlateLines);
        Assert.Equal(2, ZoomTiers.Metrics(0.4).PlateLines);
    }

    private static WireLanes.Wire Wire(double order, double targetOrder, params (double X, double Y)[] points) =>
        new([.. points.Select(point => new Point(point.X, point.Y))], order, targetOrder);

    [Fact]
    public void Wires_on_one_run_take_their_own_lanes_and_the_lower_wire_takes_the_left_lane_so_they_do_not_cross()
    {
        var routes = WireLanes.Spread(
        [
            Wire(1, 0, (100, 0), (200, 0), (200, 100), (300, 100)),
            Wire(2, 0, (100, 50), (200, 50), (200, 150), (300, 150)),
        ], []).Routes;

        Assert.Equal(208, routes[0][1].X);
        Assert.Equal(192, routes[1][1].X);
        Assert.Equal(0, routes[0][1].Y);
    }

    [Fact]
    public void A_full_channel_reports_the_longest_wire_that_may_leave_it()
    {
        // Three lanes need 8 of room at the least spacing, and this channel of 16 between margins of 6 has 4.
        Rect[] cards = [new Rect(0, 0, 100, 400), new Rect(116, 0, 100, 400)];
        var spread = WireLanes.Spread(
        [
            Wire(1, 0, (105, 10), (108, 10), (108, 300), (112, 300)),
            Wire(2, 0, (105, 20), (108, 20), (108, 100), (112, 100)),
            Wire(3, 0, (105, 30), (108, 30), (108, 200), (112, 200)),
        ], cards);

        var overflow = Assert.Single(spread.Overflows);
        Assert.Equal(0, overflow.Wire);
        Assert.Equal(new Rect(100, 10, 16, 290), overflow.Channel);
    }

    [Fact]
    public void A_wire_that_feeds_a_running_node_stays_in_a_full_channel()
    {
        Rect[] cards = [new Rect(0, 0, 100, 400), new Rect(116, 0, 100, 400)];
        WireLanes.Wire Active(double order, params (double X, double Y)[] points) => Wire(order, 0, points) with { Stays = true };
        var spread = WireLanes.Spread(
        [
            Active(1, (105, 10), (108, 10), (108, 300), (112, 300)),
            Wire(2, 0, (105, 20), (108, 20), (108, 100), (112, 100)),
            Wire(3, 0, (105, 30), (108, 30), (108, 200), (112, 200)),
        ], cards);

        Assert.Equal(2, Assert.Single(spread.Overflows).Wire);
    }

    [Fact]
    public void A_lane_stays_in_the_middle_of_its_channel()
    {
        var routes = WireLanes.Spread(
        [
            Wire(1, 0, (0, 0), (50, 0), (50, 50), (400, 50), (400, 0), (450, 0)),
        ], [new Rect(150, -100, 100, 64), new Rect(150, 120, 100, 64)]).Routes;

        Assert.Equal(42, routes[0][2].Y);
    }

    [Fact]
    public void The_wires_of_one_output_share_one_trunk_and_a_wire_of_another_output_keeps_its_own_lane()
    {
        WireLanes.Wire From(int output, double order, double y) => new([new Point(100, 0), new Point(200, 0), new Point(200, y), new Point(300, y)], order, 0, Trunk: output);

        var routes = WireLanes.Spread([From(1, 1, 100), From(1, 2, 200), From(2, 3, 150)], []).Routes;

        Assert.Equal(routes[0][1].X, routes[1][1].X);
        Assert.NotEqual(routes[0][1].X, routes[2][1].X);
        Assert.Equal(routes[0][2].X, routes[1][2].X);
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
