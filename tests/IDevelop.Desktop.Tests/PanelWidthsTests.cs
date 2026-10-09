namespace IDevelop.Desktop.Tests;

/// <summary>
/// The widths the sidebar and the inspector may show beside a canvas of at least 336 px: their chosen widths while there
/// is room, then the inspector gives way to its least width before the sidebar does.
/// </summary>
public sealed class PanelWidthsTests
{
    private static PanelWidths.Side Sidebar(double chosen) => new(chosen, 200, 400);

    private static PanelWidths.Side Inspector(double chosen) => new(chosen, 280, 520);

    // The room beside the canvas in a window of the given width, less the two 1 px splitters.
    private static double Room(double window) => window - 2 - PanelWidths.CanvasMinWidth;

    [Theory]
    [InlineData(1600, 240, 520, 400, 520)]
    [InlineData(1280, 240, 520, 400, 520)]
    [InlineData(900, 240, 320, 242, 322)]
    [InlineData(900, 240, 520, 240, 322)]
    [InlineData(900, 400, 520, 282, 280)]
    [InlineData(900, 400, 300, 282, 280)]
    public void Each_panel_keeps_its_chosen_width_while_the_canvas_keeps_336_px(double window, double sidebar, double inspector, double sidebarMax, double inspectorMax)
    {
        Assert.Equal((sidebarMax, inspectorMax), PanelWidths.Limits(Room(window), Sidebar(sidebar), Inspector(inspector)));
    }
}
