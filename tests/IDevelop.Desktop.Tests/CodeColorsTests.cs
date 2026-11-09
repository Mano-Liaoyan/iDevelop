using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Conversation;

namespace IDevelop.Desktop.Tests;

public class CodeColorsTests
{
    [AvaloniaFact]
    public void Named_code_keeps_every_character_and_colors_keywords_strings_and_comments()
    {
        const string code = "var name = \"if\"; // done";

        var runs = CodeColors.Runs(code, "csharp")!.OfType<Run>().ToList();

        Assert.Equal(code, string.Concat(runs.Select(run => run.Text)));
        Assert.Equal(["var", "\"if\"", "// done"], runs.Where(run => run.Classes.Count > 0).Select(run => run.Text));
        Assert.Equal(["codeKeyword", "codeString", "codeComment"], runs.Where(run => run.Classes.Count > 0).Select(run => run.Classes[0]));
    }

    [AvaloniaFact]
    public void A_hash_is_a_comment_only_in_a_language_that_says_so()
    {
        Assert.Contains(CodeColors.Runs("# note", "sh")!.OfType<Run>(), run => run.Classes.Contains("codeComment"));
        Assert.DoesNotContain(CodeColors.Runs("# note", "markdown")!.OfType<Run>(), run => run.Classes.Contains("codeComment"));
    }

    [AvaloniaFact]
    public void Code_with_no_language_stays_plain() => Assert.Null(CodeColors.Runs("var x;", " "));
}
