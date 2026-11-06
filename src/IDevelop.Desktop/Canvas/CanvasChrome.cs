using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The rules for the bars docked around the canvas: the breadcrumb, Run Workflow, Generate, the zoom controls, the run
/// bar, and the minimap. Each decides from the canvas's own size, so all of them switch together.
/// </summary>
internal static class CanvasChrome
{
    /// <summary>The least room between two controls side by side in a bar.</summary>
    public const double Spacing = 12;

    /// <summary>
    /// Below this canvas width the canvas is compact: Run Workflow and Stop Workflow show only their glyphs and the tooltip names them,
    /// and the breadcrumb shows only the workflow's name.
    /// </summary>
    public const double CompactWidth = 680;

    /// <summary>
    /// The lowest zoom of a fit, the editor's least. A graph that takes less stops here, and the rest is for panning.
    /// </summary>
    public const double LeastFitZoom = 0.1;

    /// <summary>Below this canvas height, as with the conversation docked under it, the minimap hides.</summary>
    public const double MinimapMinHeight = 480;

    /// <summary>
    /// Below this canvas width the run bar's words stand in for its chips, its tooltip keeps the whole text, the view controls
    /// move to the overflow menu, and the minimap hides.
    /// </summary>
    public const double NarrowBarWidth = 520;

    /// <summary>Below this canvas width the run bar's progress shortens to "5/9", so its chips fit whole.</summary>
    public const double ShortBarWidth = 760;

    /// <summary>The zoom that fits <paramref name="extent"/> in <paramref name="room"/>, never above 100% and never under <see cref="LeastFitZoom"/>.</summary>
    public static double FitZoom(Rect extent, Rect room) =>
        Math.Clamp(Math.Min(room.Width / extent.Width, room.Height / extent.Height), LeastFitZoom, 1);

    /// <summary>
    /// The viewport location that shows the most of <paramref name="extent"/> around <paramref name="focus"/> in
    /// <paramref name="room"/>, a part of the view. Along an axis where the extent fits, it centres the extent. Along an axis
    /// where it does not, the view stays on <paramref name="focus"/> and stops at the extent's edge, so no empty canvas shows
    /// past it.
    /// </summary>
    public static Point ViewOn(Rect extent, Rect room, double zoom, Point focus)
    {
        var seen = room.Size / zoom;
        double Middle(double near, double far, double span, double on) =>
            far - near <= span ? (near + far) / 2 : Math.Clamp(on, near + span / 2, far - span / 2);
        var center = new Point(Middle(extent.Left, extent.Right, seen.Width, focus.X), Middle(extent.Top, extent.Bottom, seen.Height, focus.Y));
        return OnWholePixels(center - (Vector)room.Center / zoom, zoom);
    }

    /// <summary>The nearest viewport location that puts the canvas's origin on a whole screen pixel, so a view leaves a card's edges sharp.</summary>
    public static Point OnWholePixels(Point location, double zoom) =>
        new(Math.Round(location.X * zoom) / zoom, Math.Round(location.Y * zoom) / zoom);

    /// <summary>
    /// The viewport location near <paramref name="location"/> that cuts the fewest cards at the view's edges and keeps all of
    /// <paramref name="keep"/> inside <paramref name="room"/>, the part of the view where a card rests. <paramref name="view"/>
    /// and <paramref name="room"/> are in screen pixels, and the rest in canvas units. A view zoomed in on one card lands in
    /// the gaps between the cards around it.
    /// </summary>
    public static Point ClearOfCards(Point location, Size view, Rect room, double zoom, IReadOnlyCollection<Rect> cards, Rect keep)
    {
        const double margin = 12;
        static int Cut(double near, double far, IEnumerable<(double Near, double Far)> spans) =>
            spans.Count(span => (span.Near < near && span.Far > near) || (span.Near < far && span.Far > far));
        double Best(double from, double span, double roomNear, double roomFar, double keepNear, double keepFar, (double Near, double Far)[] spans)
        {
            // The locations that hold the kept card in the room. A card wider than the room keeps its near edge in view.
            var last = keepNear - roomNear;
            var first = Math.Min(last, keepFar - roomFar);
            var start = Math.Clamp(from, first, last);
            var reach = span / 2;
            var choices = spans.SelectMany(card => new[] { card.Near - margin, card.Far + margin, card.Near + margin - span, card.Far + margin - span, card.Near - roomNear, card.Far - roomFar })
                .Where(near => Math.Abs(near - start) <= reach && near >= first && near <= last)
                .Prepend(start);
            return choices.OrderBy(near => Cut(near, near + span, spans)).ThenBy(near => Math.Abs(near - start)).First();
        }

        return new Point(
            Best(location.X, view.Width / zoom, room.Left / zoom, room.Right / zoom, keep.Left, keep.Right, [.. cards.Select(card => (card.Left, card.Right))]),
            Best(location.Y, view.Height / zoom, room.Top / zoom, room.Bottom / zoom, keep.Top, keep.Bottom, [.. cards.Select(card => (card.Top, card.Bottom))]));
    }

    public static bool IsShortBar(Size canvas) => canvas.Width < ShortBarWidth;

    public static bool IsNarrowBar(Size canvas) => canvas.Width < NarrowBarWidth;

    /// <summary>Whether Add and Generate give up their words, so the workflow's name in the breadcrumb keeps its width.</summary>

    public static bool IsCompact(Size canvas) => canvas.Width < CompactWidth;

    public static bool ShowsMinimap(Size canvas) => !IsNarrowBar(canvas) && canvas.Height >= MinimapMinHeight;
}
