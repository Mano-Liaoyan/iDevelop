using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class FencedWriterRetryTests
{
    [Theory]
    [InlineData(false, "Reset")]
    [InlineData(true, "Blocked:UncertainOwnership")]
    public async System.Threading.Tasks.Task A_fenced_writer_with_its_own_commit_is_retained_but_retry_reset_stays_blocked(bool fence, string expected)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), ready.Execution.Launch,
            f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));
        f.Git.Write("b.txt", "B\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "b").ExitCode);
        if (fence)
        {
            f.ReleaseControl();
            _ = f.Permit;
            Assert.Equal(new[] { ready.Execution.Launch }, f.Read().Fenced);
        }
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("B\n", f.Git.Git("show", retained.Commit.Hex + ":b.txt"));
        var reset = f.Materializer().ResetForRetry(f.Lease(T), f.Op(), retained.Receipt.Plan, f.Op());
        Assert.Equal(expected, reset switch
        {
            RetryReset.Reset => "Reset",
            RetryReset.Blocked blocked => "Blocked:" + blocked.Block.Problem,
            RetryReset.Rejected rejected => "Rejected:" + rejected.Reason.Problem,
            _ => reset.GetType().Name,
        });
    }

}
