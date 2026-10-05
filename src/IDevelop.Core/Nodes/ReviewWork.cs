using System.Text;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>
/// A review: a back-and-forth between two sessions until both agents agree. The reviewer's turns are the review
/// attempt's turns, in one session that only reads. Each fix round is a new attempt of the subject whose first turn
/// resumes the implementer's session. Each side reads its own history plus the other side's short message. The loop ends
/// when the reviewer approves with no finding left standing. It has no round limit.
/// </summary>
public sealed class ReviewWork : IConverses
{
    public static readonly ReviewWork Instance = new();

    /// <summary>What every reviewer turn ends with, which no blueprint can remove.</summary>
    internal const string VerdictContract =
        "End your final message with your verdict in this block, and nothing after it:\n\n" +
        "```idevelop\n{\"status\": \"verdict\", \"verdict\": \"changes\", " +
        "\"findings\": [{\"id\": \"1\", \"text\": \"what is wrong\", \"change\": \"the exact change that would settle it\"}], " +
        "\"withdrawn\": []}\n```\n\n" +
        "Write \"approve\" with no findings when nothing stands. Otherwise write \"changes\" and list every finding that " +
        "still stands, each with the exact change that would settle it. Keep the id of a finding you raised before, and give a new finding an id you have not used. When " +
        "the implementer disputes a finding, withdraw it by listing {\"id\": \"...\", \"reason\": \"...\"} in \"withdrawn\", or keep " +
        "it in \"findings\" and say in its text why it stands. A finding you leave out of both lists is settled.";

    /// <summary>What every fix round ends with, which no blueprint can remove.</summary>
    internal const string FixContract =
        "Then report what you changed, and end your final message with your answers in this block, and nothing after it:\n\n" +
        "```idevelop\n{\"status\": \"answers\", \"answers\": [{\"id\": \"1\", \"answer\": \"fixed\", \"note\": \"what you changed\"}]}\n```\n\n" +
        "Give one answer for each finding above, by its id: \"fixed\" with what you changed, or \"disputed\" with why the finding is wrong.";

    private ReviewWork() { }

    public WorkKind Kind => WorkKind.Review;

    public MessageUse Receive(string message) => new MessageUse.Guidance(message.Trim());

    /// <param name="context">Its <see cref="NodeContext.Subject"/> is the subject as of now, with its changes read.</param>
    public NodeStep Next(NodeContext context, AttemptRecord? latest) => latest switch
    {
        null => context.Subject is { } subject
            ? new NodeStep.RunTurn(FirstPrompt(context.Node, subject))
            : new NodeStep.Fail("The review has no task to review."),
        { Status: AttemptStatus.Running } => throw new ArgumentException("A turn of this attempt is running.", nameof(latest)),
        { Status: AttemptStatus.Succeeded } => new NodeStep.Finish(latest.Result),
        { Status: AttemptStatus.InReview } => Between(context, latest),
        _ => new NodeStep.Fail(latest.Detail ?? $"The review ended {latest.Status}."),
    };

    /// <summary>A reviewer turn ended. The latest round decides: repair the verdict, finish, fix, wait, or review the fix.</summary>
    private static NodeStep Between(NodeContext context, AttemptRecord review)
    {
        var ledger = ReviewLedger.Fold(review);
        var round = ledger.Rounds[^1];
        if (round.Verdict is null)
        {
            return round.Repaired
                ? new NodeStep.Fail($"iDevelop could not read the reviewer's verdict, even after asking again. {round.Problem}")
                : new NodeStep.RunTurn(
                    $"iDevelop could not read the verdict at the end of your last message. {round.Problem} Reply with only the verdict block.\n\n{VerdictContract}\n");
        }

        if (round.Verdict.Approves)
        {
            return new NodeStep.Finish(review.Result);
        }

        if (context.Subject is not { } subject)
        {
            return new NodeStep.Fail("The task under review is no longer in the workflow.");
        }

        if (subject.Latest is not { Fix: { } link } fix || link.Attempt != review.Id || link.Round != round.Number)
        {
            return subject.Latest is { Status: AttemptStatus.Running or AttemptStatus.WaitingForInput }
                ? new NodeStep.WaitForSubject()
                : new NodeStep.FixRound(FixPrompt(context.Node, subject, review, ledger), round.Number, review.Guidance.Count, subject.CanResume);
        }

        return fix.Status switch
        {
            AttemptStatus.Running or AttemptStatus.WaitingForInput => new NodeStep.WaitForSubject(),
            // Quitting iDevelop interrupts a running fix round. The round goes on in the same session once the project opens.
            AttemptStatus.Interrupted => new NodeStep.FixRound(FixPrompt(context.Node, subject, review, ledger), round.Number, review.Guidance.Count, subject.CanResume),
            AttemptStatus.Succeeded => new NodeStep.RunTurn(RoundPrompt(subject, review, ledger, round.Number, fix))
            {
                Report = new FixReport(fix.Id, fix.Result, link.Guidance),
            },
            _ => new NodeStep.Fail(
                $"\"{subject.Node.Title}\" did not finish fix round {round.Number}. It ended {fix.Status}.{(fix.Detail is { } detail ? $" {detail}" : "")}"),
        };
    }

    /// <summary>The reviewer template with the subject's ticket, its report, and its whole change, then the verdict contract.</summary>
    private static string FirstPrompt(TaskDefinition review, SubjectView subject)
    {
        var rendered = Template(review, ((WorkSpec.Review)review.Blueprint.Work).Reviewer, subject, subject.Change, findings: "");
        return $"{rendered.TrimEnd()}\n\n{VerdictContract}\n";
    }

    /// <summary>
    /// The fix template with the findings that stand, then a repeat notice and the guidance the implementer has not read,
    /// then the fix contract. A fresh session first reads the ticket and its change so far.
    /// </summary>
    private static string FixPrompt(TaskDefinition node, SubjectView subject, AttemptRecord review, ReviewLedger ledger)
    {
        var round = ledger.Round;
        var text = new StringBuilder();
        if (!subject.CanResume)
        {
            text.Append("Your earlier session could not continue, so this one starts fresh. This is the ticket you worked on, and your change so far.\n\n")
                .Append("## The ticket\n\n").Append(AgentWork.Ticket(subject.Node)).Append("\n\n")
                .Append("## Your change so far\n\n").Append(Fenced(subject.Change)).Append("\n\n");
        }

        text.Append(Template(node, ((WorkSpec.Review)node.Blueprint.Work).Fix, subject, subject.Change, Findings(ledger)).TrimEnd()).Append("\n\n");
        if (ledger.Repeats(round - 1) is { } earlier)
        {
            text.Append($"Round {round - 1} repeated the positions of round {earlier}. The reviewer now names the exact change that would settle each finding. Make that change, or answer the reviewer's latest reason for it.\n\n");
        }

        var delivered = ledger.Rounds.Select(each => each.Report?.Guidance).LastOrDefault(count => count is not null) ?? 0;
        AppendGuidance(text, review.Guidance.Skip(delivered));
        return text.Append(FixContract).Append('\n').ToString();
    }

    /// <summary>The subject's report and the change of this round, a repeat notice, and the guidance the reviewer has not read.</summary>
    private static string RoundPrompt(SubjectView subject, AttemptRecord review, ReviewLedger ledger, int round, AttemptRecord fix)
    {
        var text = new StringBuilder()
            .Append($"The implementer answered your findings of round {round}.\n\n")
            .Append("## Its report\n\n").Append(string.IsNullOrWhiteSpace(fix.Result) ? "It reported nothing." : fix.Result.Trim()).Append("\n\n")
            .Append("## The change in this round\n\n").Append(Fenced(subject.LatestChange)).Append("\n\n");
        if (Repeats(review, round, fix) is { } earlier)
        {
            text.Append($"Round {round} repeats the positions of round {earlier}. For each finding that still stands, name the exact change that would settle it, or withdraw it.\n\n");
        }

        AppendGuidance(text, review.Guidance.Where(note => note.AfterTurn >= ledger.Rounds[round - 1].FirstTurn));
        return text.Append(VerdictContract).Append('\n').ToString();
    }

    /// <summary>Whether round <paramref name="round"/>, once the fix's report is in, repeats an earlier round.</summary>
    private static int? Repeats(AttemptRecord review, int round, AttemptRecord fix)
    {
        var next = new TurnRecord(review.Turns.Count + 1, null, TurnOutcome.Running, null) { Report = new FixReport(fix.Id, fix.Result, 0) };
        return ReviewLedger.Fold(review with { Turns = review.Turns.Add(next) }).Repeats(round);
    }

    private static string Template(TaskDefinition node, PromptTemplate template, SubjectView subject, string? change, string findings) =>
        template.Render(name => name switch
        {
            "title" => node.Title,
            "inputs" => "",
            "ticket" => AgentWork.Ticket(subject.Node),
            "report" => subject.Latest?.Result ?? "",
            "change" => Fenced(change),
            "findings" => findings,
            _ => node.Field(name),
        });

    /// <summary>Each finding that stands, with the change that would settle it and the implementer's earlier answer.</summary>
    private static string Findings(ReviewLedger ledger) => string.Join("\n\n", ledger.Standing.Select(finding =>
        $"### Finding {finding.Id}\n\n{finding.Text}\n\nThe change that would settle it: {finding.Change}" +
        (finding.Answer is { } answer ? $"\n\nYour earlier answer: {answer}" : "")));

    private static void AppendGuidance(StringBuilder text, IEnumerable<GuidanceNote> notes)
    {
        var list = notes.ToList();
        if (list.Count > 0)
        {
            text.Append("## Guidance from the person\n\n").Append(string.Join("\n\n", list.Select(note => note.Text))).Append("\n\n");
        }
    }

    private static string Fenced(string? diff) => string.IsNullOrWhiteSpace(diff)
        ? "No file changed."
        : $"```diff\n{diff.TrimEnd()}\n```";
}

/// <summary>A person approves what earlier nodes handed on, or sends it back. No agent runs, so the person cannot write to it.</summary>
public sealed class PersonWork : INodeWork
{
    public static readonly PersonWork Instance = new();

    private PersonWork() { }

    public WorkKind Kind => WorkKind.Person;

    public NodeStep Next(NodeContext context, AttemptRecord? latest) => latest switch
    {
        null => new NodeStep.WaitForPerson(new Pending.Approval()),
        { Status: AttemptStatus.Running } => throw new ArgumentException("A turn of this attempt is running.", nameof(latest)),
        { Status: AttemptStatus.WaitingForInput } => new NodeStep.WaitForPerson(new Pending.Approval()),
        { Status: AttemptStatus.Succeeded } => new NodeStep.Finish(context.Inputs),
        _ => new NodeStep.Fail(latest.Detail ?? "The person sent it back."),
    };
}
