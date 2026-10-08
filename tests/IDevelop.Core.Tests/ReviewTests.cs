using System.Diagnostics;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests;

/// <summary>
/// The review loop through scripted replies: a Codex implementer and a Claude Code reviewer behind on-disk shims, in a
/// Git project, so each turn's tree snapshot is real.
/// </summary>
public sealed class ReviewTests : IDisposable
{
    private const string ImplementerSession = "019a9d2e-1111-7000-8000-000000000001";
    private const string ReviewerSession = "019a9d2e-2222-7000-8000-000000000002";
    private const string Buggy = "def add(a, b):\n    return a - b\n";
    private const string Fixed = "def add(a, b):\n    return a + b\n";
    private static readonly TaskId Subject = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;
    private readonly string _project;
    private readonly string _implementer;
    private readonly string _reviewer;
    private readonly string _gate;

    public ReviewTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _project = _temp.Create("project");
        _implementer = _temp.Create("implementer");
        _reviewer = _temp.Create("reviewer");
        _gate = Path.Combine(_temp.Create("gate"), "go");
        Git("init", "-q");
        Install(_fakes, ClientId.Codex,
            Resuming(ClientId.Codex, ImplementerSession).Scripted(_implementer),
            Fresh(ClientId.Codex).Scripted(_implementer));
        Install(_fakes, ClientId.ClaudeCode,
            Resuming(ClientId.ClaudeCode, ReviewerSession).Scripted(_reviewer),
            Fresh(ClientId.ClaudeCode).Scripted(_reviewer));
        Implementer(1, Buggy, "Wrote calc.py with add.");
    }

    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _temp.Dispose();
    }

    private static TaskDefinition SubjectNode => TestNodes.Implement(
        Subject, "Add numbers", "Write calc.py with a function add(a, b) that returns their sum.",
        execution: new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" });

    private static TaskDefinition ReviewNode => new TaskDefinition(Review, BuiltInBlueprints.Review)
    {
        Title = "Review add",
        Execution = new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" },
    }.WithField("focus", "Check that add is correct.")!;

    private static Workflow Workflow => Workflow.Empty(WorkflowId.New())
        .Must(TestNodes.Place(SubjectNode, new CanvasPoint(0, 0)))
        .Must(TestNodes.Place(ReviewNode, new CanvasPoint(300, 0)))
        .Must(new WorkflowEdit.Connect(new ConnectionKey(Subject, Review), ConnectionKind.Dependency));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_subject_session_is_invalidated_when_the_first_reviewer_turn_fails_or_is_cancelled(bool cancel)
    {
        WriteTurn(_reviewer, 1, FakeRule.On().Print(SessionLine(ClientId.ClaudeCode, ReviewerSession))
            .WaitForFile(_gate).Print("""{"type":"result","subtype":"error_during_execution","is_error":true}"""));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.Follow(Workflow);
        Assert.IsType<StartResult.Started>(runs.Start(SubjectNode));
        await Until(() => runs.Latest[Subject].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the subject succeeds");
        using var session = runs.OpenConversation(Subject);
        Assert.IsType<StartResult.Started>(runs.Start(ReviewNode));
        await Until(() => runs.Latest[Review].SessionId == ReviewerSession, "the reviewer starts");
        Assert.False(session.Snapshot.Actions.Send.Enabled);
        var released = new TaskCompletionSource<ActionAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += _ =>
        {
            var availability = session.Snapshot.Actions.Send;
            if (availability.Enabled)
            {
                released.TrySetResult(availability);
            }
        };
        if (cancel)
        {
            Assert.Null(await runs.CancelAsync(Review));
        }
        else
        {
            File.WriteAllText(_gate, "go");
        }

        await Until(() => runs.Latest[Review].Status == (cancel ? AttemptStatus.Cancelled : AttemptStatus.Failed) && runs.Active.IsEmpty, "the review settles");
        Assert.Equal(new ActionAvailability(true, "Send a message."), session.Snapshot.Actions.Send);
        Assert.Equal(new ActionAvailability(true, "Send a message."), await released.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_review_that_approves_in_the_first_round_reads_the_ticket_the_report_and_the_change_and_ends()
    {
        Reviewer(1, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""));

        var (review, runs) = await RunLoop();
        await using (runs)
        {
            Assert.Equal((AttemptStatus.Succeeded, 1, 1), (review.Status, review.Turns.Count, ReviewLedger.Fold(review).Round));
            Assert.Equal(Subject, review.Subject);
            var prompt = Prompt(_reviewer, 1);
            Assert.Contains("## What to check\n\nCheck that add is correct.", prompt);
            Assert.Contains("## The ticket\n\n# Add numbers\n\nWrite calc.py with a function add(a, b) that returns their sum.", prompt);
            Assert.Contains("## The implementer's report\n\nWrote calc.py with add.", prompt);
            Assert.Contains("+    return a - b", prompt);
            Assert.EndsWith(ReviewWork.VerdictContract + "\n", prompt);
            Assert.Equal("1", File.ReadAllText(Path.Combine(_implementer, "count")));
            Assert.True(review.ReadOnly);
        }
    }

    [Fact]
    public async Task A_review_that_agrees_in_three_rounds_resumes_both_sessions_and_passes_guidance_to_both_agents_once()
    {
        Reviewer(1, Verdict("""
            {"status": "verdict", "verdict": "changes", "findings": [
              {"id": "1", "text": "add subtracts.", "change": "Return a + b."},
              {"id": "2", "text": "add has no docstring.", "change": "Add a one-line docstring."}]}
            """));
        Implementer(2, Fixed, Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}, {"id": "2", "answer": "fixed", "note": "Done."}]"""), waitForGate: true);
        Reviewer(2, Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "2", "text": "There is still no docstring.", "change": "Add \"\"\"Return a + b.\"\"\" under the def line."}]}"""));
        Implementer(3, "def add(a, b):\n    \"\"\"Return a + b.\"\"\"\n    return a + b\n", Answers("""[{"id": "2", "answer": "fixed", "note": "Added it."}]"""));
        Reviewer(3, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""));

        var (review, runs) = await RunLoop(async runs =>
        {
            await Until(() => File.Exists(Path.Combine(_implementer, "2.stdin")), "fix round 1 starts");
            Assert.IsType<SendResult.Guided>(await runs.SendAsync(ReviewNode, "Keep add on two lines.", stopTurn: false));
            File.WriteAllText(_gate, "");
        });
        await using (runs)
        {
            Assert.Equal((AttemptStatus.Succeeded, 3), (review.Status, review.Turns.Count));
            Assert.All(new[] { 2, 3 }, turn => Assert.True(Has(Arguments(_reviewer, turn), "--resume", ReviewerSession), $"reviewer turn {turn} resumes"));
            Assert.All(new[] { 2, 3 }, turn => Assert.Equal(("thread/resume", ImplementerSession), Thread(_implementer, turn)));

            var fixes = runs.EarlierAttempts(runs.Latest[Subject]).Add(runs.Latest[Subject]);
            Assert.Equal([null, 1, 2], fixes.Select(attempt => attempt.Fix?.Round));
            Assert.All(fixes.Skip(1), fix => Assert.Equal(review.Id, fix.Fix!.Attempt));
            Assert.Equal([null, fixes[0].Id, fixes[1].Id], fixes.Select(attempt => attempt.Continues));

            Assert.Contains("### Finding 1\n\nadd subtracts.\n\nThe change that would settle it: Return a + b.", Prompt(_implementer, 2));
            Assert.DoesNotContain("Keep add on two lines.", Prompt(_implementer, 2));
            Assert.Contains("## Guidance from the person\n\nKeep add on two lines.", Prompt(_reviewer, 2));
            Assert.Contains("-    return a - b\n+    return a + b", Prompt(_reviewer, 2));
            Assert.Contains("Your earlier answer: Done.", Prompt(_implementer, 3));
            Assert.Contains("## Guidance from the person\n\nKeep add on two lines.", Prompt(_implementer, 3));
            Assert.DoesNotContain("Keep add on two lines.", Prompt(_reviewer, 3));

            var ledger = ReviewLedger.Fold(review);
            Assert.Equal(
                [("1", FindingState.Resolved, "It adds now."), ("2", FindingState.Resolved, "Added it.")],
                ledger.Findings.Select(finding => (finding.Id, finding.State, finding.Answer)));
            Assert.Equal(["Keep add on two lines."], review.Guidance.Select(note => note.Text));
        }
    }

    [Fact]
    public async Task A_disputed_finding_that_the_reviewer_withdraws_settles_the_review()
    {
        Reviewer(1, Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add should round.", "change": "Return round(a + b)."}]}"""));
        Implementer(2, null, Answers("""[{"id": "1", "answer": "disputed", "note": "The ticket asks for the exact sum."}]"""));
        Reviewer(2, Verdict("""{"status": "verdict", "verdict": "approve", "findings": [], "withdrawn": [{"id": "1", "reason": "The ticket wants the exact sum."}]}"""));

        var (review, runs) = await RunLoop();
        await using (runs)
        {
            Assert.Equal((AttemptStatus.Succeeded, 2), (review.Status, review.Turns.Count));
            Assert.Contains("\"answer\": \"disputed\"", Prompt(_reviewer, 2));
            Assert.Contains("## The change in this round\n\nNo file changed.", Prompt(_reviewer, 2));
            var finding = Assert.Single(ReviewLedger.Fold(review).Findings);
            Assert.Equal((FindingState.Withdrawn, "The ticket wants the exact sum.", "The ticket asks for the exact sum."), (finding.State, finding.Text, finding.Answer));
        }
    }

    [Fact]
    public async Task A_round_that_repeats_an_earlier_round_is_named_to_both_agents()
    {
        const string Stands = """{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}""";
        Reviewer(1, Verdict(Stands));
        Implementer(2, null, Answers("""[{"id": "1", "answer": "fixed", "note": "Fixed."}]"""));
        Reviewer(2, Verdict(Stands));
        Implementer(3, null, Answers("""[{"id": "1", "answer": "disputed", "note": "It is right."}]"""));
        Reviewer(3, Verdict(Stands));
        Implementer(4, null, Answers("""[{"id": "1", "answer": "fixed", "note": "Fixed again."}]"""));
        Reviewer(4, Verdict(Stands));
        Implementer(5, Fixed, Answers("""[{"id": "1", "answer": "fixed", "note": "Returns a + b."}]"""));
        Reviewer(5, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""));

        var (review, runs) = await RunLoop();
        await using (runs)
        {
            Assert.Equal((AttemptStatus.Succeeded, 5), (review.Status, review.Turns.Count));
            Assert.DoesNotContain("repeats the positions", Prompt(_reviewer, 3));
            Assert.Contains("Round 3 repeats the positions of round 1. For each finding that still stands, name the exact change that would settle it", Prompt(_reviewer, 4));
            Assert.Contains("Round 3 repeated the positions of round 1.", Prompt(_implementer, 5));
            Assert.DoesNotContain("repeated the positions", Prompt(_implementer, 4));
            var ledger = ReviewLedger.Fold(review);
            Assert.Equal((5, 1, (int?)null), (ledger.Round, ledger.Repeats(3), ledger.Repeats(2)));
        }
    }

    [Fact]
    public async Task An_unreadable_verdict_gets_one_repair_turn_and_a_second_one_fails_the_review()
    {
        Reviewer(1, "Looks fine to me.");
        Reviewer(2, Verdict("""{"status": "verdict", "verdict": "changes", "findings": []}"""));

        var (review, runs) = await RunLoop();
        await using (runs)
        {
            Assert.Equal(AttemptStatus.Failed, review.Status);
            Assert.Equal(
                "iDevelop could not read the reviewer's verdict, even after asking again. The verdict asks for changes but lists no finding.",
                review.Detail);
            Assert.StartsWith(
                "iDevelop could not read the verdict at the end of your last message. The message does not end with a verdict block. Reply with only the verdict block.",
                Prompt(_reviewer, 2));
        }
    }

    [Fact]
    public async Task A_reviewer_turn_that_changes_the_project_fails_the_review()
    {
        WriteTurn(_reviewer, 1, FakeRule.On()
            .Print(SessionLine(ClientId.ClaudeCode, ReviewerSession))
            .Write("calc.py", Fixed)
            .Print(ReplyLines(ClientId.ClaudeCode, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""))));

        var (review, runs) = await RunLoop();
        await using (runs)
        {
            Assert.Equal(
                (AttemptStatus.Failed, "The turn changed files in the project, although this node's agent may only read."),
                (review.Status, review.Detail));
        }
    }

    [Fact]
    public async Task Cancelling_a_review_during_a_fix_round_cancels_the_fix_too()
    {
        Reviewer(1, Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}"""));
        WriteTurn(_implementer, 2, FakeRule.On().Print(SessionLine(ClientId.Codex, ImplementerSession)).Hang());

        var (review, runs) = await RunLoop(async runs =>
        {
            await Until(() => runs.Latest.GetValueOrDefault(Subject) is { Fix: not null, SessionId: not null }, "fix round 1 runs");
            Assert.Equal(AttemptStatus.InReview, runs.Latest[Review].Status);
            Assert.Equal(new StartProblem.InReview("Review add"), runs.Check(ReviewNode));
            Assert.Null(await runs.CancelAsync(Review));
        });
        await using (runs)
        {
            Assert.Equal(AttemptStatus.Cancelled, review.Status);
            await Until(() => runs.Latest[Subject].Status == AttemptStatus.Cancelled, "the fix round is cancelled");
        }
    }

    [Fact]
    public async Task A_fix_round_that_quitting_interrupted_waits_for_Continue_fix_and_a_duplicate_choice_runs_it_once()
    {
        var runs = await Interrupted();
        await using (runs)
        {
            Assert.Equal((AttemptStatus.InReview, AttemptStatus.Interrupted), (runs.Latest[Review].Status, runs.Latest[Subject].Status));
            Assert.Equal(new StartProblem.UnderReview("Review add"), runs.Check(SubjectNode));
            Assert.Equal(new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.UnderReview("Review add"))), await runs.SendAsync(SubjectNode, "Go on.", stopTurn: false));
            Assert.Equal(new StartResult.Refused(new StartProblem.NoFixChoice("Review add")), runs.ChooseFix(Review, FixChoice.Continue));
            runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(TestNodes.Implement(TaskId.New(), "Unrelated"), new CanvasPoint(0, 0))));
            Assert.Equal(new StartProblem.UnderReview("Review add"), runs.Check(SubjectNode));

            runs.Follow(Workflow);

            var choice = new StartProblem.FixInterrupted("Add numbers", 1, CanContinue: true);
            await Until(() => runs.Check(ReviewNode) == choice, "the review asks for the person's choice");
            await Task.Delay(300);
            Assert.Equal("2", File.ReadAllText(Path.Combine(_implementer, "count")));
            Assert.Equal(choice, runs.Check(ReviewNode));
            Assert.Equal(AttemptStatus.Interrupted, runs.Latest[Subject].Status);
            var interrupted = runs.Latest[Subject];

            var choices = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => runs.ChooseFix(Review, FixChoice.Continue))));
            var started = Assert.IsType<StartResult.Started>(Assert.Single(choices, result => result is StartResult.Started));
            Assert.Equal(new StartResult.Refused(new StartProblem.NoFixChoice("Review add")), Assert.Single(choices, result => result is StartResult.Refused));
            Assert.Equal(new StartResult.Refused(new StartProblem.NoFixChoice("Review add")), runs.ChooseFix(Review, FixChoice.Retry));

            await Until(() => runs.Latest[Review].Status == AttemptStatus.Succeeded, "the review approves");
            Assert.Equal("3", File.ReadAllText(Path.Combine(_implementer, "count")));
            Assert.Equal(("thread/resume", ImplementerSession), Thread(_implementer, 3));
            Assert.StartsWith("Closing iDevelop interrupted your work on these findings. Go on from where you stopped.", Prompt(_implementer, 3));
            Assert.Contains("### Finding 1\n\nadd subtracts.", Prompt(_implementer, 3));
            var fix = runs.Latest[Subject];
            Assert.Equal(started.Attempt.Id, fix.Id);
            Assert.Equal((new ReviewLink(Review, runs.Latest[Review].Id, 1, 0), interrupted.Id, AttemptStatus.Succeeded), (fix.Fix, fix.Continues, fix.Status));
            Assert.Equal(2, runs.Latest[Review].Turns.Count);
        }
    }

    [Fact]
    public async Task Retry_fix_runs_the_interrupted_round_once_in_a_fresh_session()
    {
        var runs = await Interrupted();
        await using (runs)
        {
            runs.Follow(Workflow);
            await Until(() => runs.Check(ReviewNode) is StartProblem.FixInterrupted, "the review asks for the person's choice");
            var interrupted = runs.Latest[Subject];

            var choices = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => runs.ChooseFix(Review, FixChoice.Retry))));
            Assert.Single(choices, result => result is StartResult.Started);
            Assert.Single(choices, result => result is StartResult.Refused);

            await Until(() => runs.Latest[Review].Status == AttemptStatus.Succeeded, "the review approves");
            Assert.Equal("3", File.ReadAllText(Path.Combine(_implementer, "count")));
            Assert.Equal("thread/start", Method(_implementer, 3));
            var prompt = Prompt(_implementer, 3);
            Assert.StartsWith("You retry this fix round in a fresh session. This is the ticket you worked on, and your change so far.", prompt);
            Assert.Contains("## The ticket\n\n# Add numbers", prompt);
            Assert.Contains("+    return a - b", prompt);
            var fix = runs.Latest[Subject];
            Assert.Equal((new ReviewLink(Review, runs.Latest[Review].Id, 1, 0), (AttemptId?)null), (fix.Fix, fix.Continues));
            Assert.NotEqual(interrupted.Id, fix.Id);
        }
    }

    [Fact]
    public async Task Continue_fix_needs_a_session_that_can_go_on_and_Retry_fix_stays_available()
    {
        var runs = await Interrupted();
        await using (runs)
        {
            runs.Follow(Workflow.Must(new WorkflowEdit.SetExecution(Subject,
                new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" })));
            var choice = new StartProblem.FixInterrupted("Add numbers", 1, CanContinue: false);
            await Until(() => runs.Check(ReviewNode) == choice, "the review asks for the person's choice");

            Assert.Equal(new StartResult.Refused(choice), runs.ChooseFix(Review, FixChoice.Continue));
            await Task.Delay(300);
            Assert.Equal("2", File.ReadAllText(Path.Combine(_implementer, "count")));
            Assert.Equal(AttemptStatus.Interrupted, runs.Latest[Subject].Status);
        }
    }

    /// <summary>Runs the subject and the review's first turn, quits during fix round 1, and opens the project again.</summary>
    private async Task<ProjectRuns> Interrupted()
    {
        Reviewer(1, Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}"""));
        WriteTurn(_implementer, 2, FakeRule.On().Print(SessionLine(ClientId.Codex, ImplementerSession)).Hang());
        Implementer(3, Fixed, Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}]"""));
        Reviewer(2, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""));
        var clients = await _fakes.DiscoverAsync();
        await using (var first = ProjectRuns.Open(_project, clients))
        {
            first.Follow(Workflow);
            Assert.IsType<StartResult.Started>(first.Start(SubjectNode));
            await Until(() => first.Latest[Subject].Status == AttemptStatus.Succeeded && first.Active.IsEmpty, "the subject succeeds");
            Assert.IsType<StartResult.Started>(first.Start(ReviewNode));
            // A fix round's attempt carries the session it resumes from its start, so only the client's own count says
            // that it took its turn. Leaving earlier would hand the reopened fix round this turn's script.
            await Until(() => File.Exists(Path.Combine(_implementer, "2.stdin")), "fix round 1 runs");
        }

        return ProjectRuns.Open(_project, clients);
    }

    [Fact]
    public async Task Following_a_second_workflow_that_holds_a_followed_task_is_refused()
    {
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        var first = Workflow;
        runs.Follow(first);

        var error = Assert.Throws<InvalidOperationException>(() => runs.Follow(Workflow.Empty(WorkflowId.New()).Must(TestNodes.Place(SubjectNode, new CanvasPoint(0, 0)))));

        Assert.StartsWith("Workflow ", error.Message);
        Assert.EndsWith($"shares a task with workflow {first.Id}. A task belongs to one workflow of a project.", error.Message);
    }

    [Fact]
    public async Task Deleting_a_review_that_goes_on_frees_its_subject()
    {
        Reviewer(1, Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}"""));
        WriteTurn(_implementer, 2, FakeRule.On().Print(SessionLine(ClientId.Codex, ImplementerSession)).Hang());
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        var workflow = Workflow;
        runs.Follow(workflow);
        Assert.IsType<StartResult.Started>(runs.Start(SubjectNode));
        await Until(() => runs.Latest[Subject].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the subject succeeds");
        Assert.IsType<StartResult.Started>(runs.Start(ReviewNode));
        await Until(() => runs.Latest.GetValueOrDefault(Subject) is { Fix: not null, SessionId: not null }, "fix round 1 runs");

        runs.Follow(workflow.Must(new WorkflowEdit.Delete([Review], [])));
        Assert.Null(await runs.CancelAsync(Subject));
        await Until(() => runs.Latest[Subject].Status == AttemptStatus.Cancelled && runs.Active.IsEmpty, "the fix round is cancelled");

        Assert.Null(runs.Check(SubjectNode));
    }

    [Fact]
    public async Task A_second_review_of_the_same_task_starts_only_once_the_first_review_ends()
    {
        WriteTurn(_reviewer, 1, FakeRule.On()
            .Print(SessionLine(ClientId.ClaudeCode, ReviewerSession))
            .WaitForFile(_gate)
            .Print(ReplyLines(ClientId.ClaudeCode, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""))));
        var second = new TaskDefinition(TestTasks.Design, BuiltInBlueprints.Review)
        {
            Title = "Second review",
            Execution = ReviewNode.Execution,
        };
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        runs.Follow(Workflow
            .Must(TestNodes.Place(second, new CanvasPoint(300, 200)))
            .Must(new WorkflowEdit.Connect(new ConnectionKey(Subject, TestTasks.Design), ConnectionKind.Dependency)));
        Assert.IsType<StartResult.Started>(runs.Start(SubjectNode));
        await Until(() => runs.Latest[Subject].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the subject succeeds");
        Assert.IsType<StartResult.Started>(runs.Start(ReviewNode));

        var refused = new StartProblem.SubjectInReview("Add numbers", "Review add");
        Assert.Equal(refused, runs.Check(second));
        Assert.Equal(new StartResult.Refused(refused), runs.Start(second));

        File.WriteAllText(_gate, "");
        await Until(() => runs.Latest[Review].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the first review approves");
        Assert.Null(runs.Check(second));
    }

    [Fact]
    public async Task Reviews_in_two_followed_workflows_check_and_fix_their_own_subjects()
    {
        var secondSubject = TestNodes.Implement(TestTasks.Design, "Multiply numbers", "Write multiply.py.", execution: SubjectNode.Execution);
        var secondReview = new TaskDefinition(TaskId.New(), BuiltInBlueprints.Review)
        {
            Title = "Review multiply",
            Execution = ReviewNode.Execution,
        }.WithField("focus", "Check multiplication.")!;
        var firstFixGate = Path.Combine(_temp.Create("first-fix-gate"), "go");
        var secondFixGate = Path.Combine(_temp.Create("second-fix-gate"), "go");
        var firstWorkflow = Workflow;
        var secondWorkflow = Workflow.Empty(WorkflowId.New())
            .Must(TestNodes.Place(secondSubject, new CanvasPoint(0, 0)))
            .Must(TestNodes.Place(secondReview, new CanvasPoint(300, 0)))
            .Must(new WorkflowEdit.Connect(new ConnectionKey(secondSubject.Id, secondReview.Id), ConnectionKind.Dependency));
        WriteTurn(_implementer, 2, FakeRule.On()
            .Print(SessionLine(ClientId.Codex, ImplementerSession))
            .Write("multiply.py", "def multiply(a, b): return a + b\n")
            .Print(ReplyLines(ClientId.Codex, "Wrote multiply.py.")));
        WriteTurn(_implementer, 3, FakeRule.On()
            .Print(SessionLine(ClientId.Codex, ImplementerSession))
            .WaitForFile(firstFixGate)
            .Write("calc.py", Fixed)
            .Print(ReplyLines(ClientId.Codex, Answers("""[{"id": "add", "answer": "fixed", "note": "It adds now."}]"""))));
        WriteTurn(_implementer, 4, FakeRule.On()
            .Print(SessionLine(ClientId.Codex, ImplementerSession))
            .WaitForFile(secondFixGate)
            .Write("multiply.py", "def multiply(a, b): return a * b\n")
            .Print(ReplyLines(ClientId.Codex, Answers("""[{"id": "multiply", "answer": "fixed", "note": "It multiplies now."}]"""))));
        WriteTurn(_reviewer, 1, FakeRule.On()
            .Print(SessionLine(ClientId.ClaudeCode, ReviewerSession))
            .WaitForFile(_gate)
            .Print(ReplyLines(ClientId.ClaudeCode, Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "add", "text": "add subtracts.", "change": "Return a + b."}]}"""))));
        Reviewer(2, Verdict("""{"status": "verdict", "verdict": "changes", "findings": [{"id": "multiply", "text": "multiply adds.", "change": "Return a * b."}]}"""));
        Reviewer(3, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""));
        Reviewer(4, Verdict("""{"status": "verdict", "verdict": "approve", "findings": []}"""));
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.Follow(firstWorkflow);
        runs.Follow(secondWorkflow);
        Assert.Equal(new StartProblem.SubjectNotDone("Add numbers"), runs.Check(ReviewNode));
        Assert.Equal(new StartProblem.SubjectNotDone("Multiply numbers"), runs.Check(secondReview));
        Assert.IsType<StartResult.Started>(runs.Start(SubjectNode));
        await Until(() => runs.Latest[Subject].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the first subject succeeds");
        Assert.IsType<StartResult.Started>(runs.Start(secondSubject));
        await Until(() => runs.Latest[secondSubject.Id].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the second subject succeeds");

        Assert.Null(runs.Check(ReviewNode));
        Assert.Null(runs.Check(secondReview));
        Assert.IsType<StartResult.Started>(runs.Start(ReviewNode));
        await Until(() => File.Exists(Path.Combine(_reviewer, "1.stdin")), "the first workflow's reviewer starts");
        runs.Follow(secondWorkflow.Must(new WorkflowEdit.Rename("Multiply")));
        Assert.Equal(new StartProblem.UnderReview("Review add"), runs.Check(SubjectNode));
        Assert.Equal(new SendProblem.CannotStart(new StartProblem.UnderReview("Review add")), runs.CheckSend(SubjectNode));
        File.WriteAllText(_gate, "");
        await Until(() => File.Exists(Path.Combine(_implementer, "3.stdin")), "the first workflow's fix round starts");
        Assert.IsType<StartResult.Started>(runs.Start(secondReview));
        await Until(() => File.Exists(Path.Combine(_implementer, "4.stdin")), "the second workflow's fix round starts");
        Assert.Equal(AttemptStatus.InReview, runs.Latest[Review].Status);
        Assert.Equal(AttemptStatus.InReview, runs.Latest[secondReview.Id].Status);
        Assert.Equal([Subject, secondSubject.Id], runs.Active.Select(attempt => attempt.Task));
        runs.Follow(firstWorkflow.Must(new WorkflowEdit.Rename("Add")));
        File.WriteAllText(firstFixGate, "");
        await Until(() => runs.Latest[Review].Status == AttemptStatus.Succeeded, "the first review approves");
        Assert.Equal(AttemptStatus.InReview, runs.Latest[secondReview.Id].Status);
        File.WriteAllText(secondFixGate, "");
        await Until(() => runs.Latest[secondReview.Id].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the second review approves");

        Assert.Equal((Subject, 2, AttemptStatus.Succeeded), (runs.Latest[Review].Subject, runs.Latest[Review].Turns.Count, runs.Latest[Review].Status));
        Assert.Equal((secondSubject.Id, 2, AttemptStatus.Succeeded), (runs.Latest[secondReview.Id].Subject, runs.Latest[secondReview.Id].Turns.Count, runs.Latest[secondReview.Id].Status));
        Assert.Equal((Review, 1), (runs.Latest[Subject].Fix!.Review, runs.Latest[Subject].Fix!.Round));
        Assert.Equal((secondReview.Id, 1), (runs.Latest[secondSubject.Id].Fix!.Review, runs.Latest[secondSubject.Id].Fix!.Round));
        Assert.Equal(runs.Latest[Review].Id, runs.Latest[Subject].Fix!.Attempt);
        Assert.Equal(runs.Latest[secondReview.Id].Id, runs.Latest[secondSubject.Id].Fix!.Attempt);
        Assert.Contains("add subtracts.", Prompt(_implementer, 3));
        Assert.DoesNotContain("multiply adds.", Prompt(_implementer, 3));
        Assert.Contains("multiply adds.", Prompt(_implementer, 4));
        Assert.DoesNotContain("add subtracts.", Prompt(_implementer, 4));
        Assert.Equal(Fixed, File.ReadAllText(Path.Combine(_project, "calc.py")));
        Assert.Equal("def multiply(a, b): return a * b\n", File.ReadAllText(Path.Combine(_project, "multiply.py")));
    }

    [Fact]
    public async Task A_review_starts_only_after_its_subject_succeeded_with_a_recorded_change()
    {
        var clients = await _fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(_project, clients);
        Assert.Equal(new StartProblem.NoSubject(), runs.Check(ReviewNode));

        runs.Follow(Workflow);

        Assert.Equal(new StartProblem.SubjectNotDone("Add numbers"), runs.Check(ReviewNode));
        Assert.IsType<StartResult.Refused>(runs.Start(ReviewNode));
        Assert.Equal(new SendProblem.NotReviewing("Review add"), runs.CheckSend(ReviewNode));
    }

    [Fact]
    public async Task A_review_can_start_on_a_subject_that_ran_with_an_assume_unchanged_file()
    {
        File.WriteAllText(Path.Combine(_project, "plan.txt"), "plan\n");
        Git("add", "plan.txt");
        Git("update-index", "--assume-unchanged", "plan.txt");
        await using var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.Follow(Workflow);
        Assert.IsType<StartResult.Started>(runs.Start(SubjectNode));
        await Until(() => runs.Latest[Subject].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the subject succeeds");

        Assert.Null(runs.Check(ReviewNode));
    }

    [Fact]
    public void Each_turn_records_the_project_tree_and_a_folder_outside_Git_records_none()
    {
        var before = GitTree.Snapshot(_project);
        File.WriteAllText(Path.Combine(_project, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_project, ".idp"));
        File.WriteAllText(Path.Combine(_project, ".idp", "ignored.json"), "{}");
        var after = GitTree.Snapshot(_project);

        Assert.NotNull(before);
        Assert.NotEqual(before, after);
        Assert.Equal("diff --git a/a.txt b/a.txt", GitTree.Diff(_project, before!, after!)!.Split('\n')[0]);
        Assert.Contains("?? a.txt", Git("status", "--porcelain"));
        Assert.Null(GitTree.Snapshot(_temp.Create("plain")));
    }

    [Fact]
    public async Task Review_receives_Check_null_handling()
    {
        WriteTurn(_reviewer, 1, FakeRule.On().Print(SessionLine(ClientId.ClaudeCode, ReviewerSession)).WaitForFile(_gate)
            .Print(ReplyLines(ClientId.ClaudeCode, Verdict("""{"status":"verdict","verdict":"approve","findings":[]}"""))));
        var (review, runs) = await RunLoop(async runs =>
        {
            await Until(() => runs.Latest[Review].SessionId == ReviewerSession, "the reviewer reports its session");
            using var session = runs.OpenConversation(Review);
            Assert.Equal(new SendResult.Guided(), await session.SendAsync(session.Snapshot.Current!.Value, "Check null handling", false, default));
            var recorded = runs.Latest[Review];
            Assert.Equal(["Check null handling"], recorded.Guidance.Select(note => note.Text));
            Assert.Single(recorded.Turns);
            File.WriteAllText(_gate, "go");
        });
        await using (runs)
        {
            Assert.Equal((AttemptStatus.Succeeded, 1), (review.Status, review.Turns.Count));
            Assert.Equal(["Check null handling"], review.Guidance.Select(note => note.Text));
        }
    }

    /// <summary>Runs the subject, then the review, and waits until the review settles.</summary>
    private async Task<(AttemptRecord Review, ProjectRuns Runs)> RunLoop(Func<ProjectRuns, Task>? during = null)
    {
        var runs = ProjectRuns.Open(_project, await _fakes.DiscoverAsync());
        runs.Follow(Workflow);
        Assert.IsType<StartResult.Started>(runs.Start(SubjectNode));
        await Until(() => runs.Latest[Subject].Status == AttemptStatus.Succeeded && runs.Active.IsEmpty, "the subject succeeds");
        Assert.IsType<StartResult.Started>(runs.Start(ReviewNode));
        if (during is not null)
        {
            await during(runs);
        }

        await Until(() => runs.Latest.GetValueOrDefault(Review) is { Status: not (AttemptStatus.Running or AttemptStatus.InReview) }, "the review settles");
        return (runs.Latest[Review], runs);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Patience, $"Timed out waiting until {what}.");
            await Task.Delay(25);
        }
    }

    private void Implementer(int turn, string? write, string reply, bool waitForGate = false)
    {
        var steps = FakeRule.On().Print(SessionLine(ClientId.Codex, ImplementerSession));
        steps = waitForGate ? steps.WaitForFile(_gate) : steps;
        steps = write is null ? steps : steps.Write("calc.py", write);
        WriteTurn(_implementer, turn, steps.RecordArguments(Path.Combine(_implementer, $"{turn}.args")).Print(ReplyLines(ClientId.Codex, reply)));
    }

    private void Reviewer(int turn, string reply) => WriteTurn(_reviewer, turn, FakeRule.On()
        .Print(SessionLine(ClientId.ClaudeCode, ReviewerSession))
        .RecordArguments(Path.Combine(_reviewer, $"{turn}.args"))
        .Print(ReplyLines(ClientId.ClaudeCode, reply)));

    private static void WriteTurn(string folder, int turn, FakeRule steps) => File.WriteAllText(Path.Combine(folder, $"{turn}.json"), steps.StepsJson());

    private static string Verdict(string json) => $"I read the change.\n\n```idevelop\n{json}\n```";

    private static string Answers(string answers) => $"I answered each finding.\n\n```idevelop\n{{\"status\": \"answers\", \"answers\": {answers}}}\n```";

    private static string Prompt(string folder, int turn) => File.ReadAllText(Path.Combine(folder, $"{turn}.stdin"));

    private static (string?, string?) Thread(string folder, int turn)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, $"{turn}.args.thread.json")));
        return (json.RootElement.GetProperty("method").GetString(), json.RootElement.GetProperty("params").GetProperty("threadId").GetString());
    }

    private static string? Method(string folder, int turn)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, $"{turn}.args.thread.json")));
        return json.RootElement.GetProperty("method").GetString();
    }

    private static string[] Arguments(string folder, int turn) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(folder, $"{turn}.args")))!;

    private static bool Has(string[] arguments, params string[] run) =>
        Enumerable.Range(0, Math.Max(0, arguments.Length - run.Length + 1)).Any(index => arguments[index..(index + run.Length)].SequenceEqual(run));

    private string Git(params string[] arguments)
    {
        using var git = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = _project, RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return output;
    }
}
