using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using RunFixtures = IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// The coordinator consumes E3a's cleanup outcome as a diagnostic only. These tests switch the job and group launchers
/// off for the whole process, so they run alone.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class CleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_outcomes_never_gate_the_run(bool unavailable)
    {
        await using var f = new CoordinatorFixture(Graph([Agent(A), Agent(B)], (A, B)));
        f.Answer(A, Writes(A, "result.txt", "done\n")).Answer(B, FakeRule.On()
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-B"))
            .Copy("result.txt", Path.Combine(f.Evidence, "successor.txt"))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "B ready.\n")));
        await f.Open();
        ProcessGroup.LaunchFailure = unavailable;
        ProcessJob.AssignmentFailure = unavailable;
        try
        {
            await f.Resume();
            await f.UntilStatus(RunStatus.Completed);
        }
        finally
        {
            ProcessGroup.LaunchFailure = false;
            ProcessJob.AssignmentFailure = false;
        }
        var record = f.Read();
        var producer = ((ResultOrigin.Executed)record.CurrentResults[A].Origin).Attempt;
        var log = AttemptEvidence.Read(f.Preparation.Store.AttemptFolder(RunFixtures.W, f.Preparation.RunId, A, producer));
        var launched = Assert.Single(log.Events.OfType<AttemptEvent.Launched>());
        var cleaned = Assert.Single(log.Events.OfType<AttemptEvent.CleanedUp>());
        if (unavailable)
        {
            Assert.IsType<Containment.None>(launched.Containment);
            Assert.Equal(CleanupResult.Incomplete, cleaned.Result);
        }
        else
        {
            Assert.IsNotType<Containment.None>(launched.Containment);
            Assert.Equal(CleanupResult.Completed, cleaned.Result);
        }
        Assert.Equal(2, record.Results.Count);
        Assert.Equal("done\n", f.ResultFile(A, "result.txt"));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(f.Evidence, "successor.txt")));
    }
}
