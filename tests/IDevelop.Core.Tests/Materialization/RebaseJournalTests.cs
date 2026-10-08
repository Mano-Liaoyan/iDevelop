using IDevelop.Execution;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.RebaseTests;

namespace IDevelop.Core.Tests.Materialization;

/// <summary>The journal's own checks on a rebase plan and its rebased result, whatever the materializer checked first (E3f).</summary>
public sealed class RebaseJournalTests
{
    private static readonly Digest Command = new(new string('a', 64));

    /// <summary>A stale consumer whose approved rebase stopped at <paramref name="point"/>, and the journal before the approval.</summary>
    private static async Task<(MaterializationPlan.Rebase Plan, OperationId Id, RunRecord Before)> Crash(PreparationFixture f, string point)
    {
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        var before = f.Read();
        Assert.Throws<InvalidOperationException>(() => Rebaser(f, probe: reached =>
        {
            if (reached == point) throw new InvalidOperationException("Crashed.");
        }).Rebase(f.Lease(U), f.Op(), preview.Identity));
        var pair = f.Read().Plans.Single(pair => pair.Value is MaterializationPlan.Rebase);
        return ((MaterializationPlan.Rebase)pair.Value, pair.Key, before);
    }

    private static RunRead Apply(PreparationFixture f, RunRecord record, OperationId operation, RunEvent e) =>
        RunReducer.Apply(W, f.RunId, record, new RunEntry(record.Schema, record.Sequence + 1, operation, Command, At, e));

    private static RunProblem Problem(RunRead read) => Assert.IsType<RunRead.Rejected>(read).Reason.Problem;

    [Fact]
    public async Task A_rebase_plan_must_replace_the_current_stale_result_on_fresh_inputs_of_a_closed_task()
    {
        using var f = new PreparationFixture(Chain());
        var (plan, id, before) = await Crash(f, "journal.rebase-plan.after");
        RunRead Plan(MaterializationPlan.Rebase candidate, RunRecord? record = null, OperationId? operation = null) =>
            Apply(f, record ?? before, operation ?? id, new RunEvent.Planned(candidate));
        var planned = Assert.IsType<RunRead.Loaded>(Plan(plan)).Record;
        var stale = before.CurrentResults[U];
        var old = before.Inputs[stale.Inputs] with { Id = new(Guid.Parse("00000000-0000-0000-0000-00000000f001")) };
        Assert.Equal(RunProblem.InvalidData, Problem(Plan(plan with { Ref = plan.Ref + "x" })));
        Assert.Equal(RunProblem.InvalidData, Problem(Plan(plan with { Recipe = plan.Recipe with { Parents = [plan.From] } })));
        Assert.Equal(RunProblem.UnknownResult, Problem(Plan(plan with { Source = before.CurrentResults[T].Id })));
        Assert.Equal(RunProblem.UnsupportedResult, Problem(Plan(plan with { From = plan.Commit })));
        Assert.Equal(RunProblem.StaleInput, Problem(Plan(plan with { Inputs = old, Recipe = plan.Recipe with { Parents = [old.CodeBase] } })));
        Assert.Equal(RunProblem.InputConflict, Problem(Plan(plan with { Inputs = plan.Inputs with { Text = "changed" } })));
        Assert.Equal(RunProblem.InputConflict, Problem(Plan(plan with { Inputs = plan.Inputs with { Id = stale.Inputs } })));
        Assert.Equal(RunProblem.InputConflict, Problem(Plan(plan with { JoinRecipe = plan.Recipe })));
        Assert.Equal(RunProblem.RunStopped, Problem(Plan(plan, before with { Phase = RunPhase.StopRequested })));
        var open = new AttemptId(Guid.Parse("00000000-0000-0000-0000-00000000f002"));
        Assert.Equal(RunProblem.UnclosedAttempts, Problem(Plan(plan, before with
        {
            Attempts = before.Attempts.Add(open, new(open, U, stale.Revision, stale.Inputs, new AttemptCause.Initial())),
        })));
        Assert.Equal(RunProblem.InvalidData, Problem(Plan(plan, before with { Schema = 2 })));
        var taken = before.Results[0].Id;
        Assert.Equal(RunProblem.InvalidData, Problem(Plan(plan with { Result = taken, Ref = RunLayout.RebaseRef(before.RunKey!, before.TaskKeys[U], taken) })));
        // Without the update, the consumer's result is current but not stale.
        var update = before.CurrentResults[T];
        var unchanged = before with { Results = before.Results.Remove(update) };
        var rebuilt = InputMaterial.Build(unchanged, old.Id, U, old.Revision, old.Bindings, InputMaterial.Sources(unchanged, old.Bindings), null);
        Assert.Equal(RunProblem.UnknownResult, Problem(Plan(plan with { Inputs = rebuilt, Recipe = plan.Recipe with { Parents = [rebuilt.CodeBase] } }, unchanged)));
        var amended = new RevisionId(new string('b', 64));
        Assert.Equal(RunProblem.InputConflict, Problem(Plan(plan with { Inputs = plan.Inputs with { Revision = amended } },
            before with { Revisions = before.Revisions.Add(amended, before.Revision with { Id = amended }) })));
        var second = new ResultId(Guid.Parse("00000000-0000-0000-0000-00000000f003"));
        Assert.Equal(RunProblem.ReplacementConflict, Problem(Plan(plan with { Result = second, Ref = RunLayout.RebaseRef(before.RunKey!, before.TaskKeys[U], second) },
            planned, f.Op())));
    }

    [Theory]
    [InlineData("git.rebase-retain.after", false)]
    [InlineData("git.rebase-branch.after", false)]
    [InlineData("journal.rebase-reset-intent.after", false)]
    [InlineData("journal.rebase-accepted.before", true)]
    public async Task A_rebased_result_needs_its_plan_and_every_observed_move(string point, bool complete)
    {
        using var f = new PreparationFixture(Chain());
        var (plan, id, _) = await Crash(f, point);
        var record = f.Read();
        var result = RunReducer.RebaseResult(record, id, plan);
        RunRead Accept(ResultRecord accepted, InputRecord? inputs = null) =>
            Apply(f, record, f.Op(), new RunEvent.ResultAccepted(accepted, inputs ?? plan.Inputs));
        if (!complete)
        {
            Assert.Equal(RunProblem.InputConflict, Problem(Accept(result)));
            return;
        }
        Assert.IsType<RunRead.Loaded>(Accept(result));
        Assert.Equal(RunProblem.InputConflict, Problem(Accept(result with { Report = "changed" })));
        Assert.Equal(RunProblem.InputConflict, Problem(Accept(result, plan.Inputs with { Text = "changed" })));
        Assert.Equal(RunProblem.UnknownResult, Problem(Accept(result with { Origin = new ResultOrigin.Rebased(plan.Source, f.Op()) })));
        Assert.Equal(RunProblem.UnknownResult, Problem(Apply(f, record with { Schema = 2 }, f.Op(), new RunEvent.ResultAccepted(result, plan.Inputs))));
        Assert.Equal(RunProblem.InvalidData, Problem(Accept(result with { Origin = new ResultOrigin.Rebased(record.CurrentResults[T].Id, id) })));
    }
}
