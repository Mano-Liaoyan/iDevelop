namespace IDevelop.Desktop.Canvas;

/// <summary>How much a card shows at the canvas's zoom. Every tier shows the card's title, never under 9 px on screen.</summary>
public enum ZoomTier { Full, Detail, Compact, Label, Title, Block }

/// <summary>A Title or Block tier card's text size and line height in canvas units, its title lines, and how far the card draws under its 64 px box.</summary>
public readonly record struct FarText(double Size, double Line, int Lines, double Reach);

public static class ZoomTiers
{
    private const double FullFrom = 0.84;
    private const double DetailFrom = 0.6;
    private const double CompactFrom = 0.5;
    private const double LabelFrom = 0.36;
    private const double TitleFrom = 0.23;

    /// <summary>The least zoom at which a card's title reads at a glance.</summary>
    public const double ReadableZoom = LabelFrom;

    /// <summary>The zoom that the bar calls 100%: a card here reads at the app's body size, title and agent, at a size that fits a workflow.</summary>
    public const double ActualSize = 0.4;

    /// <summary>The largest zoom of a fit: 120%, the last zoom at which a card keeps the form it has at 100%.</summary>
    public const double LargestFitZoom = 0.48;

    /// <summary>The least size in screen pixels of a Title or Block tier card's text.</summary>
    public const double ReadablePixels = 9;

    /// <summary>The most title lines of a Title tier card.</summary>
    private const int TitleLines = 3;

    /// <summary>The space in canvas units over a Title tier card's title.</summary>
    public const double TitleTop = 2;

    /// <summary>The space in canvas units under a Title tier card's agent's line, which holds the line's descenders.</summary>
    private const double TitleBottom = 5;

    /// <summary>The least gap in screen pixels between a Title tier card that grows and the card under it.</summary>
    public const double CardGap = 6;

    /// <summary>The height in canvas units for the title of a Block tier card, which has no agent's line and no badge.</summary>
    private const double BlockRoom = 142;

    /// <summary>The most title lines of a Block tier card.</summary>
    private const int BlockLines = 2;

    /// <summary>A zoom that rises enters the larger tier this far past its threshold, so a zoom near a threshold does not flicker.</summary>
    private const double Gap = 0.012;

    /// <summary>The tier at <paramref name="zoom"/>, after <paramref name="current"/>. A falling zoom leaves a tier at its threshold.</summary>
    public static ZoomTier Next(ZoomTier current, double zoom)
    {
        var (at, past) = (Of(zoom), Of(zoom - Gap));
        return at > current ? at : past < current ? past : current;
    }

    /// <summary>How much thicker a wire draws in the tier, so that it stays near its weight on screen.</summary>
    public static double WireScale(ZoomTier tier) => tier switch
    {
        ZoomTier.Detail => 1.3,
        ZoomTier.Compact => 1.5,
        ZoomTier.Label => 2,
        ZoomTier.Title => 3,
        ZoomTier.Block => 3,
        _ => 1,
    };

    /// <summary>How far a collapsed card draws under its 64 px box in the tier. The styles set the same heights, and the wires go around them.</summary>
    public static double CardReach(ZoomTier tier) => tier switch
    {
        ZoomTier.Detail => 30,
        ZoomTier.Compact => 34,
        ZoomTier.Label => 90,
        ZoomTier.Title => 94,
        ZoomTier.Block => 94,
        _ => 0,
    };

    /// <summary>
    /// The text size in canvas units that keeps a Title or Block tier card's text just readable on screen at <paramref name="zoom"/>,
    /// with its line height, the title lines, and how far the card draws under its 64 px box.
    /// A Title tier card takes up to three title lines over its agent's line and grows to hold them, but only as far as
    /// <paramref name="pitch"/>, the least distance in canvas units from a card's top to the top of the card under it, leaves a 6 px gap (ADR 0068).
    /// A Block tier card gives up its agent's line and its badge, so its title keeps one or two lines at 9 px at every zoom.
    /// </summary>
    public static FarText TitleText(double zoom, ZoomTier tier = ZoomTier.Title, double pitch = double.PositiveInfinity)
    {
        var least = 2 * Math.Ceiling(ReadablePixels / zoom / 2);
        if (tier == ZoomTier.Block)
        {
            var blockSize = Math.Max(least, 30);
            var blockLine = Math.Ceiling(blockSize * 1.04);
            return new(blockSize, blockLine, Math.Clamp((int)(BlockRoom / blockLine), 1, BlockLines), CardReach(tier));
        }

        // The Title tier's size steps by one, so the text keeps as close to 9 px as the room for three lines needs.
        var size = Math.Clamp(Math.Ceiling(ReadablePixels / zoom), 30, 44);
        var line = Math.Ceiling(size * 1.04);
        var drawn = 64 + CardReach(ZoomTier.Title);
        var room = Math.Max(drawn, pitch - CardGap / zoom);
        var lines = TitleLines;
        while (lines > 1 && Height(lines) > room)
        {
            lines--;
        }

        return new(size, line, lines, Math.Max(drawn, Height(lines)) - 64);

        // The title's lines and the agent's line, one line high, under the top's space.
        double Height(int count) => TitleTop + (count + 1) * line + TitleBottom;
    }

    /// <summary>The style class that the canvas takes in the tier, or null for the full card.</summary>
    public static string? StyleClass(ZoomTier tier) => tier switch
    {
        ZoomTier.Detail => "tier-detail",
        ZoomTier.Compact => "tier-compact",
        ZoomTier.Label => "tier-label",
        ZoomTier.Title => "tier-title",
        ZoomTier.Block => "tier-block",
        _ => null,
    };

    private static ZoomTier Of(double zoom) =>
        zoom >= FullFrom ? ZoomTier.Full : zoom >= DetailFrom ? ZoomTier.Detail : zoom >= CompactFrom ? ZoomTier.Compact : zoom >= LabelFrom ? ZoomTier.Label : zoom >= TitleFrom ? ZoomTier.Title : ZoomTier.Block;
}
