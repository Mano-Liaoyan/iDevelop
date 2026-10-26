using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

/// <summary>
/// The verdict read from a reviewer's final message (#101): each finding keeps the severity and the location the reviewer
/// gave it, and a verdict without them stays readable.
/// </summary>
public sealed class VerdictTests
{
    private static string Block(string json) => $"I read the change.\n\n```idevelop\n{json}\n```";

    [Fact]
    public void A_verdict_without_severity_and_location_stays_readable()
    {
        var (verdict, problem) = Verdict.Read(Block(
            """{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}"""));

        Assert.Null(problem);
        var standing = Assert.Single(verdict!.Findings);
        Assert.Equal(("1", "add subtracts.", null, null), (standing.Id, standing.Text, standing.Severity, standing.Location));
    }

    [Fact]
    public void A_severity_or_location_that_is_not_text_is_left_out_and_never_fails_the_verdict()
    {
        var (verdict, problem) = Verdict.Read(Block(
            """
            {"status": "verdict", "verdict": "changes", "findings": [
              {"id": "1", "severity": 3, "location": ["src/Calc.cs", 12], "text": "add subtracts.", "change": "Return a + b."},
              {"id": "2", "severity": " ", "location": "", "text": "add has no docstring.", "change": "Add one."}]}
            """));

        Assert.Null(problem);
        Assert.Equal([(null, null), (null, null)], verdict!.Findings.Select(standing => (standing.Severity, standing.Location)));
    }

    [Fact]
    public void The_verdict_format_asks_each_finding_for_its_severity_and_its_location()
    {
        Assert.Contains("""{"id": "1", "severity": "high", "location": "src/Calc.cs:12", "text": "what is wrong", """, BuiltInBlueprints.VerdictFormat);
        Assert.Contains("Give each finding a severity, \"high\", \"medium\", or \"low\", and its location, the file and line it concerns, such as src/Calc.cs:12.",
            BuiltInBlueprints.VerdictFormat);
    }
}
