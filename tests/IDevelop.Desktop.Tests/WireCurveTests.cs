using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

/// <summary>The curve the canvas draws along a wire's route (ADR 0045).</summary>
public sealed class WireCurveTests
{
    private static readonly Point Source = new(100, 100);

    [Fact]
    public void A_wire_with_one_turn_is_one_curve_from_port_to_port_that_leaves_and_arrives_across()
    {
        var curve = WireCurve.Of(Source, [new Point(126, 100), new Point(200, 100), new Point(200, 300), new Point(274, 300)], new Point(300, 300), []);

        var piece = Assert.Single(curve);
        Assert.Equal(new WirePiece(Source, new Point(200, 100), new Point(200, 300), new Point(300, 300)), piece);
    }

    [Fact]
    public void A_wire_on_one_row_is_straight()
    {
        var piece = Assert.Single(WireCurve.Of(Source, [new Point(126, 100), new Point(274, 100)], new Point(300, 100), []));

        Assert.All(new[] { 0.25, 0.5, 0.75 }, at => Assert.Equal(100, piece.At(at).Y));
    }

    [Fact]
    public void A_detour_curves_at_each_turn_and_runs_straight_along_its_lane()
    {
        // Out of a card's row, along the gap below the next card, and back up to the target's row.
        var curve = WireCurve.Of(
            Source,
            [new Point(126, 100), new Point(126, 188), new Point(574, 188), new Point(574, 100)],
            new Point(600, 100),
            []);

        Assert.Equal(2, curve.Length);
        Assert.Equal(Source, curve[0].Start);
        Assert.Equal(new Point(350, 188), curve[0].End);
        Assert.Equal(curve[0].End, curve[1].Start);
        Assert.Equal(new Point(600, 100), curve[1].End);
    }

    [Fact]
    public void A_curve_that_would_touch_a_card_keeps_closer_to_its_route()
    {
        // The wide curve from (100, 100) to (700, 300) crosses this card, and the route passes clear of it.
        var card = new Rect(250, 160, 260, 64);
        Point[] route = [new Point(126, 100), new Point(600, 100), new Point(600, 300), new Point(674, 300)];

        var wide = WireCurve.Of(Source, route, new Point(700, 300), []);
        var fitted = WireCurve.Of(Source, route, new Point(700, 300), [card]);

        Assert.Contains(wide.SelectMany(Samples), new Rect(250, 100, 260, 200).Contains);
        Assert.DoesNotContain(fitted.SelectMany(Samples), card.Contains);
    }

    [Fact]
    public void A_wire_without_a_route_is_the_plain_curve()
    {
        var piece = Assert.Single(WireCurve.Plain(Source, new Point(300, 200)));

        Assert.Equal(new Point(200, 100), piece.First);
        Assert.Equal(new Point(200, 200), piece.Second);
    }

    private static IEnumerable<Point> Samples(WirePiece piece) => Enumerable.Range(0, 33).Select(step => piece.At(step / 32.0));
}
