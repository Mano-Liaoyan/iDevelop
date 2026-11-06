using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Inspector;

namespace IDevelop.Desktop.Tests;

/// <summary>The one pattern for long text in the inspector (ADR 0045).</summary>
public sealed class FoldPanelTests
{
    private static FoldPanel Panel(double height, double rest = 100) =>
        new() { Rest = rest, Child = new Border { Height = height } };

    private static double Measured(FoldPanel panel)
    {
        panel.Measure(new Size(200, double.PositiveInfinity));
        return panel.DesiredSize.Height;
    }

    [AvaloniaFact]
    public void Text_that_fits_shows_whole_and_offers_no_More()
    {
        var panel = Panel(80);

        Assert.Equal(80, Measured(panel));
        Assert.False(panel.Folds);
    }

    [AvaloniaFact]
    public void Longer_text_rests_at_its_height_and_More_opens_it()
    {
        var panel = Panel(300);

        Assert.Equal(100, Measured(panel));
        Assert.True(panel.Folds);
        Assert.Equal("More", panel.MoreLabel);

        panel.IsOpen = true;

        Assert.Equal(300, Measured(panel));
        Assert.Equal("Less", panel.MoreLabel);
    }

    [AvaloniaFact]
    public void A_stack_of_blocks_rests_after_its_last_whole_block()
    {
        var panel = new FoldPanel
        {
            Rest = 100,
            Child = new StackPanel { Spacing = 4, Children = { new Border { Height = 40 }, new Border { Height = 40 }, new Border { Height = 40 } } },
        };

        Assert.Equal(84, Measured(panel));
    }
}
