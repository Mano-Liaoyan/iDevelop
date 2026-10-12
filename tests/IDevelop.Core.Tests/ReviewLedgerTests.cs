using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

/// <summary>
/// The findings ledger read from a review attempt's turns alone (#101): each finding keeps the severity and the location
/// the reviewer last gave it, and a verdict written before the contract asked for them stays readable.
/// </summary>
public sealed class ReviewLedgerTests
{
    private static string Verdict(string json) => $"I read the change.\n\n```idevelop\n{json}\n```";

    private static string Answers(string answers) => $"I answered each finding.\n\n```idevelop\n{{\"status\": \"answers\", \"answers\": {answers}}}\n```";

    /// <summary>A review attempt whose reviewer turns end with each verdict, each turn after the first carrying the fix report before it.</summary>
    private static AttemptRecord Review(params (string Verdict, string? Report)[] turns)
    {
        var requested = new AttemptEvent.Requested(DateTimeOffset.UnixEpoch, AttemptId.New(), TestTasks.Review, "Review", new(ClientId.Codex), "Review it.", "codex", []);
        var fix = AttemptId.New();
        return AttemptReducer.Replay([requested])! with
        {
            Turns = [.. turns.Select((turn, index) => new TurnRecord(index + 1, null, TurnOutcome.Succeeded, turn.Verdict)
            {
                Report = turn.Report is { } report ? new FixReport(fix, report, 0) : null,
            })],
        };
    }

    [Fact]
    public void A_finding_keeps_the_severity_and_location_the_reviewer_last_gave_it_after_it_is_settled()
    {
        var review = Review(
            (Verdict("""
                {"status": "verdict", "verdict": "changes", "findings": [
                  {"id": "1", "severity": "High", "location": " src/Calc.cs:12 ", "text": "add subtracts.", "change": "Return a + b."},
                  {"id": "2", "severity": "Low", "location": "README.md", "text": "add has no docstring.", "change": "Add one."}]}
                """), null),
            (Verdict("""
                {"status": "verdict", "verdict": "changes", "findings": [
                  {"id": "1", "severity": "medium", "location": "src/Calc.cs:14", "text": "add still subtracts once.", "change": "Return a + b."}],
                 "withdrawn": [{"id": "2", "reason": "The ticket asks for no docstring."}]}
                """), Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}, {"id": "2", "answer": "disputed", "note": "Not asked."}]""")),
            (Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""),
                Answers("""[{"id": "1", "answer": "fixed", "note": "Both branches add."}]""")));

        var ledger = ReviewLedger.Fold(review);

        Assert.Equal(
            [("1", FindingState.Resolved, "medium", "src/Calc.cs:14"), ("2", FindingState.Withdrawn, "low", "README.md")],
            ledger.Findings.Select(finding => (finding.Id, finding.State, finding.Severity, finding.Location)));
    }

    [Fact]
    public void A_finding_listed_again_without_a_severity_or_location_keeps_the_ones_the_reviewer_gave_before()
    {
        var review = Review(
            (Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "severity": "high", "location": "src/Calc.cs:12", "text": "add subtracts.", "change": "Return a + b."}]}"""), null),
            (Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add still subtracts.", "change": "Return a + b."}]}"""),
                Answers("""[{"id": "1", "answer": "disputed", "note": "It adds."}]""")));

        var finding = Assert.Single(ReviewLedger.Fold(review).Findings);

        Assert.Equal(("add still subtracts.", "high", "src/Calc.cs:12"), (finding.Text, finding.Severity, finding.Location));
    }

    [Fact]
    public void A_withdrawn_finding_keeps_what_the_reviewer_raised_beside_the_reason_it_was_withdrawn()
    {
        var review = Review(
            (Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add should round.", "change": "Return round(a + b)."}, {"id": "2", "text": "add has no docstring.", "change": "Add one."}]}"""), null),
            (Verdict("""{"status": "verdict", "verdict": "approve", "findings": [], "withdrawn": [{"id": "1", "reason": "The ticket wants the exact sum."}]}"""),
                Answers("""[{"id": "1", "answer": "disputed", "note": "The ticket asks for the exact sum."}, {"id": "2", "answer": "disputed", "note": "Not asked."}]""")));

        var findings = ReviewLedger.Fold(review).Findings;

        Assert.Equal(
            [("1", FindingState.Withdrawn, "add should round.", "The ticket wants the exact sum."), ("2", FindingState.Withdrawn, "add has no docstring.", null)],
            findings.Select(finding => (finding.Id, finding.State, finding.Text, finding.Reason)));
    }

    [Fact]
    public void A_verdict_written_before_the_contract_asked_for_severity_and_location_stays_readable()
    {
        var (verdict, problem) = ReviewLedger.ReadVerdict(Verdict(
            """{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}"""));

        Assert.Null(problem);
        var standing = Assert.Single(verdict!.Findings);
        Assert.Equal(("1", "add subtracts.", null, null), (standing.Id, standing.Text, standing.Severity, standing.Location));
    }

    [Fact]
    public void A_severity_or_location_that_is_not_text_is_left_out_and_never_fails_the_verdict()
    {
        var (verdict, problem) = ReviewLedger.ReadVerdict(Verdict(
            """
            {"status": "verdict", "verdict": "changes", "findings": [
              {"id": "1", "severity": 3, "location": ["src/Calc.cs", 12], "text": "add subtracts.", "change": "Return a + b."},
              {"id": "2", "severity": " ", "location": "", "text": "add has no docstring.", "change": "Add one."}]}
            """));

        Assert.Null(problem);
        Assert.Equal([(null, null), (null, null)], verdict!.Findings.Select(standing => (standing.Severity, standing.Location)));
    }

    [Fact]
    public void The_verdict_contract_asks_each_finding_for_its_severity_and_its_location()
    {
        Assert.Contains("""{"id": "1", "severity": "high", "location": "src/Calc.cs:12", "text": "what is wrong", """, ReviewWork.VerdictContract);
        Assert.Contains("Give each finding a severity, \"high\", \"medium\", or \"low\", and its location, the file and line it concerns, such as src/Calc.cs:12.",
            ReviewWork.VerdictContract);
    }
}
