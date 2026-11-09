using IDevelop.Desktop.Conversation;
using Xunit;

namespace IDevelop.Desktop.Tests;

public sealed class ChatDetailTests
{
    [Fact]
    public void A_line_breaks_between_tokens_and_keeps_a_name_whole()
    {
        var shown = CodeText.Wrapped("var value=Random.Shared.NextDouble()", 22);

        Assert.DoesNotContain("Ran\n", shown);
        Assert.Contains("NextDouble", shown);
    }

    [Fact]
    public void The_first_line_stays_clear_of_the_corner_tools()
    {
        var shown = CodeText.Wrapped("aaaa bbbb cccc dddd", 19, reservedFirstLine: 5);

        Assert.True(shown.Split('\n')[0].Length <= 14);
    }

    [Fact]
    public void A_table_keeps_its_widths_while_it_fits_and_gives_way_when_it_does_not()
    {
        Assert.Equal([100, 200], FitTable.Widths([100, 200], 400));

        var fitted = FitTable.Widths([300, 300, 300], 450);

        Assert.All(fitted, width => Assert.True(width >= 64));
        Assert.Equal(450, fitted.Sum(), 1);
    }
}
