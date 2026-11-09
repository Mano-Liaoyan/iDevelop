using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

public sealed class CardWordsTests
{
    [Fact]
    public void A_wire_s_line_of_result_takes_one_line_and_marks_what_it_loses()
    {
        Assert.Equal("Done. It works.", CardText.Line("Done.\n\nIt   works.", 200));
        Assert.Equal("Done.…", CardText.Line("Done. It works.", 5));
    }
}
