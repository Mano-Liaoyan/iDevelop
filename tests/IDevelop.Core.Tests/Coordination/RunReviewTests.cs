using IDevelop.Core.Tests.Turns;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;


namespace IDevelop.Core.Tests.Coordination;

/// <summary>
/// Reviews in a workflow run (E3c.2): the reviewer's turns and the subject's fix rounds go through the run's coordinator,
/// one client root at a time. One fake Codex answers every turn, and the start of each prompt picks the task's script.
/// </summary>
public sealed class RunReviewTests
{
    /// <summary>The review node. Its folder and launch count are C's.</summary>
    private static readonly TaskId R = C;

    private const string Changes1 = """{"status": "verdict", "verdict": "changes", "findings": [{"id": "1", "text": "add subtracts.", "change": "Return a + b."}]}""";
    private const string Changes2 = """{"status": "verdict", "verdict": "changes", "findings": [{"id": "2", "text": "add has no comment.", "change": "Add # sum."}]}""";
    private const string Approve = """{"status": "verdict", "verdict": "approve", "findings": []}""";

    /// <summary>A review whose reviewer prompt starts with "Review the change." and whose fix prompt starts with "Fix the findings.".</summary>
    internal static TaskDefinition Reviewer(TaskId task, string opening = "Review the change.") => new(task, new Blueprint(
        new(opening == "Review the change." ? "example.review" : "example.check", 1), "Review",
        new WorkSpec.Review(PromptTemplate.Parse(opening + "\n\n{{ticket}}\n\n{{report}}\n\n{{change}}"),
            PromptTemplate.Parse("Fix the findings.\n\n{{findings}}")), [], new(null, ConversationMode.Autonomous)))
    {
        Title = opening == "Review the change." ? "Review" : "Check",
        Execution = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" },
    };

    /// <summary><c>A → Review → B</c>: a writer, its review, and a writer that consumes the agreed code.</summary>
    internal static Workflow Reviewed() =>
        Graph([RunConversationFixture.Agent(A, ConversationMode.Autonomous), Reviewer(R), RunConversationFixture.Agent(B, ConversationMode.Autonomous)],
            (A, R), (R, B));

    internal static string Verdict(string json) => $"I read the change.\n\n```idevelop\n{json}\n```";

    internal static string Answers(string answers) => $"I answered each finding.\n\n```idevelop\n{{\"status\": \"answers\", \"answers\": {answers}}}\n```";

    /// <summary>Routes the reviewer's later turns to the review's script and every fix round to the subject's.</summary>
    internal static RunConversationFixture Routed(RunConversationFixture f) => f
        .Route("Review the change.", R).Route("The implementer answered", R).Route("iDevelop could not read the verdict", R)
        .Route("Fix the findings.", A).Route("Closing iDevelop interrupted", A).Route("You retry this fix round", A)
        .Route("Your earlier session could not continue", A);

    /// <summary>A turn of <paramref name="task"/> that writes <paramref name="file"/> before it answers.</summary>
    internal static FakeRule Writes(RunConversationFixture f, TaskId task, int turn, string file, string text, string reply,
        string session = "session-1", string? gate = null, (string Name, byte[] Bytes)? artifact = null)
    {
        var rule = FakeRule.On()
            .RecordArguments(Path.Combine(f.Evidence, $"{Name(task)}-{turn}.args"))
            .RecordWorkingDirectory(Path.Combine(f.Evidence, $"{Name(task)}-{turn}.cwd"))
            .Print(FakeAgents.SessionLine(ClientId.Codex, session));
        rule = (gate is null ? rule : rule.WaitForFile(f.Gate(gate))).Write(file, text);
        if (artifact is { } declared) rule = rule.Outbox(declared.Name, declared.Bytes);
        return rule.Print(FakeAgents.ReplyLines(ClientId.Codex, reply));
    }

    private static readonly byte[] Payload = [67, 0, 127];

    /// <summary>A reviewer turn that copies the checkout's <c>calc.txt</c> to the evidence folder before its verdict.</summary>
    internal static FakeRule Reads(RunConversationFixture f, int turn, string verdict, string? gate = null)
    {
        var rule = FakeRule.On()
            .RecordArguments(Path.Combine(f.Evidence, $"{Name(R)}-{turn}.args"))
            .RecordWorkingDirectory(Path.Combine(f.Evidence, $"{Name(R)}-{turn}.cwd"))
            .Print(FakeAgents.SessionLine(ClientId.Codex, "session-r"))
            .Copy("calc.txt", Path.Combine(f.Evidence, $"review-{turn}.calc.txt"));
        return (gate is null ? rule : rule.WaitForFile(f.Gate(gate))).Print(FakeAgents.ReplyLines(ClientId.Codex, Verdict(verdict)));
    }

    [Fact]
    public async Task A_three_round_review_fixes_twice_and_hands_on_once_after_agreement()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A,
                Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"),
                Writes(f, A, 2, "calc.txt", "a + b\n", Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}]"""), gate: "fix-1"),
                Writes(f, A, 3, "calc.txt", "a + b # sum\n", Answers("""[{"id": "2", "answer": "fixed", "note": "Commented."}]""")))
            .Answer(R, Reads(f, 1, Changes1), Reads(f, 2, Changes2), Reads(f, 3, Approve, gate: "agree"))
            .Answer(B, Writes(f, B, 1, "b.txt", "B\n", "B ready.\n"));
        await f.Open();
        await f.Resume();

        await f.Until(view => view.Tasks[A].State == TaskState.Running);
        await TurnFixture.WaitUntilAsync(() => f.Launches(A) == 2);
        var review = f.Attempt(R);
        Assert.Equal((TaskState.Waiting, AttemptStatus.InReview), (f.View.Tasks[R].State, f.View.Tasks[R].Status));
        using (var session = f.Session(R))
            Assert.IsType<SendResult.Guided>(await session.SendAsync(new TurnKey(review, 1), "Keep add on one line.", false, default).WaitAsync(Bound));
        Assert.Equal(1, f.Lines(R, "guidanceAdded"));
        f.Open("fix-1");

        await f.Until(view => view.Tasks[R].State == TaskState.Running && f.Log(R).Record!.Turns.Count == 3);
        await TurnFixture.WaitUntilAsync(() => f.Launches(R) == 3);
        Assert.Equal(0, f.Launches(B));
        f.Open("agree");
        var done = await f.UntilStatus(RunStatus.Completed);

        Assert.Equal((3, 3, 1), (f.Launches(R), f.Launches(A), f.Launches(B)));
        var record = f.Read();
        Assert.Equal(["Initial", "ReviewFix", "ReviewFix"], record.Attempts.Values.Where(attempt => attempt.Task == A)
            .OrderBy(attempt => record.Receipts.Values.Single(entry => entry.Event is RunEvent.Reserved reserved && reserved.Attempt.Id == attempt.Id).Sequence)
            .Select(attempt => attempt.Cause.GetType().Name));
        Assert.Equal([1, 2], record.Attempts.Values.Select(attempt => attempt.Cause).OfType<AttemptCause.ReviewFix>()
            .Select(fix => fix.Link.Round).Order());
        Assert.All(record.Attempts.Values.Select(attempt => attempt.Cause).OfType<AttemptCause.ReviewFix>(), fix => Assert.Equal(review, fix.Link.Attempt));
        Assert.Single(record.Attempts.Values, attempt => attempt.Task == R);
        Assert.Equal(("session-1", "session-1"), (f.Resumed(A, 2), f.Resumed(A, 3)));
        Assert.Equal(("session-r", "session-r"), (f.Resumed(R, 2), f.Resumed(R, 3)));

        Assert.StartsWith("Review the change.", f.Prompt(R, 1));
        Assert.Contains("+a - b", f.Prompt(R, 1));
        Assert.StartsWith("Fix the findings.\n\n### Finding 1\n\nadd subtracts.", f.Prompt(A, 2));
        Assert.DoesNotContain("Keep add on one line.", f.Prompt(A, 2));
        Assert.StartsWith("The implementer answered your findings of round 1.", f.Prompt(R, 2));
        Assert.Contains("-a - b\n+a + b", f.Prompt(R, 2));
        Assert.Contains("## Guidance from the person\n\nKeep add on one line.", f.Prompt(R, 2));
        Assert.Contains("## Guidance from the person\n\nKeep add on one line.", f.Prompt(A, 3));
        Assert.DoesNotContain("Keep add on one line.", f.Prompt(R, 3));
        Assert.Equal(["a - b\n", "a + b\n", "a + b # sum\n"], Enumerable.Range(1, 3).Select(turn => File.ReadAllText(Path.Combine(f.Evidence, $"review-{turn}.calc.txt"))));

        var log = f.Log(R).Record!;
        Assert.Equal((AttemptStatus.Succeeded, 3, A), (log.Status, log.Turns.Count, log.Subject));
        Assert.Equal([FindingState.Resolved, FindingState.Resolved], ReviewLedger.Fold(log).Findings.Select(finding => finding.State));
        var agreed = record.CurrentResults[R];
        var code = Assert.IsType<CodeSelection.Single>(record.Inputs[record.CurrentResults[B].Inputs].Code);
        Assert.Equal(R, code.Source.Task);
        Assert.Equal(A, Assert.Single(code.Source.Owners));
        Assert.Equal(new CodeOutput.Forwarded(agreed.Inputs), agreed.Code);
        Assert.Equal("a + b # sum\n", File.ReadAllText(Path.Combine(f.Checkout(B), "calc.txt")));
        long Sequence(Func<RunEvent, bool> match) => record.Receipts.Values.Single(entry => match(entry.Event)).Sequence;
        Assert.True(Sequence(e => e is RunEvent.ResultAccepted accepted && accepted.Result.Task == R) <
            Sequence(e => e is RunEvent.TurnClaimed claimed && record.Attempts[claimed.Key.Attempt].Task == B));
        Assert.Equal(TaskState.Done, done.Tasks[R].State);
    }

    [Fact]
    public async Task Agreement_forwards_the_reviewed_code_and_artifacts_without_making_the_reviewer_their_owner()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A, Writes(f, A, 1, "calc.txt", "a + b\n", "A ready.\n", artifact: ("payload", Payload)))
            .Answer(R, Reads(f, 1, Approve))
            .Answer(B, Writes(f, B, 1, "b.txt", "B\n", "B ready.\n"));
        await f.Open();
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        var record = f.Read();
        var produced = record.CurrentResults[A];
        var agreed = record.CurrentResults[R];
        var original = Assert.Single(produced.Artifacts);
        var forwarded = Assert.Single(agreed.Artifacts);
        Assert.Equal(("payload", Revision.Hash(Payload), 3L), (forwarded.Name, forwarded.Content, forwarded.ByteLength));
        Assert.Equal((original.Name, original.Content, original.ByteLength), (forwarded.Name, forwarded.Content, forwarded.ByteLength));
        Assert.Equal(RunStorage.ArtifactPath(agreed.Id, "payload"), forwarded.StoredPath);
        Assert.Equal(RunStorage.ArtifactPath(produced.Id, "payload"), original.StoredPath);
        var storage = new RunStorage(f.Preparation.Git.Folder, Runs.RunFixtures.W, f.Preparation.RunId);
        Assert.Equal(Payload, storage.ReadArtifact(agreed.Id, forwarded));
        Assert.Equal(Payload, storage.ReadArtifact(produced.Id, original));

        var inputs = record.Inputs[record.CurrentResults[B].Inputs];
        var delivered = Assert.Single(inputs.Files, file => file.RelativePath.EndsWith("/artifacts/payload", StringComparison.Ordinal));
        Assert.Equal(agreed.Id, delivered.Source);
        Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(f.Checkout(B), delivered.RelativePath)));
        Assert.Contains($"- payload: {delivered.RelativePath} (3 bytes, SHA-256 {Revision.Hash(Payload).Sha256})", f.Prompt(B, 1));
        var code = Assert.IsType<CodeSelection.Single>(inputs.Code);
        Assert.Equal((R, A), (code.Source.Task, Assert.Single(code.Source.Owners)));
        Assert.Equal("a + b\n", File.ReadAllText(Path.Combine(f.Checkout(B), "calc.txt")));
        Assert.Equal(Assert.IsType<CodeOutput.Produced>(produced.Code).Code.Commit, code.Source.Commit);
    }

    [Fact]
    public async Task Two_inputs_with_different_artifacts_of_one_name_block_the_review_before_it_runs()
    {
        // A review takes one writer as its subject, so the second artifact comes through another review that forwards it.
        var workflow = Graph([RunConversationFixture.Agent(A, ConversationMode.Autonomous), RunConversationFixture.Agent(D, ConversationMode.Autonomous),
            Reviewer(X, "Check the change."), Reviewer(R), RunConversationFixture.Agent(B, ConversationMode.Autonomous)], (A, R), (D, X), (X, R), (R, B));
        await using var f = new RunConversationFixture(workflow);
        Routed(f).Route("Check the change.", X).Answer(A, Writes(f, A, 1, "calc.txt", "a + b\n", "A ready.\n", artifact: ("payload", Payload)))
            .Answer(D, Writes(f, D, 1, "d.txt", "D\n", "D ready.\n", artifact: ("PAYLOAD", [88])))
            .Answer(X, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-x")).Print(FakeAgents.ReplyLines(ClientId.Codex, Verdict(Approve))))
            .Answer(R, Reads(f, 1, Approve));
        await f.Open();
        await f.Resume();
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[R].State == TaskState.Blocked);

        Assert.Equal(MaterializationProblem.ArtifactCollision, stuck.Tasks[R].Block!.Problem);
        Assert.Equal("Two inputs hand on different artifacts named PAYLOAD.", stuck.Tasks[R].Block!.Detail);
        Assert.Equal((1, 1, 1, 0, 0), (f.Launches(A), f.Launches(D), f.Launches(X), f.Launches(R), f.Launches(B)));
        Assert.Equal(("PAYLOAD", Revision.Hash([88])), (Assert.Single(f.Read().CurrentResults[X].Artifacts).Name, f.Read().CurrentResults[X].Artifacts[0].Content));
        Assert.DoesNotContain(f.Read().Attempts.Values, attempt => attempt.Task == R);
    }
}
