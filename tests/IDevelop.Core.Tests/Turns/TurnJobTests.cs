using System.Diagnostics;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Turns.TurnFixture;

namespace IDevelop.Core.Tests.Turns;

[Collection(EnvironmentCollection.Name)]
public sealed class TurnJobTests
{
    [UnixFact]
    public Task Workflow_cleanup_is_recorded_and_never_gates_acceptance() => CleanupAcceptance("Incomplete", "No process group contains this turn's descendants.", "killTree");

    [WindowsFact]
    public Task Workflow_job_cleanup_is_recorded_and_never_gates_acceptance() => CleanupAcceptance("Completed", null, "terminateJob");

    [WindowsFact]
    public Task Cleanup_failure_is_diagnostic_when_termination_fails() => CleanupAcceptance("Incomplete", "Win32 error 5", "terminateJob", "termination");

    [WindowsFact]
    public Task Cleanup_failure_is_diagnostic_when_the_job_is_missing() => CleanupAcceptance("Incomplete", "The client could not join a job object.", "killTree", "assignment");

    private static async Task CleanupAcceptance(string result, string? detail, string action, string? failure = null)
    {
        var consumer = PreparationFixture.Writer(U) with { Execution = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" } };
        await using var f = new TurnFixture(configure: workflow => Connect(Edit(workflow, TestNodes.Place(consumer, new(0, 0))), T, U));
        using var children = new Processes();
        var pidFile = Path.Combine(f.Evidence, "sleeper.pid");
        await f.Open(FakeAgents.Fresh(ClientId.Codex).SpawnSleepingChild(pidFile)
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
            .Write("result.txt", "done\n").Print(FakeAgents.ReplyLines(ClientId.Codex, "Done.")));
        SettledTurn turn;
        using var barrier = new ProbeBarrier("runner.cleanup.inside");
        f.Runs.Probe = barrier.Probe;
        if (failure == "assignment") ProcessJob.AssignmentFailure = true;
        try
        {
            var running = await f.Start();
            var pid = await children.PidAsync(pidFile).WaitAsync(Bound);
            await barrier.Reached.Task.WaitAsync(Bound);
            await running.RootExited.WaitAsync(Bound);
            if (failure == "termination") ProcessJob.TerminationFailure = 5;
            barrier.Dispose();
            turn = await f.Settled(running);
            if (result == "Completed" || failure == "termination")
            {
                await WaitUntilAsync(() => Gone(pid));
                Assert.True(Gone(pid));
            }
            else Assert.False(Gone(pid));
        }
        finally
        {
            ProcessJob.AssignmentFailure = false;
            ProcessJob.TerminationFailure = null;
            barrier.Dispose();
        }
        var cleanup = Assert.IsType<AttemptEvent.CleanedUp>(turn.Cleanup);
        Assert.Equal(result, cleanup.Result.ToString());
        Assert.Equal(detail, cleanup.Detail);
        Assert.Equal(new[] { action }, cleanup.Steps.Select(step => step.Action));
        if (failure == "termination") Assert.Equal("Win32 error 5", Assert.Single(cleanup.Steps).Failure);
        if (result == "Completed") Assert.Null(Assert.Single(cleanup.Steps).Failure);
        Assert.IsType<CaptureDisposition.Matched>(turn.Capture.Disposition);
        var launched = Assert.Single(f.Log(turn.Address.Launch).Events.OfType<AttemptEvent.Launched>());
        if (failure == "assignment")
            Assert.Equal("The client could not join a job object.", Assert.IsType<Containment.None>(launched.Containment).Reason);
        else if (OperatingSystem.IsWindows()) Assert.IsType<Containment.Job>(launched.Containment);
        else Assert.Equal("No process group contains this turn's descendants.", Assert.IsType<Containment.None>(launched.Containment).Reason);
        var events = f.Log(turn.Address.Launch).Events;
        Assert.IsType<AttemptEvent.CleanedUp>(events[^2]);
        Assert.IsType<AttemptEvent.Exited>(events[^1]);
        var published = f.Publish(turn);
        Assert.Single(f.Preparation.Read().Results);
        Assert.Equal("done\n", f.Preparation.Git.Git("show", Assert.IsType<CodeOutput.Produced>(published.Code).Code.Commit.Hex + ":result.txt"));
        var ready = Assert.IsType<Preparation.Ready>(await f.Preparation.Prepare(U));
        Assert.EndsWith(".worktrees/93f23689/c67f2fc3", ready.Checkout.Replace('\\', '/'));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(1, f.Launches);
        children.Dispose();
    }

    private static bool Gone(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
