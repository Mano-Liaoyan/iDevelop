using IDevelop.Execution;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class OwnershipFenceTests
{
    [Fact]
    public async System.Threading.Tasks.Task A_prepared_but_never_claimed_branch_does_not_excuse_a_foreign_commit_after_takeover()
    {
        foreach (var observed in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
            var t = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            if (observed) RootExitTests.Claim(f, t);
            f.Git.Write("foreign.txt", "foreign\n", t.Checkout);
            Assert.Equal(0, f.Git.Run(t.Checkout, "add", "foreign.txt").ExitCode);
            Assert.Equal(0, f.Git.Run(t.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "foreign").ExitCode);
            if (observed)
            {
                Assert.Equal(TipOwnership.Explained, Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(
                    f.Lease(T), f.Op(), t.Execution.Launch, new RootExit.Exited(0))).Observation.Ownership);
                Assert.Equal("RecoveryEvidenceInsufficient", Assert.IsType<Settlement.Rejected>(await f.Materializer().RecoverSettlement(
                    f.Lease(T), f.Op(), t.Execution.Launch)).Reason.Problem.ToString());
                Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), t.Execution.Launch.Attempt,
                    RecoveryOutcome.Stopped, f.Op(), "Root stopped."));
            }
            var u = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
            await f.Close(u, assertMatched: observed);
            f.ReleaseControl();
            Assert.True(f.Permit.Held);
            Assert.Empty(f.Read().Fenced);
            var publication = f.Materializer().Accept(f.Lease(U), f.Op(), u.Execution.Launch.Attempt);
            if (observed)
            {
                Assert.Equal(U, Assert.IsType<Acceptance.Accepted>(publication).Result.Task);
                Assert.Equal(U, Assert.Single(f.Read().Results).Task);
            }
            else
            {
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Acceptance.Blocked>(publication).Block.Problem);
                Assert.Empty(f.Read().Results);
            }
            Assert.Equal("foreign\n", File.ReadAllText(Path.Combine(t.Checkout, "foreign.txt")));
        }
    }

}
