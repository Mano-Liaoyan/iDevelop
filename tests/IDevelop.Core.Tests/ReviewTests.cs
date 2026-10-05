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
[Collection(ProcessCollection.Name)]
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
            Assert.IsType<SendResult.Guided>(runs.Send(ReviewNode, "Keep add on two lines.", stopTurn: false));
            File.WriteAllText(_gate, "");
        });
        await using (runs)
        {
            Assert.Equal((AttemptStatus.Succeeded, 3), (review.Status, review.Turns.Count));
            Assert.All(new[] { 2, 3 }, turn => Assert.True(Has(Arguments(_reviewer, turn), "--resume", ReviewerSession), $"reviewer turn {turn} resumes"));
            Assert.All(new[] { 2, 3 }, turn => Assert.True(Has(Arguments(_implementer, turn), ImplementerSession, "-") && Has(Arguments(_implementer, turn), "exec", "resume"), $"fix round {turn - 1} resumes"));

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
            Assert.Null(runs.Cancel(Review));
        });
        await using (runs)
        {
            Assert.Equal(AttemptStatus.Cancelled, review.Status);
            await Until(() => runs.Latest[Subject].Status == AttemptStatus.Cancelled, "the fix round is cancelled");
        }
    }

    [Fact]
    public async Task A_fix_round_that_quitting_interrupted_goes_on_in_the_same_session_when_the_project_opens_again()
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
            await Until(() => first.Latest.GetValueOrDefault(Subject) is { Fix: not null, SessionId: not null }, "fix round 1 runs");
        }

        await using var runs = ProjectRuns.Open(_project, clients);
        Assert.Equal((AttemptStatus.InReview, AttemptStatus.Interrupted), (runs.Latest[Review].Status, runs.Latest[Subject].Status));
        Assert.Equal(new StartProblem.UnderReview("Review add"), runs.Check(SubjectNode));
        Assert.Equal(new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.UnderReview("Review add"))), runs.Send(SubjectNode, "Go on.", stopTurn: false));

        runs.Follow(Workflow);

        await Until(() => runs.Latest[Review].Status == AttemptStatus.Succeeded, "the review approves");
        Assert.True(Has(Arguments(_implementer, 3), ImplementerSession, "-"), "the fix round resumes the implementer's session");
        Assert.Equal((1, AttemptStatus.Succeeded), (runs.Latest[Subject].Fix!.Round, runs.Latest[Subject].Status));
        Assert.Equal(2, runs.Latest[Review].Turns.Count);
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
