using IDevelop.Execution;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class OwnershipFenceTests
{
    [Fact]
    public async System.Threading.Tasks.Task Fence_suspends_a_lost_writers_ancestry_permission_for_sibling_publication()
    {
        foreach (var fence in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
            var t = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(t.Execution.Location.Owner.Task), f.Op(),
                t.Execution.Launch, f.Read().Inputs[t.Execution.Inputs], t.Execution.PromptHash));
            f.Git.Write("foreign.txt", "foreign\n", t.Checkout);
            Assert.Equal(0, f.Git.Run(t.Checkout, "add", "foreign.txt").ExitCode);
            Assert.Equal(0, f.Git.Run(t.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "foreign").ExitCode);
            var foreign = f.Git.Run(t.Checkout, "rev-parse", "HEAD").Text.Trim();
            var u = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
            f.Close(u);
            if (fence)
            {
                f.ReleaseControl();
                Assert.True(f.Permit.Held);
                Assert.Equal(new[] { new LaunchKey(A1, 1) }, f.Read().Fenced);
            }
            var published = f.Materializer().Publish(f.Lease(u.Execution.Location.Owner.Task), f.Op(), u.Execution.Launch.Attempt);
            if (fence)
            {
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Publication.Blocked>(published).Block.Problem);
                Assert.Empty(f.Read().Results);
            }
            else
            {
                Assert.Equal(U, Assert.IsType<Publication.Accepted>(published).Result.Task);
                Assert.Equal(U, Assert.Single(f.Read().Results).Task);
            }
            Assert.Equal(foreign, f.Git.Run(t.Checkout, "rev-parse", t.Execution.Location.Owner.Branch).Text.Trim());
            Assert.Equal("foreign\n", f.Git.Run(t.Checkout, "show", t.Execution.Location.Owner.Branch + ":foreign.txt").Text);
            Assert.Equal("foreign\n", File.ReadAllText(Path.Combine(t.Checkout, "foreign.txt")));
        }
    }
}
