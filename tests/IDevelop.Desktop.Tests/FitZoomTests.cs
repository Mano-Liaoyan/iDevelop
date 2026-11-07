using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

public sealed class ZoomTitleTextTests
{
    [Theory]
    [InlineData(0.23, 44, 2)]
    [InlineData(0.29, 36, 2)]
    [InlineData(0.32, 32, 3)]
    [InlineData(0.34, 30, 3)]
    public void The_title_tier_keeps_its_text_readable_and_a_closer_zoom_keeps_more_lines(double zoom, double size, int lines)
    {
        var text = ZoomTiers.TitleText(zoom);

        Assert.Equal((size, lines), (text.Size, text.Lines));
        Assert.True(text.Size * zoom >= 10, $"The text draws at {text.Size * zoom} px.");
    }
}

/// <summary>Fit keeps titles readable (ADR 0045).</summary>
public sealed class FitZoomTests
{
    private static readonly Rect Room = new(0, 0, 680, 500);

    [Fact]
    public void A_graph_that_fits_at_a_readable_zoom_fits_whole_and_centred()
    {
        var extent = new Rect(100, 100, 1500, 400);

        var zoom = CanvasChrome.FitZoom(extent, Room);
        var location = CanvasChrome.ViewOn(extent, Room, zoom, new Point(100, 100));

        Assert.Equal(0.4, zoom, 3);
        Assert.Equal(extent.Center, location + (Vector)Room.Center / zoom, new PointComparer());
    }

    [Fact]
    public void Fit_never_zooms_past_the_size_the_bar_calls_100_percent()
    {
        Assert.Equal(ZoomTiers.ActualSize, CanvasChrome.FitZoom(new Rect(0, 0, 260, 64), Room));
    }

    [Fact]
    public void A_huge_graph_stops_at_the_least_zoom_around_its_focus()
    {
        var extent = new Rect(0, 0, 40000, 20000);

        var zoom = CanvasChrome.FitZoom(extent, Room);
        var location = CanvasChrome.ViewOn(extent, Room, zoom, new Point(20000, 10000));

        Assert.Equal(CanvasChrome.LeastFitZoom, zoom);
        Assert.Equal(new Point(20000, 10000), location + (Vector)Room.Center / zoom);
    }

    [Fact]
    public void A_focus_near_the_graphs_edge_keeps_the_graph_in_the_room()
    {
        var extent = new Rect(0, 0, 40000, 20000);
        var room = new Rect(24, 60, 632, 420);

        var location = CanvasChrome.ViewOn(extent, room, CanvasChrome.LeastFitZoom, new Point(100, 30));

        // The graph's top left corner meets the room's top left corner, so no card sits under the bars.
        Assert.Equal(room.TopLeft, (Point)((extent.TopLeft - location) * CanvasChrome.LeastFitZoom), new PointComparer());
    }

    [Fact]
    public void A_view_moves_into_the_gaps_so_it_cuts_no_card_and_keeps_the_found_one()
    {
        Rect[] column = [new(0, 0, 240, 90), new(0, 190, 240, 90), new(0, 380, 240, 90), new(0, 570, 240, 90)];

        var location = CanvasChrome.ClearOfCards(new Point(-40, 150), new Size(320, 340), new Rect(12, 12, 296, 316), 1, column, column[2]);

        var view = new Rect(location, new Size(320, 340));
        Assert.All(column, card => Assert.True(view.Contains(card) || !view.Intersects(card), $"{view} cuts {card}."));
        Assert.True(view.Contains(column[2]));
    }

    [Fact]
    public void A_view_with_no_clear_place_stays_where_it_is()
    {
        Rect[] wall = [new(0, 0, 2000, 90)];

        Assert.Equal(new Point(100, -100), CanvasChrome.ClearOfCards(new Point(100, -100), new Size(320, 340), new Rect(12, 12, 296, 316), 1, wall, new Rect(120, 0, 240, 90)));
    }

    private sealed class PointComparer : IEqualityComparer<Point>
    {
        public bool Equals(Point x, Point y) => Point.Distance(x, y) < 0.001;

        public int GetHashCode(Point point) => 0;
    }
}
