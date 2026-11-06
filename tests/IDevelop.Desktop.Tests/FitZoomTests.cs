using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

/// <summary>Fit keeps titles readable (ADR 0045).</summary>
public sealed class FitZoomTests
{
    private static readonly Rect Room = new(0, 0, 680, 500);

    [Fact]
    public void A_graph_that_fits_at_a_readable_zoom_fits_whole_and_centred()
    {
        var extent = new Rect(100, 100, 1000, 400);

        var zoom = CanvasChrome.FitZoom(extent, Room);
        var location = CanvasChrome.ViewOn(extent, Room, zoom, new Point(100, 100));

        Assert.Equal(0.68, zoom, 3);
        Assert.Equal(extent.Center, location + (Vector)Room.Center / zoom);
    }

    [Fact]
    public void Fit_never_zooms_past_100_percent()
    {
        Assert.Equal(1, CanvasChrome.FitZoom(new Rect(0, 0, 260, 64), Room));
    }

    [Fact]
    public void A_large_graph_stops_at_the_readable_zoom_around_its_focus()
    {
        var extent = new Rect(0, 0, 4000, 2000);

        var zoom = CanvasChrome.FitZoom(extent, Room);
        var location = CanvasChrome.ViewOn(extent, Room, zoom, new Point(2000, 1000));

        Assert.Equal(CanvasChrome.ReadableZoom, zoom);
        Assert.Equal(new Point(2000, 1000), location + (Vector)Room.Center / zoom);
    }

    [Fact]
    public void A_focus_near_the_graphs_edge_keeps_the_graph_in_the_room()
    {
        var extent = new Rect(0, 0, 4000, 2000);
        var room = new Rect(24, 60, 632, 420);

        var location = CanvasChrome.ViewOn(extent, room, CanvasChrome.ReadableZoom, new Point(100, 30));

        // The graph's top left corner meets the room's top left corner, so no card sits under the bars.
        Assert.Equal(room.TopLeft, (Point)((extent.TopLeft - location) * CanvasChrome.ReadableZoom), new PointComparer());
    }

    private sealed class PointComparer : IEqualityComparer<Point>
    {
        public bool Equals(Point x, Point y) => Point.Distance(x, y) < 0.001;

        public int GetHashCode(Point point) => 0;
    }
}
