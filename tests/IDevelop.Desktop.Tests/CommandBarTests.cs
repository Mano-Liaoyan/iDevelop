using IDevelop.Desktop.Chrome;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

/// <summary>How the Command Bar shares its width, what the caption buttons take, and the Run Strip's progress segments.</summary>
public sealed class CommandBarTests
{
    [Theory]
    [InlineData(864, 240, 480, 360)]
    [InlineData(600, 240, 360, 240)]
    [InlineData(592, 300, 320, 272)]
    [InlineData(370, 300, 250, 120)]
    [InlineData(150, 300, 96, 54)]
    public void The_Run_Strip_keeps_320_to_480_px_and_gives_way_only_to_the_breadcrumbs_least_room(double room, double crumb, double strip, double crumbRoom)
    {
        Assert.Equal((strip, crumbRoom), CommandBarPanel.Share(room, crumb));
    }

    [Theory]
    [InlineData(false, true, false, 0, 0)]
    [InlineData(true, true, false, 0, 138)]
    [InlineData(true, false, true, 76, 0)]
    [InlineData(true, false, false, 0, 0)]
    public void The_bar_reserves_the_caption_buttons_width_only_in_an_extended_title_bar(bool extended, bool windows, bool mac, double left, double right)
    {
        Assert.Equal((left, right), CommandBar.CaptionReserve(extended, windows, mac));
    }

    [Fact]
    public void The_segments_follow_the_rollup_order_and_the_counts()
    {
        Assert.Equal(
            [("StateWaitingStrongBrush", 20d), ("StateFailedStrongBrush", 10d), ("StateRunningBrush", 20d), ("StateWarningStrongBrush", 10d), ("StateSuccessStrongBrush", 30d)],
            StateBar.Segments(new StateCounts(3, 2, 2, 1, 1, 12), 120));
        Assert.Empty(StateBar.Segments(new StateCounts(0, 0, 0, 0, 0, 0), 120));
    }
}
