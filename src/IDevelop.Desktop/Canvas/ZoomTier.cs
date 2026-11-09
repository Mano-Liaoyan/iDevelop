namespace IDevelop.Desktop.Canvas;

/// <summary>How much a card shows at the Map's zoom.</summary>
public enum ZoomTier { Full, Plate, Block }

/// <summary>
/// The Graph's sizes in canvas units at one zoom, so a card's text and strokes stay near their size on screen (brief S2).
/// </summary>
/// <param name="TitleSize">The card title's font size. It grows under 100% so it never draws under 11.5 px on screen at the Full tier.</param>
/// <param name="TitleLine">The card title's line height.</param>
/// <param name="CaptionSize">The socket and agent rows' font size.</param>
/// <param name="CaptionLine">The socket and agent rows' line height.</param>
/// <param name="PlateTitleSize">The Plate tier's title size, 12 px on screen.</param>
/// <param name="PlateTitleLine">The Plate tier's title line height.</param>
/// <param name="PlateLines">How many title lines fit in a Plate tier card.</param>
/// <param name="MarkSize">A Plate or Block tier card's State Mark.</param>
/// <param name="SocketSize">A Plate tier socket, 6 px on screen.</param>
/// <param name="Stroke">A card's state and selection rings, 1.5 px on screen.</param>
/// <param name="Halo">The selection's halo, 3 px on screen.</param>
/// <param name="Inset">The space inside a Plate tier card.</param>
public readonly record struct GraphMetrics(
    double TitleSize, double TitleLine, double CaptionSize, double CaptionLine, double PlateTitleSize, double PlateTitleLine, int PlateLines,
    double MarkSize, double SocketSize, double Stroke, double Halo, double Inset);

public static class ZoomTiers
{
    private const double FullFrom = 0.75;
    private const double PlateFrom = 0.3;

    /// <summary>The least zoom at which a card's title reads: the Plate tier.</summary>
    public const double ReadableZoom = PlateFrom;

    /// <summary>The largest zoom of a fit: 100%, a card at its true size.</summary>
    public const double LargestFitZoom = 1;

    private const double PlateTitlePixels = 12;
    private const double PlateLinePixels = 14;
    private const double PlateMarkPixels = 14;
    private const double PlateSocketPixels = 6;
    private const double PlateInsetPixels = 4;

    // A card's text stays at the Type ramp's Caption and Body sizes on screen while the Full tier zooms out.
    private const double LeastTextPixels = 11.5;
    private const double TitleSize = 13;
    private const double TitleLine = 18;
    private const double CaptionSize = 11.5;
    private const double CaptionLine = 14;

    // The card's box height, which holds a Plate tier title's lines.
    private const double PlateCardHeight = 94;

    /// <summary>A rising zoom enters a larger tier this far past its threshold, so a zoom near a threshold does not flicker.</summary>
    private const double Gap = 0.012;

    /// <summary>Whether a card at <paramref name="tier"/> gives up its agent's name, so a workflow that fits only at this tier opens in Flow.</summary>
    public static bool HidesNames(ZoomTier tier) => tier == ZoomTier.Block;

    /// <summary>The tier at <paramref name="zoom"/>, after <paramref name="current"/>. A falling zoom leaves a tier at its threshold.</summary>
    public static ZoomTier Next(ZoomTier current, double zoom)
    {
        var (at, past) = (Of(zoom), Of(zoom - Gap));
        return at > current ? at : past < current ? past : current;
    }

    /// <summary>How much thicker a wire draws so it stays near its weight on screen under 100%, in steps of a quarter.</summary>
    public static double WireScale(double zoom) => zoom >= 1 ? 1 : Math.Round(4 / zoom) / 4;

    /// <summary>The sizes at <paramref name="zoom"/>.</summary>
    public static GraphMetrics Metrics(double zoom)
    {
        var least = Math.Max(1, LeastTextPixels / TitleSize / zoom);
        var (title, line) = zoom >= FullFrom ? (TitleSize * least, TitleLine * least) : (TitleSize, TitleLine);
        var captionScale = zoom >= FullFrom ? Math.Max(1, 1 / zoom) : 1;
        var plateLine = PlateLinePixels / zoom;
        var lines = plateLine * 2 + 2 * PlateInsetPixels / zoom <= PlateCardHeight ? 2 : 1;
        var pixel = Math.Max(1, 1 / zoom);
        return new GraphMetrics(
            title, line, CaptionSize * captionScale, CaptionLine * captionScale, PlateTitlePixels / zoom, plateLine, lines,
            Math.Min(PlateMarkPixels / zoom, PlateCardHeight * 0.6), PlateSocketPixels / zoom, 1.5 * pixel, 3 * pixel, PlateInsetPixels / zoom);
    }

    /// <summary>The style class that the canvas takes in the tier.</summary>
    public static string StyleClass(ZoomTier tier) => tier switch
    {
        ZoomTier.Plate => "tier-plate",
        ZoomTier.Block => "tier-block",
        _ => "tier-full",
    };

    internal static ZoomTier Of(double zoom) => zoom >= FullFrom ? ZoomTier.Full : zoom >= PlateFrom ? ZoomTier.Plate : ZoomTier.Block;
}
