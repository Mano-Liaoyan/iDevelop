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

    /// <summary>A fix round that writes <c>keep.txt</c> and then works until the window closes.</summary>
    private static FakeRule Interrupting(RunConversationFixture f, string keep = "keep\n", bool session = true)
    {
        var rule = FakeRule.On().RecordArguments(Path.Combine(f.Evidence, "A-2.args"));
        if (session) rule = rule.Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"));
        return rule.Write("keep.txt", keep).Write(Path.Combine(f.Evidence, "fix-1-wrote"), "yes").WaitForFile(f.Gate("never"));
    }

    /// <summary>Runs A and the review's first turn, closes the window while fix round 1 works, and opens it again with the run resumed.</summary>
    private static async Task<FixRecovery> Interrupt(RunConversationFixture f)
    {
        await f.Open();
        await f.Resume();
        await TurnFixture.WaitUntilAsync(() => File.Exists(Path.Combine(f.Evidence, "fix-1-wrote")));
        await f.Reopen();
        // The choice shows once the interrupted fix is closed, which the closing window did; the run waits for Resume.
        Assert.Equal(RunStatus.Paused, (await f.Until(view => view.Tasks[R].Fix is not null)).Status);
        await f.Resume();
        var waiting = await f.Until(view => view.Resumed && view.Tasks[R].Fix is not null);
        Assert.Equal((TaskState.Waiting, TaskState.Failed), (waiting.Tasks[R].State, waiting.Tasks[A].State));
        Assert.Equal(TerminalAttemptOutcome.Interrupted, Assert.IsType<AttemptEnd.Logged>(f.Read().Closures[waiting.Tasks[R].Fix!.Fix]).Outcome);
        Assert.Equal(RunStatus.NeedsAttention, waiting.Status);
        return waiting.Tasks[R].Fix!;
    }

    private static int Count(RunConversationFixture f, Func<AttemptCause, bool> cause) => f.Read().Attempts.Values.Count(attempt => cause(attempt.Cause));

    [Fact]
    public async Task An_interrupted_fix_waits_for_Continue_fix_and_a_duplicate_choice_reserves_one_attempt_in_its_session()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A,
                Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"),
                Interrupting(f),
                FakeRule.On().RecordArguments(Path.Combine(f.Evidence, "A-3.args")).Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
                    .Copy("keep.txt", Path.Combine(f.Evidence, "continued.keep.txt")).Write("calc.txt", "a + b\n")
                    .Print(FakeAgents.ReplyLines(ClientId.Codex, Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}]"""))))
            .Answer(R, Reads(f, 1, Changes1), Reads(f, 2, Approve))
            .Answer(B, Writes(f, B, 1, "b.txt", "B\n", "B ready.\n"));
        var recovery = await Interrupt(f);
        Assert.Equal((1, (string?)null), (recovery.Round, recovery.ContinueUnavailable));
        await f.Decided();
        Assert.Equal((2, 1, 0), (f.Launches(A), f.Launches(R), Count(f, cause => cause is AttemptCause.Continue or AttemptCause.Retry)));

        var confirmation = new OperationId(Guid.NewGuid());
        var first = Assert.IsType<FixReply.Reserved>(await f.Coordinator.ContinueFix(f.Address, R, confirmation).WaitAsync(Bound));
        Assert.Equal(first, await f.Coordinator.ContinueFix(f.Address, R, confirmation).WaitAsync(Bound));
        Assert.Equal(RunProblem.ReplacementConflict, Assert.IsType<FixReply.Refused>(
            await f.Coordinator.ContinueFix(f.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(RunProblem.ReplacementConflict, Assert.IsType<FixReply.Refused>(
            await f.Coordinator.RetryFix(f.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound)).Reason.Problem);
        await f.UntilStatus(RunStatus.Completed);

        var record = f.Read();
        Assert.Equal(1, Count(f, cause => cause is AttemptCause.Continue));
        Assert.Equal(0, Count(f, cause => cause is AttemptCause.Retry));
        var continued = record.Attempts[first.Attempt];
        Assert.Equal(new AttemptCause.Continue(recovery.Fix, confirmation), continued.Cause);
        Assert.Equal(new ReviewLink(R, f.Attempt(R), 1, 0), record.ReviewOf(continued.Id));
        Assert.Equal((3, 2, 1), (f.Launches(A), f.Launches(R), f.Launches(B)));
        Assert.Equal("session-1", f.Resumed(A, 3));
        Assert.StartsWith("Closing iDevelop interrupted your work on these findings. Go on from where you stopped.\n\nFix the findings.", f.Prompt(A, 3));
        var baseline = Assert.Single(record.Baselines.Values).Baseline;
        Assert.Equal((recovery.Fix, confirmation, "session-1"), (baseline.Previous, baseline.Confirmation, baseline.Session));
        var preserved = record.Preservations[OperationIds.Derive(baseline.Preservation, "preserve-plan")];
        Assert.Equal("keep\n", f.Preparation.Git.Git("show", $"{preserved.Commit.Hex}:keep.txt"));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(f.Evidence, "continued.keep.txt")));
        Assert.Equal(continued.Id, Assert.IsType<ResultOrigin.Executed>(record.CurrentResults[A].Origin).Attempt);
        Assert.Equal("a + b\n", File.ReadAllText(Path.Combine(f.Checkout(B), "calc.txt")));
    }

    [Fact]
    public async Task Continue_fix_over_a_changing_checkout_blocks_and_starts_nothing()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A, Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"), Interrupting(f, "first\n"))
            .Answer(R, Reads(f, 1, Changes1));
        await Interrupt(f);
        var keep = Path.Combine(f.Checkout(A), "keep.txt");
        f.Runs.Probe = point =>
        {
            if (point == "git.preserve-observe-1.after") File.WriteAllText(keep, "second\n");
        };

        var blocked = Assert.IsType<FixReply.Blocked>(await f.Coordinator.ContinueFix(f.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound));

        Assert.Equal(MaterializationProblem.DirtyWorktree, blocked.Block.Problem);
        var divergence = Assert.Single(f.Read().PreservationDivergences.Values);
        Assert.Equal("first\n", f.Preparation.Git.Git("show", $"{divergence.First.Commit.Hex}:keep.txt"));
        Assert.Equal("second\n", f.Preparation.Git.Git("show", $"{divergence.Second.Commit.Hex}:keep.txt"));
        Assert.Empty(f.Read().Baselines);
        await f.Decided();
        Assert.Equal((2, 0), (f.Launches(A), Count(f, cause => cause is AttemptCause.Continue)));
        Assert.Equal("second\n", File.ReadAllText(keep));
    }

    [Fact]
    public async Task Retry_fix_salvages_the_interrupted_work_and_a_duplicate_choice_reserves_one_attempt_in_a_fresh_session()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A,
                Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"),
                Interrupting(f),
                Writes(f, A, 3, "calc.txt", "a + b\n", Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}]"""), session: "session-2"))
            .Answer(R, Reads(f, 1, Changes1), Reads(f, 2, Approve))
            .Answer(B, Writes(f, B, 1, "b.txt", "B\n", "B ready.\n"));
        var recovery = await Interrupt(f);
        await f.Decided();
        Assert.Equal(2, f.Launches(A));

        var confirmation = new OperationId(Guid.NewGuid());
        var first = Assert.IsType<FixReply.Reserved>(await f.Coordinator.RetryFix(f.Address, R, confirmation).WaitAsync(Bound));
        Assert.Equal(first, await f.Coordinator.RetryFix(f.Address, R, confirmation).WaitAsync(Bound));
        Assert.Equal(RunProblem.ReplacementConflict, Assert.IsType<FixReply.Refused>(
            await f.Coordinator.RetryFix(f.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound)).Reason.Problem);
        await f.UntilStatus(RunStatus.Completed);

        var record = f.Read();
        Assert.Equal((1, 0), (Count(f, cause => cause is AttemptCause.Retry), Count(f, cause => cause is AttemptCause.Continue)));
        Assert.Equal(new AttemptCause.Retry(recovery.Fix, confirmation), record.Attempts[first.Attempt].Cause);
        Assert.Equal(new ReviewLink(R, f.Attempt(R), 1, 0), record.ReviewOf(first.Attempt));
        Assert.Equal((3, 2, 1), (f.Launches(A), f.Launches(R), f.Launches(B)));
        Assert.Null(f.Resumed(A, 3));
        Assert.Equal("session-2", AttemptEvidence.Read(f.AttemptFolder(A, first.Attempt)).Record!.SessionId);
        var prompt = f.Prompt(A, 3);
        Assert.StartsWith("You retry this fix round in a fresh session. This is the ticket you worked on, and your change so far.", prompt);
        Assert.Contains("+a - b", prompt);
        var salvage = Assert.Single(record.Salvages.Values);
        Assert.Equal("keep\n", f.Preparation.Git.Git("show", $"{salvage.Commit.Hex}:keep.txt"));
        var fixedCommit = Assert.IsType<CodeOutput.Produced>(record.CurrentResults[A].Code).Code.Commit.Hex;
        Assert.NotEqual(0, f.Preparation.Git.Run(f.Preparation.Git.Folder, "cat-file", "-e", $"{fixedCommit}:keep.txt").ExitCode);
        Assert.Equal("a + b\n", File.ReadAllText(Path.Combine(f.Checkout(B), "calc.txt")));
    }

    [Fact]
    public async Task Continue_fix_needs_a_session_that_can_go_on_and_Retry_fix_stays_available()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A,
                FakeRule.On().Write("calc.txt", "a - b\n").Print(FakeAgents.ReplyLines(ClientId.Codex, "A ready.\n")),
                Interrupting(f, session: false),
                Writes(f, A, 3, "calc.txt", "a + b\n", Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}]"""), session: "session-2"))
            .Answer(R, Reads(f, 1, Changes1), Reads(f, 2, Approve))
            .Answer(B, Writes(f, B, 1, "b.txt", "B\n", "B ready.\n"));
        var recovery = await Interrupt(f);
        Assert.Equal(RunReviews.NoFixSession, recovery.ContinueUnavailable);
        Assert.StartsWith("Your earlier session could not continue, so this one starts fresh.", f.Prompt(A, 2));

        Assert.Equal(RunProblem.SessionUnavailable, Assert.IsType<FixReply.Refused>(
            await f.Coordinator.ContinueFix(f.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(0, Count(f, cause => cause is AttemptCause.Continue));
        Assert.IsType<FixReply.Reserved>(await f.Coordinator.RetryFix(f.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound));
        await f.UntilStatus(RunStatus.Completed);
        Assert.Equal(3, f.Launches(A));
    }

    [Fact]
    public async Task Only_the_controlling_window_chooses_how_an_interrupted_fix_goes_on()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A, Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"), Interrupting(f)).Answer(R, Reads(f, 1, Changes1));
        await Interrupt(f);
        var (runs, coordinator) = await f.SecondWindow();
        await using (runs)
        {
            Assert.Equal(new FixReply.Unavailable(WorkflowRunCoordinator.ElsewhereMessage),
                await coordinator.ContinueFix(coordinator.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound));
            Assert.Equal(new FixReply.Unavailable(WorkflowRunCoordinator.ElsewhereMessage),
                await coordinator.RetryFix(coordinator.Address, R, new OperationId(Guid.NewGuid())).WaitAsync(Bound));
        }
        Assert.Equal(RunProblem.IdentityMismatch, Assert.IsType<FixReply.Refused>(await f.Coordinator.RetryFix(f.Address with { Run = new(Guid.NewGuid()) }, R,
            new OperationId(Guid.NewGuid())).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(RunProblem.InvalidClaim, Assert.IsType<FixReply.Refused>(await f.Coordinator.RetryFix(f.Address, B,
            new OperationId(Guid.NewGuid())).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(RunProblem.ConfirmationRequired, Assert.IsType<FixReply.Refused>(await f.Coordinator.RetryFix(f.Address, R,
            default).WaitAsync(Bound)).Reason.Problem);
        Assert.Equal(0, Count(f, cause => cause is AttemptCause.Continue or AttemptCause.Retry));
    }

    [Fact]
    public async Task Cancelling_a_review_during_a_fix_round_cancels_the_fix_too()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A, Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"), Interrupting(f)).Answer(R, Reads(f, 1, Changes1));
        await f.Open();
        await f.Resume();
        await TurnFixture.WaitUntilAsync(() => File.Exists(Path.Combine(f.Evidence, "fix-1-wrote")));
        var fix = f.Attempt(A);

        using (var session = f.Session(R))
            Assert.Equal(CommandOutcome.Applied, (await session.CancelAsync(new TurnKey(f.Attempt(R), 1), default).WaitAsync(Bound)).Outcome);
        var stuck = await f.Until(view => view.Status == RunStatus.NeedsAttention && view.Tasks[A].State == TaskState.Failed);

        var record = f.Read();
        Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(record.Closures[f.Attempt(R)]).Outcome);
        Assert.Equal(TerminalAttemptOutcome.Cancelled, Assert.IsType<AttemptEnd.Logged>(record.Closures[fix]).Outcome);
        Assert.Equal((TaskState.Failed, TaskState.Pending), (stuck.Tasks[R].State, stuck.Tasks[B].State));
        Assert.Equal((2, 1, 0), (f.Launches(A), f.Launches(R), f.Launches(B)));
        Assert.Equal(fix, f.Attempt(A));
    }

    [Fact]
    public async Task A_verdict_is_asked_for_once_more_and_a_fix_round_that_fails_fails_the_review()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A,
                Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"),
                FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-1"))
                    .Print("""{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"failed","error":{"message":"The fix broke."}}}}"""))
            .Answer(R, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-r")).Print(FakeAgents.ReplyLines(ClientId.Codex, "Looks fine to me.")),
                Reads(f, 2, Changes1));
        await f.Open();
        await f.Resume();
        var failed = await f.Until(view => view.Tasks[R].State == TaskState.Failed);

        Assert.StartsWith("iDevelop could not read the verdict at the end of your last message.", f.Prompt(R, 2));
        Assert.Equal(("session-r", 2, 2), (f.Resumed(R, 2), f.Launches(R), f.Launches(A)));
        var log = f.Log(R).Record!;
        Assert.Equal(AttemptStatus.Failed, log.Status);
        Assert.Equal("\"A\" did not finish fix round 1. It ended Failed. The fix broke.", log.Detail);
        Assert.Equal(TerminalAttemptOutcome.Failed, Assert.IsType<AttemptEnd.Logged>(f.Read().Closures[f.Attempt(R)]).Outcome);
        Assert.Equal((TaskState.Failed, TaskState.Pending, 0), (failed.Tasks[A].State, failed.Tasks[B].State, f.Launches(B)));
        Assert.DoesNotContain(f.Read().Results, result => result.Task == R);
    }

    [Fact]
    public async Task A_reserved_fix_round_whose_start_failed_resumes_once_with_its_recorded_cause()
    {
        await using var f = new RunConversationFixture(Reviewed());
        Routed(f).Answer(A, Writes(f, A, 1, "calc.txt", "a - b\n", "A ready.\n"),
                Writes(f, A, 2, "calc.txt", "a + b\n", Answers("""[{"id": "1", "answer": "fixed", "note": "It adds now."}]""")))
            .Answer(R, Reads(f, 1, Changes1), Reads(f, 2, Approve))
            .Answer(B, Writes(f, B, 1, "b.txt", "B\n", "B ready.\n"));
        await f.Open();
        var fixing = false;
        var thrown = 0;
        f.Runs.Probe = point =>
        {
            if (point == "coordinator.fix") fixing = true;
            else if (point == "runner.claim.before" && fixing && Interlocked.CompareExchange(ref thrown, 1, 0) == 0)
                throw new IOException("The claim could not be written.");
        };
        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        var record = f.Read();
        Assert.Equal(1, thrown);
        var fix = Assert.Single(record.Attempts.Values, attempt => attempt.Cause is AttemptCause.ReviewFix);
        Assert.Equal(1, record.Claims.Keys.Count(key => key.Attempt == fix.Id));
        Assert.Equal((2, 2, 1), (f.Launches(A), f.Launches(R), f.Launches(B)));
        Assert.Equal("session-1", f.Resumed(A, 2));
        Assert.StartsWith("Fix the findings.\n\n### Finding 1", f.Prompt(A, 2));
    }
}
