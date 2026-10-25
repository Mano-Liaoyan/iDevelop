using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>
/// A review: a back-and-forth between two sessions until both agents agree. The reviewer's turns are the review
/// attempt's turns, in one session that only reads. Each fix round is a new attempt of an implementer whose first turn
/// resumes its session: the subject's, or for a Merge node, one for each implementer behind it that its findings go to.
/// Each side reads its own history plus the other side's short message. The loop ends
/// when the reviewer approves with no finding left standing. A round that repeats an earlier round's positions waits for
/// the person's guidance before the next one starts.
/// </summary>
public sealed class ReviewWork : IConverses
{
    public static readonly ReviewWork Instance = new();

    /// <summary>What every reviewer turn ends with, which no blueprint can remove.</summary>
    internal const string VerdictContract =
        "End your final message with your verdict in this block, and nothing after it:\n\n" +
        "```idevelop\n{\"status\": \"verdict\", \"verdict\": \"changes\", " +
        "\"findings\": [{\"id\": \"1\", \"severity\": \"high\", \"location\": \"src/Calc.cs:12\", \"text\": \"what is wrong\", " +
        "\"change\": \"the exact change that would settle it\"}], \"withdrawn\": []}\n```\n\n" +
        "Write \"approve\" with no findings when nothing stands. Otherwise write \"changes\" and list every finding that " +
        "still stands, each with the exact change that would settle it. Give each finding a severity, \"high\", \"medium\", or \"low\", " +
        "and its location, the file and line it concerns, such as src/Calc.cs:12. " +
        "Keep the id of a finding you raised before, and give a new finding an id you have not used. When " +
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

        if (subject.Node.Blueprint.Work is WorkSpec.Merge)
        {
            return Behind(context.Node, subject, review, ledger, round);
        }

        if (subject.Latest is not { Fix: { } link } fix || link.Attempt != review.Id || link.Round != round.Number)
        {
            return subject.Latest is { Status: AttemptStatus.Running or AttemptStatus.WaitingForInput }
                ? new NodeStep.WaitForSubject()
                : Round(context.Node, subject, review, ledger, review.Guidance.Count, subject.CanResume, choice: null);
        }

        return fix.Status switch
        {
            AttemptStatus.Running or AttemptStatus.WaitingForInput => new NodeStep.WaitForSubject(),
            // Quitting iDevelop interrupts a running fix round. It never goes on by itself: the person chooses how.
            AttemptStatus.Interrupted => new NodeStep.ChooseFix(round.Number),
            // A round that repeats an earlier one waits for the person, whose guidance after it resumes the loop (ADR 0040).
            AttemptStatus.Succeeded when Repeats(review, round.Number, fix.Result) is { } earlier && !GuidedSince(review, fix) =>
                new NodeStep.WaitForGuidance(round.Number, earlier),
            AttemptStatus.Succeeded => new NodeStep.RunTurn(RoundPrompt(subject, review, ledger, round.Number, fix))
            {
                Report = new FixReport(fix.Id, fix.Result, link.Guidance),
            },
            _ => new NodeStep.Fail(
                $"\"{subject.Node.Title}\" did not finish fix round {round.Number}. It ended {fix.Status}.{(fix.Detail is { } detail ? $" {detail}" : "")}"),
        };
    }

    /// <summary>
    /// A review of a Merge node between rounds (ADR 0041). Each finding goes to the implementers behind the merge by file,
    /// each implementer fixes its own in its own fix attempt, and once all of them ended and the Merge node merged their
    /// new work, the reviewer reads the new merge with every implementer's report.
    /// </summary>
    private static NodeStep Behind(TaskDefinition node, SubjectView subject, AttemptRecord review, ReviewLedger ledger, ReviewRound round)
    {
        if (subject.Implementers.IsEmpty)
        {
            return new NodeStep.Fail($"\"{subject.Node.Title}\" has no implementer before it to fix the findings.");
        }

        if (subject.Implementers.Any(implementer => implementer.Latest is { Status: AttemptStatus.Running or AttemptStatus.WaitingForInput }))
        {
            return new NodeStep.WaitForSubject();
        }

        var fixes = Fixes(subject, review, ledger, round.Number);
        if (fixes.Any(fix => fix.Fix is { Status: AttemptStatus.Interrupted }))
        {
            return new NodeStep.ChooseFix(round.Number);
        }

        if (fixes.FirstOrDefault(fix => fix.Fix is null) is { } missing)
        {
            return Round(node, missing.Implementer, review, ledger, missing.Findings, review.Guidance.Count, missing.Implementer.CanResume, choice: null);
        }

        if (fixes.FirstOrDefault(fix => fix.Fix!.Status != AttemptStatus.Succeeded) is { } failed)
        {
            var end = failed.Fix!;
            return new NodeStep.Fail(
                $"\"{failed.Implementer.Node.Title}\" did not finish fix round {round.Number}. It ended {end.Status}.{(end.Detail is { } detail ? $" {detail}" : "")}");
        }

        // The Merge node merges the implementers' new work by its own step, and the reviewer reads that merge.
        if (!subject.Merged)
        {
            return new NodeStep.WaitForSubject();
        }

        var report = Combined(fixes);
        var ended = fixes.All(fix => fix.Fix!.EndedAt is not null) ? fixes.Max(fix => fix.Fix!.EndedAt) : null;
        if (Repeats(review, round.Number, report) is { } earlier && !review.Guidance.Any(note => ended is not { } at || note.At >= at))
        {
            return new NodeStep.WaitForGuidance(round.Number, earlier);
        }

        return new NodeStep.RunTurn(MergedRoundPrompt(subject, review, ledger, round.Number, fixes, report))
        {
            Report = new FixReport(fixes[^1].Fix!.Id, report, fixes.Min(fix => fix.Fix!.Fix!.Guidance)),
        };
    }

    /// <summary>One implementer's part of a round: the findings it gets, and its fix attempt of the round, if any.</summary>
    private sealed record ImplementerFix(SubjectView Implementer, ImmutableArray<Finding> Findings, AttemptRecord? Fix);

    /// <summary>The implementers that get at least one finding of the round, in task order, with their fixes of it.</summary>
    private static ImmutableArray<ImplementerFix> Fixes(SubjectView subject, AttemptRecord review, ReviewLedger ledger, int round)
    {
        var routed = FindingRouting.Route(ledger.Standing, [.. subject.Implementers.Select(implementer => implementer.Touched)]);
        return [.. subject.Implementers.Select((implementer, index) => new ImplementerFix(implementer, routed[index],
                implementer.Latest is { Fix: { } link } fix && link.Attempt == review.Id && link.Round == round ? fix : null))
            .Where(fix => !fix.Findings.IsEmpty)];
    }

    /// <summary>
    /// Every implementer's report, without its answers block, then one answers block for the ledger: each finding's answers,
    /// disputed when any implementer disputes it.
    /// </summary>
    private static string Combined(ImmutableArray<ImplementerFix> fixes)
    {
        var text = new StringBuilder();
        var answers = new List<(string Id, bool Disputes, List<string> Notes)>();
        foreach (var fix in fixes)
        {
            var prose = ResultBlock.Prose(fix.Fix!.Result);
            text.Append($"### {fix.Implementer.Node.Title}\n\n").Append(string.IsNullOrWhiteSpace(prose) ? "It reported nothing." : prose.Trim()).Append("\n\n");
            foreach (var answer in ReviewLedger.Answers(fix.Fix.Result).Where(answer => fix.Findings.Any(finding => finding.Id == answer.Id)))
            {
                var note = fixes.Count(other => other.Findings.Any(finding => finding.Id == answer.Id)) > 1
                    ? $"{fix.Implementer.Node.Title}: {answer.Note}" : answer.Note;
                var index = answers.FindIndex(known => known.Id == answer.Id);
                if (index < 0)
                {
                    answers.Add((answer.Id, answer.Disputes, [note]));
                }
                else
                {
                    answers[index].Notes.Add(note);
                    answers[index] = answers[index] with { Disputes = answers[index].Disputes || answer.Disputes };
                }
            }
        }

        var block = JsonSerializer.Serialize(new
        {
            status = "answers",
            answers = answers.Select(answer => new { id = answer.Id, answer = answer.Disputes ? "disputed" : "fixed", note = string.Join("\n", answer.Notes) }),
        });
        return text.Append("```idevelop\n").Append(block).Append("\n```").ToString();
    }

    /// <summary>
    /// The reviewer's prompt after the implementers' fixes: each one's report, the new merge's change since the merge the
    /// reviewer read, a repeat notice, and the guidance the reviewer has not read.
    /// </summary>
    private static string MergedRoundPrompt(SubjectView subject, AttemptRecord review, ReviewLedger ledger, int round,
        ImmutableArray<ImplementerFix> fixes, string report)
    {
        var text = new StringBuilder()
            .Append($"The implementers answered your findings of round {round}, and \"{subject.Node.Title}\" merged their work again.\n\n")
            .Append("## Their reports\n\n");
        foreach (var fix in fixes)
        {
            text.Append($"### {fix.Implementer.Node.Title}\n\n")
                .Append(string.IsNullOrWhiteSpace(fix.Fix!.Result) ? "It reported nothing." : fix.Fix.Result.Trim()).Append("\n\n");
        }

        text.Append("## The change in this round\n\n").Append(Fenced(subject.LatestChange)).Append("\n\n");
        if (Repeats(review, round, report) is { } earlier)
        {
            text.Append($"Round {round} repeats the positions of round {earlier}. For each finding that still stands, name the exact change that would settle it, or withdraw it.\n\n");
        }

        AppendGuidance(text, review.Guidance.Where(note => note.AfterTurn >= ledger.Rounds[round - 1].FirstTurn));
        return text.Append(VerdictContract).Append('\n').ToString();
    }

    /// <summary>
    /// The fix attempt of the review's latest round that waits for the person's Continue fix or Retry fix, or null. For a
    /// Merge node, it is the first implementer's in task order.
    /// </summary>
    public AttemptRecord? Interrupted(NodeContext context, AttemptRecord review)
    {
        if (context.Subject is not { } subject || Next(context, review) is not NodeStep.ChooseFix choose)
        {
            return null;
        }

        return subject.Node.Blueprint.Work is WorkSpec.Merge
            ? Fixes(subject, review, ReviewLedger.Fold(review), choose.Round).FirstOrDefault(fix => fix.Fix is { Status: AttemptStatus.Interrupted })?.Fix
            : subject.Latest;
    }

    /// <summary>
    /// Every fix round the review's step would start now, one per implementer that has none yet, or the subject's one. Empty
    /// when its step is no fix round.
    /// </summary>
    public ImmutableArray<NodeStep.FixRound> Rounds(NodeContext context, AttemptRecord review)
    {
        if (Next(context, review) is not NodeStep.FixRound first)
        {
            return [];
        }

        if (context.Subject is not { Node.Blueprint.Work: WorkSpec.Merge } subject)
        {
            return [first];
        }

        var ledger = ReviewLedger.Fold(review);
        return [.. Fixes(subject, review, ledger, ledger.Round).Where(fix => fix.Fix is null)
            .Select(fix => Round(context.Node, fix.Implementer, review, ledger, fix.Findings, review.Guidance.Count, fix.Implementer.CanResume, choice: null))];
    }

    /// <summary>
    /// The fix round that the person's choice starts after closing iDevelop interrupted it, or null when the review waits
    /// for no choice. Continue resumes the interrupted fix's session, which the caller checks with
    /// <see cref="SubjectView.CanResume"/>; Retry starts a fresh one. Both keep the review, the round, and the guidance count.
    /// </summary>
    public NodeStep.FixRound? Recover(NodeContext context, AttemptRecord review, FixChoice choice)
    {
        if (context.Subject is not { } subject || Interrupted(context, review) is not { } fix)
        {
            return null;
        }

        var resumes = choice == FixChoice.Continue;
        return subject.Node.Blueprint.Work is WorkSpec.Merge
            ? Round(context, review, review.Guidance.Count, resumes, choice, fix.Task)
            : Round(context.Node, subject, review, ReviewLedger.Fold(review), review.Guidance.Count, resumes, choice);
    }

    /// <summary>
    /// The fix round of the review's latest round with the guidance notes up to <paramref name="guidance"/>, as a run
    /// rebuilds a reserved round's prompt from its recorded link. <paramref name="choice"/> names the person's choice after
    /// an interruption, which only changes how the prompt begins. For a Merge node, <paramref name="implementer"/> names
    /// the implementer it goes to, with only its findings.
    /// </summary>
    public NodeStep.FixRound Round(NodeContext context, AttemptRecord review, int guidance, bool resumes, FixChoice? choice, TaskId? implementer = null)
    {
        var subject = context.Subject ?? throw new ArgumentException("A fix round needs the review's subject.", nameof(context));
        var ledger = ReviewLedger.Fold(review);
        if (subject.Node.Blueprint.Work is not WorkSpec.Merge)
        {
            return Round(context.Node, subject, review, ledger, guidance, resumes, choice);
        }

        var fix = Fixes(subject, review, ledger, ledger.Round).FirstOrDefault(fix => fix.Implementer.Node.Id == implementer) ??
            throw new ArgumentException("The implementer gets no finding of this round.", nameof(implementer));
        return Round(context.Node, fix.Implementer, review, ledger, fix.Findings, guidance, resumes, choice);
    }

    private static NodeStep.FixRound Round(TaskDefinition node, SubjectView subject, AttemptRecord review, ReviewLedger ledger, int guidance,
        bool resumes, FixChoice? choice) =>
        new(FixPrompt(node, subject, review, ledger, ledger.Standing, guidance, resumes, choice), ledger.Round, guidance, resumes);

    private static NodeStep.FixRound Round(TaskDefinition node, SubjectView implementer, AttemptRecord review, ReviewLedger ledger,
        ImmutableArray<Finding> findings, int guidance, bool resumes, FixChoice? choice) =>
        new(FixPrompt(node, implementer, review, ledger, findings, guidance, resumes, choice), ledger.Round, guidance, resumes)
        {
            Implementer = implementer.Node.Id,
        };

    /// <summary>The reviewer template with the subject's ticket, its report, and its whole change, then the verdict contract.</summary>
    private static string FirstPrompt(TaskDefinition review, SubjectView subject)
    {
        var rendered = Template(review, ((WorkSpec.Review)review.Blueprint.Work).Reviewer, subject, subject.Change, findings: "");
        return $"{rendered.TrimEnd()}\n\n{VerdictContract}\n";
    }

    /// <summary>
    /// The fix template with the findings that stand, then a repeat notice and the guidance up to <paramref name="guidance"/>
    /// that the implementer has not read, then the fix contract. A fresh session first reads the ticket and its change so far.
    /// </summary>
    private static string FixPrompt(TaskDefinition node, SubjectView subject, AttemptRecord review, ReviewLedger ledger, ImmutableArray<Finding> findings,
        int guidance,
        bool resumes, FixChoice? choice)
    {
        var round = ledger.Round;
        var text = new StringBuilder();
        if (!resumes)
        {
            text.Append(choice == FixChoice.Retry
                    ? "You retry this fix round in a fresh session. This is the ticket you worked on, and your change so far.\n\n"
                    : subject.Latest is null
                        ? "You work on these findings in a fresh session. This is the ticket, and the change so far.\n\n"
                        : "Your earlier session could not continue, so this one starts fresh. This is the ticket you worked on, and your change so far.\n\n")
                .Append("## The ticket\n\n").Append(Ticket(subject.Node)).Append("\n\n")
                .Append("## Your change so far\n\n").Append(Fenced(subject.Change)).Append("\n\n");
        }
        else if (choice == FixChoice.Continue)
        {
            text.Append("Closing iDevelop interrupted your work on these findings. Go on from where you stopped.\n\n");
        }

        text.Append(Template(node, ((WorkSpec.Review)node.Blueprint.Work).Fix, subject, subject.Change, Findings(findings)).TrimEnd()).Append("\n\n");
        if (ledger.Repeats(round - 1) is { } earlier)
        {
            text.Append($"Round {round - 1} repeated the positions of round {earlier}. The reviewer now names the exact change that would settle each finding. Make that change, or answer the reviewer's latest reason for it.\n\n");
        }

        var delivered = ledger.Rounds.Select(each => each.Report?.Guidance).LastOrDefault(count => count is not null) ?? 0;
        AppendGuidance(text, review.Guidance.Take(guidance).Skip(delivered));
        return text.Append(FixContract).Append('\n').ToString();
    }

    /// <summary>The subject's report and the change of this round, a repeat notice, and the guidance the reviewer has not read.</summary>
    private static string RoundPrompt(SubjectView subject, AttemptRecord review, ReviewLedger ledger, int round, AttemptRecord fix)
    {
        var text = new StringBuilder()
            .Append($"The implementer answered your findings of round {round}.\n\n")
            .Append("## Its report\n\n").Append(string.IsNullOrWhiteSpace(fix.Result) ? "It reported nothing." : fix.Result.Trim()).Append("\n\n")
            .Append("## The change in this round\n\n").Append(Fenced(subject.LatestChange)).Append("\n\n");
        if (Repeats(review, round, fix.Result) is { } earlier)
        {
            text.Append($"Round {round} repeats the positions of round {earlier}. For each finding that still stands, name the exact change that would settle it, or withdraw it.\n\n");
        }

        AppendGuidance(text, review.Guidance.Where(note => note.AfterTurn >= ledger.Rounds[round - 1].FirstTurn));
        return text.Append(VerdictContract).Append('\n').ToString();
    }

    /// <summary>Whether round <paramref name="round"/>, once the fix's report is in, repeats an earlier round.</summary>
    private static int? Repeats(AttemptRecord review, int round, string? report)
    {
        var next = new TurnRecord(review.Turns.Count + 1, null, TurnOutcome.Running, null) { Report = new FixReport(default, report, 0) };
        return ReviewLedger.Fold(review with { Turns = review.Turns.Add(next) }).Repeats(round);
    }

    /// <summary>Whether the person added guidance once <paramref name="fix"/> ended its round.</summary>
    private static bool GuidedSince(AttemptRecord review, AttemptRecord fix) =>
        review.Guidance.Any(note => fix.EndedAt is not { } ended || note.At >= ended);

    private static string Template(TaskDefinition node, PromptTemplate template, SubjectView subject, string? change, string findings) =>
        template.Render(name => name switch
        {
            "title" => node.Title,
            "inputs" => "",
            "ticket" => Ticket(subject.Node),
            "report" => subject.Latest?.Result ?? subject.Report ?? "",
            "change" => Fenced(change),
            "findings" => findings,
            _ => node.Field(name),
        });

    /// <summary>What the subject was asked to do: an agent's rendered template, or a Merge node's merge with its instructions.</summary>
    private static string Ticket(TaskDefinition subject) => subject.Blueprint.Work is WorkSpec.Merge
        ? $"# {subject.Title}\n\nMerge the work of the tasks before this one into one change." +
            (subject.Fields.GetValueOrDefault("instructions") is { } instructions && !string.IsNullOrWhiteSpace(instructions)
                ? $"\n\n## Instructions\n\n{instructions.Trim()}" : "")
        : AgentWork.Ticket(subject);

    /// <summary>Each finding that stands, with the change that would settle it and the implementer's earlier answer.</summary>
    private static string Findings(ImmutableArray<Finding> findings) => string.Join("\n\n", findings.Select(finding =>
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
