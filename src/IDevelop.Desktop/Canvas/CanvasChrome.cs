using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The rules for the controls that float over the canvas: the breadcrumb, Run Workflow, Generate, the zoom controls,
/// the run bar, and the minimap. Each decides from the canvas's own size, so all of them switch together.
/// </summary>
internal static class CanvasChrome
{
    /// <summary>Every floating control keeps this far from the canvas's edges.</summary>
    public const double Inset = 12;

    /// <summary>The least room between two floating controls side by side.</summary>
    public const double Spacing = 12;

    /// <summary>
    /// Below this canvas width the canvas is compact: Generate and the run bar's buttons show only their glyphs, Run Workflow says Run,
    /// the breadcrumb shows only the workflow's name, and the minimap hides.
    /// </summary>
    public const double CompactWidth = 700;

    /// <summary>
    /// The lowest zoom at which Fit keeps a graph whole. A card's title still reads there, in two lines over 10 px on screen. A
    /// larger graph stops here, and the rest is for panning and the minimap.
    /// </summary>
    public const double ReadableZoom = 0.4;

    /// <summary>Below this canvas height, as with the conversation docked under it, the minimap hides.</summary>
    public const double MinimapMinHeight = 480;

    /// <summary>Below this canvas width the run bar's summary shortens to "5/9", and its tooltip keeps the whole text.</summary>
    public const double NarrowBarWidth = 520;

    /// <summary>Below this canvas width the run bar's progress shrinks, so its summary fits whole.</summary>
    public const double ShortBarWidth = 760;

    /// <summary>The zoom that fits <paramref name="extent"/> in <paramref name="room"/>, never above 100% and never under <see cref="ReadableZoom"/>.</summary>
    public static double FitZoom(Rect extent, Rect room) =>
        Math.Clamp(Math.Min(room.Width / extent.Width, room.Height / extent.Height), ReadableZoom, 1);

    /// <summary>
    /// The viewport location that shows the most of <paramref name="extent"/> around <paramref name="focus"/> in
    /// <paramref name="room"/>, a part of the view. Along an axis where the extent fits, it centres the extent. Along an axis
    /// where it does not, the view stays on <paramref name="focus"/> and stops at the extent's edge, so no card sits under a
    /// control while empty canvas shows past it.
    /// </summary>
    public static Point ViewOn(Rect extent, Rect room, double zoom, Point focus)
    {
        var seen = room.Size / zoom;
        double Middle(double near, double far, double span, double on) =>
            far - near <= span ? (near + far) / 2 : Math.Clamp(on, near + span / 2, far - span / 2);
        var center = new Point(Middle(extent.Left, extent.Right, seen.Width, focus.X), Middle(extent.Top, extent.Bottom, seen.Height, focus.Y));
        return center - (Vector)room.Center / zoom;
    }

    public static bool IsShortBar(Size canvas) => canvas.Width < ShortBarWidth;

    /// <summary>From this many nodes a fit shows too little of each, so the minimap stays in view.</summary>
    public const int LargeGraph = 24;

    public static bool IsNarrowBar(Size canvas) => canvas.Width < NarrowBarWidth;

    public static bool IsCompact(Size canvas) => canvas.Width < CompactWidth;

    public static bool ShowsMinimap(Size canvas) => !IsCompact(canvas) && canvas.Height >= MinimapMinHeight;
}
