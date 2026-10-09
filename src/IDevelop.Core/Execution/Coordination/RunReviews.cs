using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Closing iDevelop interrupted this review's fix round, which waits for the person's Continue fix or Retry fix.</summary>
/// <param name="Fix">The interrupted fix attempt of the review's subject.</param>
/// <param name="ContinueUnavailable">Why Continue fix cannot resume the fix's session, or null when it can.</param>
internal sealed record FixRecovery(AttemptId Fix, int Round, string? ContinueUnavailable);

/// <summary>How a run answers Continue fix or Retry fix.</summary>
internal abstract record FixReply
{
    private FixReply() { }

    /// <summary>The replacement attempt is reserved and prepared. It starts at once, or once a client slot is free at the run's bound.</summary>
    internal sealed record Reserved(AttemptId Attempt) : FixReply;

    /// <summary>A recorded block holds the subject's checkout, such as a preservation whose two observations differ.</summary>
    internal sealed record Blocked(MaterializationBlock Block) : FixReply;

    internal sealed record Refused(RunRejection Reason) : FixReply;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : FixReply;
}

/// <summary>
/// A review as a workflow run reads it: its subject from the run's results and attempt logs, so the same
/// <see cref="ReviewWork"/> that drives a standalone review decides each step of a run-owned one.
/// </summary>
internal static class RunReviews
{
    internal const string NotStartedFix = "The fix's client never started, so its session cannot go on. Retry fix starts the round in a fresh session.";
    internal const string NoFixSession = "The fix reported no session, so it cannot go on. Retry fix starts the round in a fresh session.";

    /// <summary>The subject's attempt reserved just before <paramref name="attempt"/>, which a fix round follows, or null.</summary>
    public static AttemptId? Previous(RunRecord record, AttemptId attempt)
    {
        var task = record.Attempts[attempt].Task;
        AttemptId? previous = null;
        foreach (var entry in record.Receipts.Values.Where(entry => entry.Event is RunEvent.Reserved).OrderBy(entry => entry.Sequence))
        {
            var reserved = ((RunEvent.Reserved)entry.Event).Attempt;
            if (reserved.Id == attempt) return previous;
            if (reserved.Task == task) previous = reserved.Id;
        }
        return null;
    }

    /// <summary>
    /// The session a review fix resumes: that of the subject's attempt before it, when the subject still uses that client.
    /// Null starts a fresh session.
    /// </summary>
    public static Continuation? FixSession(RunRecord record, AttemptId fix, Func<RunAttempt, AttemptRecord?> log)
    {
        if (Previous(record, fix) is not { } previous || log(record.Attempts[previous]) is not { SessionId: { } session } earlier) return null;
        var attempt = record.Attempts[fix];
        return record.Revisions[attempt.Revision].Snapshot.Tasks[attempt.Task].Execution?.Client == earlier.Requested.Client
            ? new Continuation(previous, session) : null;
    }

    /// <summary>
    /// The attempt as a review's next step reads it: a closed attempt's status is its closure's, so a recovered one is
    /// interrupted. A success counts once its result is accepted; until then, and while it settles, it still runs.
    /// </summary>
    public static AttemptRecord Effective(RunRecord record, AttemptRecord attempt)
    {
        if (record.Closures.TryGetValue(attempt.Id, out var end))
        {
            var status = end switch
            {
                AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded } when record.Results.Any(result =>
                    result.Origin is ResultOrigin.Executed executed && executed.Attempt == attempt.Id) => AttemptStatus.Succeeded,
                AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Succeeded } => AttemptStatus.Running,
                AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Failed } => AttemptStatus.Failed,
                AttemptEnd.Logged { Outcome: TerminalAttemptOutcome.Cancelled } => AttemptStatus.Cancelled,
                _ => AttemptStatus.Interrupted,
            };
            return attempt with { Status = status };
        }
        return attempt.Status is AttemptStatus.Running or AttemptStatus.WaitingForInput or AttemptStatus.InReview
            ? attempt : attempt with { Status = AttemptStatus.Running };
    }

    /// <summary>
    /// The subject of the review task <paramref name="review"/> at <paramref name="revision"/> as the review reads it, or
    /// null when it has none. Its latest attempt is <paramref name="latest"/>, or else its newest one in the run. With a
    /// project folder, Git reads its whole change, from its first result's base to its current result, and the current
    /// result's own change.
    /// </summary>
    public static SubjectView? Subject(RunRecord record, TaskId review, RevisionId revision, Func<RunAttempt, AttemptRecord?> log,
        string? project, AttemptId? latest = null)
    {
        var snapshot = record.Revisions[revision].Snapshot;
        if (snapshot.SubjectOf(review) is not { } id || snapshot.Tasks.GetValueOrDefault(id) is not { } node) return null;
        var newest = latest ?? RunProjection.LatestAttempts(record).GetValueOrDefault(id);
        var read = newest is { } attempt && log(record.Attempts[attempt]) is { } logged ? Effective(record, logged) : null;
        var view = new SubjectView(node, read)
        {
            CanResume = read is { SessionId: not null } && (node.Execution is null || node.Execution.Client == read.Requested.Client),
        };
        var produced = record.Results.Where(result => result.Task == id).Select(result => result.Code).OfType<CodeOutput.Produced>().ToList();
        if (produced.Count == 0 || record.CurrentResults.GetValueOrDefault(id)?.Code is not CodeOutput.Produced current) return view;
        if (project is null) return view with { Change = "", LatestChange = "" };
        return view with
        {
            Change = GitTree.Diff(project, produced[0].Code.AttemptBase.Hex, current.Code.Commit.Hex),
            LatestChange = GitTree.Diff(project, current.Code.AttemptBase.Hex, current.Code.Commit.Hex),
        };
    }

    /// <summary>The review's next step, with its subject read as <see cref="Subject"/> does.</summary>
    public static NodeStep Step(RunRecord record, AttemptRecord review, Func<RunAttempt, AttemptRecord?> log, string? project) =>
        ReviewWork.Instance.Next(Context(record, review.Id, log, project), review);

    private static NodeContext Context(RunRecord record, AttemptId review, Func<RunAttempt, AttemptRecord?> log, string? project,
        AttemptId? latest = null)
    {
        var reviewer = record.Attempts[review];
        return new(record.Revisions[reviewer.Revision].Snapshot.Tasks[reviewer.Task], "")
        {
            Subject = Subject(record, reviewer.Task, reviewer.Revision, log, project, latest),
        };
    }

    /// <summary>
    /// The prompt of <paramref name="attempt"/>, a reserved fix of the review, rebuilt from its recorded cause: a round's
    /// fix with the guidance its link counts, or the person's Continue fix or Retry fix of an interrupted one.
    /// </summary>
    public static string FixPrompt(RunRecord record, AttemptRecord review, AttemptId attempt, Func<RunAttempt, AttemptRecord?> log, string project) =>
        FixPrompt(record, review, Previous(record, attempt), record.Attempts[attempt].Cause,
            record.ReviewOf(attempt) ?? throw new ArgumentException("The attempt fixes no review round.", nameof(attempt)),
            record.Attempts[attempt].Cause is AttemptCause.ReviewFix && FixSession(record, attempt, log) is not null, log, project);

    /// <summary>The prompt of the person's Continue fix or Retry fix of <paramref name="fix"/>, before its attempt is reserved.</summary>
    public static string ReplacementPrompt(RunRecord record, AttemptRecord review, AttemptId fix, AttemptCause cause,
        Func<RunAttempt, AttemptRecord?> log, string project) =>
        FixPrompt(record, review, fix, cause, record.ReviewOf(fix) ?? throw new ArgumentException("The attempt fixes no review round.", nameof(fix)),
            false, log, project);

    private static string FixPrompt(RunRecord record, AttemptRecord review, AttemptId? latest, AttemptCause cause, ReviewLink link, bool resumes,
        Func<RunAttempt, AttemptRecord?> log, string project)
    {
        var context = Context(record, review.Id, log, project, latest);
        // A round's fix resumes the session it can; the person's Continue fix always resumes, and Retry fix never does.
        var (choice, resumed) = cause switch
        {
            AttemptCause.ReviewFix => ((FixChoice?)null, resumes),
            AttemptCause.Continue => (FixChoice.Continue, true),
            AttemptCause.Retry => (FixChoice.Retry, false),
            _ => throw new ArgumentException("The attempt fixes no review round.", nameof(cause)),
        };
        return ReviewWork.Instance.Round(context, review, link.Guidance, resumed, choice).Prompt;
    }

    /// <summary>The interrupted fix round that waits for the person's choice, or null.</summary>
    public static FixRecovery? Recovery(RunRecord record, AttemptRecord review, Func<RunAttempt, AttemptRecord?> log)
    {
        if (Step(record, review, log, null) is not NodeStep.ChooseFix choose ||
            Context(record, review.Id, log, null).Subject?.Latest is not { } fix) return null;
        return new(fix.Id, choose.Round, ContinueUnavailable(record, fix));
    }

    /// <summary>Why Continue fix cannot resume <paramref name="fix"/>'s session, or null.</summary>
    public static string? ContinueUnavailable(RunRecord record, AttemptRecord fix) =>
        record.Closures.GetValueOrDefault(fix.Id) is AttemptEnd.Recovered { Outcome: RecoveryOutcome.NotStarted } ? NotStartedFix
        : fix.SessionId is null ? NoFixSession : null;
}
