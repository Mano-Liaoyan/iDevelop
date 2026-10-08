using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

/// <summary>
/// A read-only result needs its final launch's matched capture at the attempt base (E3a.5b). The refreshed-review row is
/// ReviewMaterializationTests.Review_fix_keeps_the_subject_checkout_and_agreement_forwards_the_refreshed_writer, whose
/// reviewer is accepted after its final launch's base moved.
/// </summary>
public sealed class ReadOnlyAcceptanceTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("files")]
    [InlineData("index")]
    public async Task Read_only_acceptance_checks_the_final_capture(string row)
    {
        await using var f = new TurnFixture(readOnly: true);
        await f.Open(FakeAgents.Fresh(f.Client)
            .Print(FakeAgents.SessionLine(f.Client, "session-1"))
            .Print(FakeAgents.ReplyLines(f.Client, "A ready.\n")));
        // After the exit snapshot and before both captures, so the snapshot guard passes and both captures match.
        f.Runs.Probe = point =>
        {
            if (point != "runner.checkpoint.after" || row == "unchanged") return;
            var file = Path.Combine(f.Checkout, "root.txt");
            File.WriteAllText(file, "changed\n");
            if (row == "files") return;
            Assert.Equal(0, f.Preparation.Git.Run(f.Checkout, "add", "root.txt").ExitCode);
            File.WriteAllText(file, "root\n");
        };
        var turn = await f.Settled(await f.Start());
        Assert.Equal("Succeeded", turn.Attempt.Status.ToString());
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        Assert.Equal(row == "files" ? "changed\n" : "root\n", File.ReadAllText(Path.Combine(f.Checkout, "root.txt")));
        var attempt = turn.Address.Launch.Attempt;
        Assert.IsType<RunDecision.Recorded>(f.Preparation.Store.CloseAttempt(f.Preparation.Permit, f.Preparation.Op(), attempt,
            TerminalAttemptOutcome.Succeeded, turn.Log));
        var accepted = f.Preparation.Store.AcceptReport(f.Preparation.Permit, f.Preparation.Op(), attempt, turn.Preparation.Inputs,
            turn.Attempt.Result!);
        if (row == "unchanged")
        {
            var result = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(accepted).Event).Result;
            Assert.Equal("A ready.\n", result.Report);
            Assert.Single(f.Preparation.Read().Results);
        }
        else
        {
            Assert.Equal("OutcomeMismatch", Assert.IsType<RunDecision.Rejected>(accepted).Reason.Problem.ToString());
            Assert.Empty(f.Preparation.Read().Results);
        }
    }
}
