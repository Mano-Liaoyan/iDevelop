using System.Text;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class TaskOwnershipTests
{
    [Theory]
    [InlineData("e1-run/events.jsonl", 2, 1)]
    [InlineData("e2-run/events.jsonl", 7, 2)]
    public void An_unclosed_attempt_in_any_schema_owns_its_task_without_taking_the_write_lock(string fixture, int lines, int schema)
    {
        using var f = new RunFixtures();
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", File.ReadLines(Fixture.Path(fixture)).Take(lines)) + "\n");
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        Assert.Equal(schema, f.Read().Schema);
        using var write = new FileStream(Path.Combine(f.Project, ".idp", "runs", "write.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var owned = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, T));
        using var free = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, C));

        Assert.Equal(new TaskRunOwnership.Owned("Workflow"), f.Store.TaskOwnership(owned));
        Assert.Equal(new TaskRunOwnership.Free(), f.Store.TaskOwnership(free));
        Assert.Equal(bytes, File.ReadAllBytes(f.Journal(W, Run)));
    }

    [Fact]
    public void Ownership_reads_every_workflows_journals_and_uses_an_incomplete_tails_prefix()
    {
        using var f = new RunFixtures();
        f.Approve();
        var otherWorkflow = new WorkflowId(Id(20));
        var other = Edit(Workflow.Empty(otherWorkflow), new WorkflowEdit.Rename("Delivery"));
        other = Edit(other, TestNodes.Place(Task(U), new(0, 0)));
        Assert.IsType<RunDecision.Created>(f.Store.Approve(otherWorkflow, OtherRun, f.Op(), Revision.Capture(other), new(Base, BaseChoice.Head)));
        using (var permit = Assert.IsType<ControlTake.Owned>(f.Store.TakeControl(otherWorkflow, OtherRun)).Permit)
        using (var lease = Assert.IsType<LeaseTake.Taken>(permit.TakeTask(U)).Lease)
        {
            var plan = f.Op();
            Assert.IsType<RunDecision.Recorded>(f.Store.Plan(lease, plan, Revision.Capture(other).Id, new AttemptCause.Initial()));
            Assert.IsType<RunDecision.Created>(f.Store.Reserve(lease, f.Op(), plan));
        }
        File.AppendAllText(f.Journal(otherWorkflow, OtherRun), "{\"schema\":3");
        var bytes = File.ReadAllBytes(f.Journal(otherWorkflow, OtherRun));
        using var owned = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, U));
        using var approved = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, T));
        using var free = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, C));

        Assert.Equal(new TaskRunOwnership.Owned("Delivery"), f.Store.TaskOwnership(owned));
        Assert.Equal(new TaskRunOwnership.Owned("Workflow"), f.Store.TaskOwnership(approved));
        Assert.Equal(new TaskRunOwnership.Free(), f.Store.TaskOwnership(free));
        Assert.Equal(bytes, File.ReadAllBytes(f.Journal(otherWorkflow, OtherRun)));
    }

    [Fact]
    public void A_fenced_attempt_in_an_abandoned_run_keeps_ownership_until_recovery_closes_it()
    {
        using var f = new RunFixtures();
        f.Approve();
        f.Claim(f.Reserve());
        f.ReleaseControl();
        Assert.IsType<RunDecision.Recorded>(f.Store.Abandon(f.Permit, f.Op(), f.Op(), "Abandoned."));
        Assert.Equal(new[] { new LaunchKey(A1, 1) }, f.Read().Fenced);
        using (var owned = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, T)))
        using (var free = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, U)))
        {
            Assert.Equal(new TaskRunOwnership.Owned("Workflow"), f.Store.TaskOwnership(owned));
            Assert.Equal(new TaskRunOwnership.Free(), f.Store.TaskOwnership(free));
        }

        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(T), f.Op(), A1, RecoveryOutcome.Stopped, f.Op(), "Stopped."));
        f.Release(T);
        using var released = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, T));
        Assert.Equal(new TaskRunOwnership.Free(), f.Store.TaskOwnership(released));
        Assert.Equal(RecoveryOutcome.Stopped, Assert.IsType<AttemptEnd.Recovered>(f.Read().Closures[A1]).Outcome);
    }

    [Fact]
    public void The_first_owning_workflow_in_ordinal_order_supplies_the_display_name()
    {
        using var f = new RunFixtures(Edit(FixtureWorkflow(), new WorkflowEdit.Rename("First")));
        var otherWorkflow = new WorkflowId(Id(20));
        var other = Edit(Workflow.Empty(otherWorkflow), TestNodes.Place(Task(T), new(0, 0)));
        other = Edit(other, new WorkflowEdit.Rename("Second"));
        Assert.IsType<RunDecision.Created>(f.Store.Approve(otherWorkflow, OtherRun, f.Op(), Revision.Capture(other), new(Base, BaseChoice.Head)));
        f.Approve();
        using var held = Assert.IsType<StandaloneLease>(StandaloneLease.TryTake(f.Project, T));

        Assert.Equal(new TaskRunOwnership.Owned("First"), f.Store.TaskOwnership(held));
    }
}
