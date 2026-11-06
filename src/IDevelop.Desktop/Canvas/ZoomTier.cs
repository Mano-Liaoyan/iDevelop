namespace IDevelop.Desktop.Canvas;

/// <summary>How much a card shows at the canvas's zoom, so a title never draws under 10 px on screen.</summary>
public enum ZoomTier { Full, Detail, Compact, Label, Title, Block }

public static class ZoomTiers
{
    private const double FullFrom = 0.84;
    private const double DetailFrom = 0.6;
    private const double CompactFrom = 0.5;
    private const double LabelFrom = 0.36;
    private const double TitleFrom = 0.23;

    /// <summary>A zoom that rises enters the larger tier this far past its threshold, so a zoom near a threshold does not flicker.</summary>
    private const double Gap = 0.02;

    /// <summary>The tier at <paramref name="zoom"/>, after <paramref name="current"/>. A falling zoom leaves a tier at its threshold.</summary>
    public static ZoomTier Next(ZoomTier current, double zoom)
    {
        var (at, past) = (Of(zoom), Of(zoom - Gap));
        return at > current ? at : past < current ? past : current;
    }

    /// <summary>How much thicker a wire draws in the tier, so that it stays near its weight on screen.</summary>
    public static double WireScale(ZoomTier tier) => tier switch
    {
        ZoomTier.Detail or ZoomTier.Compact => 1.2,
        ZoomTier.Label => 1.5,
        ZoomTier.Title => 2,
        ZoomTier.Block => 2.5,
        _ => 1,
    };

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
