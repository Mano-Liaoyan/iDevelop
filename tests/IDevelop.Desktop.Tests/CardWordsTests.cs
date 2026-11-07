using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;

namespace IDevelop.Desktop.Tests;

public sealed class CardWordsTests
{
    [Theory]
    [InlineData("pi exited with code 1: 402 Provider balance is too low for model qwen-4-max. Add credit or choose another model.", true)]
    [InlineData("Quota exceeded", true)]
    [InlineData("The build failed with 3 errors.", false)]
    public void A_failure_that_a_retry_cannot_fix_asks_for_another_model(string reason, bool needs) =>
        Assert.Equal(needs, ConversationViewModel.NeedsAnotherModel(reason));

    [Fact]
    public void A_wire_s_line_of_result_takes_one_line_and_marks_what_it_loses()
    {
        Assert.Equal("Done. It works.", CardText.Line("Done.\n\nIt   works.", 200));
        Assert.Equal("Done.…", CardText.Line("Done. It works.", 5));
    }
}
