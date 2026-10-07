using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class ReviewLineageTests
{
    [Fact]
    public void A_failed_round_two_fix_has_one_replacement_and_retains_its_original_review_link()
    {
        var writerBlueprint = new Blueprint(new("example.writer", 1), "Writer", new WorkSpec.Agent(AgentAccess.Edit, false,
            PromptTemplate.Parse("{{brief}}")),
            [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(Task().Execution, ConversationMode.Autonomous));
        var reviewBlueprint = new Blueprint(new("example.review", 1), "Review", new WorkSpec.Review(PromptTemplate.Parse("{{brief}}"),
            PromptTemplate.Parse("{{brief}}")),
            [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(Task().Execution, ConversationMode.Autonomous));
        var workflow = Connect(FixtureWorkflow(new TaskDefinition(T, writerBlueprint) { Title = "Plan" }, new TaskDefinition(U,
            reviewBlueprint) { Title = "Review" }), T, U);
        var revision = Revision.Capture(workflow);
        var reviewer = new AttemptId(Id(200));
        var first = new AttemptId(Id(201));
        var second = new AttemptId(Id(202));
        var reviewInputs = new InputRecord(new(Id(300)), U, revision.Id, [new InputBinding.Provided(new(T, U), ConnectionKind.Dependency,
            new(Id(400)))], new CodeSelection.Legacy(Base), "", [], null);
        var writerInputs = new InputRecord(new(Id(301)), T, revision.Id, [], new CodeSelection.Legacy(Base), "", [], null);
        var initial = new RunAttempt(new(Id(199)), T, revision.Id, writerInputs.Id, new AttemptCause.Initial());
        var checkpoint = new LogCheckpoint(100, Prompt);
        var link = new ReviewLink(U, reviewer, 2, 0);
        RunEvent[] events =
        [
            new RunEvent.Approved(Run, revision, new(Base, BaseChoice.Head)),
            new RunEvent.Reserved(initial, writerInputs),
            new RunEvent.AttemptClosed(initial.Id, new AttemptEnd.Logged(TerminalAttemptOutcome.Succeeded, checkpoint)),
        ];
        var record = Assert.IsType<RunRead.Loaded>(RunReducer.Replay(W, Run, events.Select((e, i) => new RunEntry(1, i + 1,
            new(Id(500 + i)), Prompt, At, e)))).Record;
        var reviewAttempt = new RunAttempt(reviewer, U, revision.Id, reviewInputs.Id, new AttemptCause.Initial());
        record = record with
        {
            Attempts = record.Attempts.Add(reviewer, reviewAttempt),
            Inputs = record.Inputs.Add(reviewInputs.Id, reviewInputs),
            Claims = record.Claims.Add(new(reviewer, 2), new(new(reviewer, 2), reviewInputs, Prompt)),
            TurnClosures = record.TurnClosures.Add(new(reviewer, 2), checkpoint),
        };
        RunRead Apply(RunRecord current, RunEvent e, int operation) => RunReducer.Apply(W, Run, current,
            new(1, current.Sequence + 1, new(Id(operation)), Prompt, At, e));
        var fixInputs = writerInputs with
        {
            Id = new(Id(302))
        };
        var fix = new RunAttempt(first, T, revision.Id, fixInputs.Id, new AttemptCause.ReviewFix(link));
        record = Assert.IsType<RunRead.Loaded>(Apply(record, new RunEvent.Reserved(fix, fixInputs), 510)).Record;
        record = Assert.IsType<RunRead.Loaded>(Apply(record, new RunEvent.AttemptClosed(first,
            new AttemptEnd.Logged(TerminalAttemptOutcome.Failed, checkpoint)), 511)).Record;
        var retryInputs = writerInputs with
        {
            Id = new(Id(303))
        };
        var replacement = new RunAttempt(second, T, revision.Id, retryInputs.Id, new AttemptCause.Retry(first, new(Id(600))));
        record = Assert.IsType<RunRead.Loaded>(Apply(record, new RunEvent.Reserved(replacement, retryInputs), 512)).Record;
        Assert.Equal(new AttemptId(Id(201)), Assert.IsType<AttemptCause.Retry>(record.Attempts[second].Cause).Previous);
        Assert.Equal(2, record.ReviewOf(second)!.Round);
        Assert.Equal(1, record.Attempts.Values.Count(attempt => attempt.Cause is AttemptCause.Retry retry && retry.Previous == first));
        Assert.Equal(new RunRejection(RunProblem.StartConflict, 7), Assert.IsType<RunRead.Rejected>(Apply(record,
            new RunEvent.Reserved(fix with
            {
                Id = new(Id(204)),
                Cause = new AttemptCause.ReviewFix(link with
                {
                    Guidance = 1
                })
            }, fixInputs), 514)).Reason);
        Assert.Equal(new RunRejection(RunProblem.ReplacementConflict, 7), Assert.IsType<RunRead.Rejected>(Apply(record,
            new RunEvent.Reserved(replacement with
            {
                Id = new(Id(203))
            }, retryInputs), 513)).Reason);
    }
}
