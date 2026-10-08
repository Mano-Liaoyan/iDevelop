using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class AuthorityScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task Materialization_pins_authority_until_prepare_or_publish_returns(bool publish)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var lease = f.Lease(T);
        var permit = f.Permit;
        Preparation.Ready? ready = null;
        if (publish)
        {
            ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            await f.Close(ready);
        }
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var materializer = f.Materializer(probe: step =>
        {
            if (step != (publish ? "journal.plan.before" : "journal.reserve.before")) return;
            entered.Release();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        });
        var operation = f.Op();
        var work = System.Threading.Tasks.Task.Run(async () => publish
            ? (object)materializer.Publish(lease, operation, ready!.Execution.Launch.Attempt)
            : await materializer.Prepare(lease, operation, new AttemptCause.Initial()));
        await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        object? result = null;
        try
        {
            f.ReleaseControl();
            Assert.True(permit.Held);
            Assert.True(lease.Held);
            Assert.Null(lease.Use());
            Assert.IsType<LeaseTake.Busy>(permit.TakeTask(U));
            using var contender = Control(f);
            Assert.Equal("Busy", await contender.Line());
            await contender.Exit();
        }
        finally
        {
            release.Release();
            result = await work.WaitAsync(TimeSpan.FromSeconds(30));
        }
        if (publish) Assert.Equal(T, Assert.IsType<Publication.Accepted>(result).Result.Task);
        else Assert.Equal(A1, Assert.IsType<Preparation.Ready>(result).Execution.Launch.Attempt);
        Assert.False(permit.Held);
        Assert.False(lease.Held);
        using var successor = Control(f);
        Assert.Equal("Owned:", await successor.Line());
        await successor.Exit();
        Assert.Empty(f.Read().Fenced);
    }

    private static Racer Control(PreparationFixture f) => new("control", f.Git.Folder,
        W.Value.ToString("D"), f.RunId.Value.ToString("D"), "-", "-");
}
