using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>How a run-owned review reads its subject's attempts, on hand-built records (E3c.2).</summary>
public sealed class RunReviewsTests
{
    private static readonly AttemptId Fix = A1;

    private static RunRecord Record(AttemptEnd? end = null, bool published = false) =>
        new RunRecord(Run, W, new(Base, BaseChoice.Head), Revision.Capture(FixtureWorkflow()))
        {
            Closures = end is null ? [] : new Dictionary<AttemptId, AttemptEnd> { [Fix] = end }.ToImmutableDictionary(),
            Results = published ? [new(new(Guid.NewGuid()), T, default, default, new ResultOrigin.Executed(Fix), "Fixed.\n", null)] : [],
        };

    private static AttemptRecord Log(AttemptStatus status, string? session = "session-1") =>
        AttemptReducer.Start(new AttemptEvent.Requested(At, Fix, T, "A", new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol" }, "Fix.", "codex", []))
            with { Status = status, SessionId = session };

    private static AttemptEnd Logged(TerminalAttemptOutcome outcome) => new AttemptEnd.Logged(outcome, new(1, Prompt));

    [Fact]
    public void A_fix_reads_as_its_closure_and_a_success_counts_once_published()
    {
        Assert.Equal(AttemptStatus.Succeeded, RunReviews.Effective(Record(Logged(TerminalAttemptOutcome.Succeeded), published: true), Log(AttemptStatus.Succeeded)).Status);
        Assert.Equal(AttemptStatus.Running, RunReviews.Effective(Record(Logged(TerminalAttemptOutcome.Succeeded)), Log(AttemptStatus.Succeeded)).Status);
        Assert.Equal(AttemptStatus.Failed, RunReviews.Effective(Record(Logged(TerminalAttemptOutcome.Failed)), Log(AttemptStatus.Failed)).Status);
        Assert.Equal(AttemptStatus.Cancelled, RunReviews.Effective(Record(Logged(TerminalAttemptOutcome.Cancelled)), Log(AttemptStatus.Cancelled)).Status);
        Assert.Equal(AttemptStatus.Interrupted, RunReviews.Effective(Record(Logged(TerminalAttemptOutcome.Interrupted)), Log(AttemptStatus.Interrupted)).Status);
        foreach (var outcome in new[] { RecoveryOutcome.Stopped, RecoveryOutcome.NotStarted })
            Assert.Equal(AttemptStatus.Interrupted, RunReviews.Effective(Record(new AttemptEnd.Recovered(outcome, default, "Recovered.")), Log(AttemptStatus.Running)).Status);
        // An attempt that is not closed yet still settles, whatever its log says, unless it rests for the person.
        Assert.Equal(AttemptStatus.Running, RunReviews.Effective(Record(), Log(AttemptStatus.Interrupted)).Status);
        Assert.Equal(AttemptStatus.Running, RunReviews.Effective(Record(), Log(AttemptStatus.Succeeded)).Status);
        Assert.Equal(AttemptStatus.WaitingForInput, RunReviews.Effective(Record(), Log(AttemptStatus.WaitingForInput)).Status);
    }

    [Fact]
    public void Continue_fix_needs_a_started_client_and_a_session()
    {
        Assert.Null(RunReviews.ContinueUnavailable(Record(Logged(TerminalAttemptOutcome.Interrupted)), Log(AttemptStatus.Interrupted)));
        Assert.Null(RunReviews.ContinueUnavailable(Record(new AttemptEnd.Recovered(RecoveryOutcome.Stopped, default, "Stopped.")), Log(AttemptStatus.Running)));
        Assert.Equal(RunReviews.NotStartedFix, RunReviews.ContinueUnavailable(
            Record(new AttemptEnd.Recovered(RecoveryOutcome.NotStarted, default, "Not started.")), Log(AttemptStatus.Running)));
        Assert.Equal(RunReviews.NoFixSession, RunReviews.ContinueUnavailable(Record(Logged(TerminalAttemptOutcome.Interrupted)),
            Log(AttemptStatus.Interrupted, session: null)));
    }
}
