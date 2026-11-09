namespace IDevelop.Desktop.Tests;

/// <summary>The width tiers and the widths the Index and the Agent panel may show beside a Map of at least 336 px.</summary>
public sealed class PanelWidthsTests
{
    private static PanelWidths.Side Index(double chosen) => new(chosen, 240, 400);

    private static PanelWidths.Side Panel(double chosen) => new(chosen, 360, 560);

    // The room beside the Map in a window of the given width, less the outer margins and the two 4 px gaps.
    private static double Room(double window) => window - 16 - PanelWidths.CanvasMinWidth;

    [Theory]
    [InlineData(900, WidthTier.Compact)]
    [InlineData(1179, WidthTier.Compact)]
    [InlineData(1180, WidthTier.Default)]
    [InlineData(1699, WidthTier.Default)]
    [InlineData(1700, WidthTier.Roomy)]
    [InlineData(2199, WidthTier.Roomy)]
    [InlineData(2200, WidthTier.Large)]
    public void A_window_s_width_alone_decides_its_tier(double width, WidthTier tier) => Assert.Equal(tier, PanelWidths.TierOf(width));

    [Theory]
    [InlineData(WidthTier.Compact, 252, 252, 252, 372, 372, 372)]
    [InlineData(WidthTier.Default, 252, 240, 400, 400, 360, 560)]
    [InlineData(WidthTier.Roomy, 280, 240, 400, 520, 360, 720)]
    [InlineData(WidthTier.Large, 300, 240, 400, 1008, 880, 1200)]
    public void A_tier_gives_each_sheet_its_width_and_its_limits(WidthTier tier, double index, double indexMin, double indexMax, double panel, double panelMin, double panelMax)
    {
        Assert.Equal((new PanelWidths.SheetSize(index, indexMin, indexMax), new PanelWidths.SheetSize(panel, panelMin, panelMax)), PanelWidths.SizesOf(tier));
    }

    [Theory]
    [InlineData(1600, 240, 560, 400, 560)]
    [InlineData(1280, 240, 560, 368, 560)]
    [InlineData(1180, 400, 560, 400, 428)]
    [InlineData(1180, 240, 560, 268, 560)]
    [InlineData(1000, 400, 560, 288, 360)]
    public void A_narrow_window_takes_width_from_the_Agent_panel_first_and_then_from_the_Index(double window, double index, double panel, double indexMax, double panelMax)
    {
        Assert.Equal((indexMax, panelMax), PanelWidths.Limits(Room(window), Index(index), Panel(panel)));
    }
}
