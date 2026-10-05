using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Execution;

namespace IDevelop.Nodes;

/// <summary>
/// Where a finding stands. Open, Fixed, and Disputed wait for the reviewer. Resolved and Withdrawn are settled: the
/// reviewer accepted the fix or dropped the finding.
/// </summary>
public enum FindingState { Open, Fixed, Disputed, Resolved, Withdrawn }

/// <summary>
/// One finding with both sides' latest words. <see cref="Text"/> is the reviewer's: what is wrong, why it still stands, or
/// why it was withdrawn. <see cref="Answer"/> is the implementer's latest answer, if any.
/// </summary>
public sealed record Finding(string Id, FindingState State, string Text, string Change, string? Answer, int Raised)
{
    public bool IsOpen => State is FindingState.Open or FindingState.Fixed or FindingState.Disputed;
}

/// <summary>A finding as the reviewer lists it in a verdict: still standing, with the change that would settle it.</summary>
public sealed record Standing(string Id, string Text, string Change);

/// <summary>The reviewer's verdict block. Approval lists no finding, and a request for changes lists at least one.</summary>
public sealed record Verdict(bool Approves, ImmutableArray<Standing> Findings, ImmutableArray<(string Id, string Reason)> Withdrawn);

/// <summary>The implementer's answer to one finding: fixed, or disputed with a reason.</summary>
public sealed record FindingAnswer(string Id, bool Disputes, string Note);

/// <summary>
/// One round: the reviewer's verdict, after at most one repair turn, then the subject's report of its fix.
/// <see cref="Position"/> is what each side held once the report arrived, which the repeat check compares.
/// </summary>
public sealed record ReviewRound(int Number, int FirstTurn, Verdict? Verdict, string? Problem, bool Repaired, FixReport? Report, string? Position);

/// <summary>
/// The findings ledger, folded from a review attempt alone: each reviewer turn's verdict and each fix round's report,
/// which the reviewer's next turn carries. It is a record and a view. The agents talk through their own sessions.
/// </summary>
public sealed record ReviewLedger(ImmutableArray<ReviewRound> Rounds, ImmutableArray<Finding> Findings)
{
    public static readonly ReviewLedger Empty = new([], []);

    /// <summary>The round the review is in, from 1, or 0 before the first verdict.</summary>
    public int Round => Rounds.Length;

    public int OpenCount => Findings.Count(finding => finding.IsOpen);

    /// <summary>The ids the latest verdict keeps standing.</summary>
    public ImmutableArray<Finding> Standing => Rounds.LastOrDefault()?.Verdict is { } verdict
        ? [.. verdict.Findings.Select(standing => Findings.First(finding => finding.Id == standing.Id))]
        : [];

    public static ReviewLedger Fold(AttemptRecord review)
    {
        var rounds = new List<ReviewRound>();
        var findings = new List<Finding>();
        foreach (var turn in review.Turns)
        {
            if (turn.Number == 1 || turn.Report is not null)
            {
                if (turn.Report is { } report && rounds.Count > 0)
                {
                    var answered = rounds[^1];
                    Answer(findings, answered.Verdict, Answers(report.Text));
                    rounds[^1] = answered with { Report = report, Position = Position(findings, answered.Verdict) };
                }

                rounds.Add(new ReviewRound(rounds.Count + 1, turn.Number, null, null, false, null, null));
            }
            else
            {
                rounds[^1] = rounds[^1] with { Repaired = true };
            }

            if (turn.Outcome == TurnOutcome.Succeeded)
            {
                rounds[^1] = ReadVerdict(turn.FinalText) switch
                {
                    (Verdict verdict, _) => Judge(findings, rounds[^1] with { Verdict = verdict, Problem = null }),
                    (_, var problem) => rounds[^1] with { Problem = problem },
                };
            }
        }

        return new ReviewLedger([.. rounds], [.. findings]);
    }

    /// <summary>The earliest round before <paramref name="round"/> whose positions it repeats, or null.</summary>
    public int? Repeats(int round) =>
        round >= 1 && round <= Rounds.Length && Rounds[round - 1].Position is { Length: > 0 } position
            ? Rounds.Take(round - 1).FirstOrDefault(earlier => earlier.Position == position)?.Number
            : null;

    /// <summary>The verdict at the end of a reviewer's final message, or why it cannot be read.</summary>
    public static (Verdict? Verdict, string? Problem) ReadVerdict(string? finalText)
    {
        if (ResultBlock.Read(finalText) is not ResultBlock.Readable block)
        {
            return (null, ResultBlock.Read(finalText) is ResultBlock.Unreadable unreadable
                ? unreadable.Problem
                : "The message does not end with a verdict block.");
        }

        if (block.Status != "verdict")
        {
            return (null, "The block's status is not \"verdict\".");
        }

        var approves = block.Text("verdict") switch
        {
            "approve" => true,
            "changes" => false,
            _ => (bool?)null,
        };
        if (approves is null)
        {
            return (null, "The verdict is neither \"approve\" nor \"changes\".");
        }

        var standing = ImmutableArray.CreateBuilder<Standing>();
        foreach (var item in Items(block.Value, "findings"))
        {
            if (Id(item) is not { } id)
            {
                return (null, "A finding has no id.");
            }

            if (Text(item, "text") is not { } text || Text(item, "change") is not { } change)
            {
                return (null, $"Finding {id} needs its text and the change that would settle it.");
            }

            if (standing.Any(other => other.Id == id))
            {
                return (null, $"Finding {id} appears twice.");
            }

            standing.Add(new Standing(id, text, change));
        }

        var withdrawn = Items(block.Value, "withdrawn")
            .Select(item => (Id: Id(item), Reason: Text(item, "reason") ?? ""))
            .Where(item => item.Id is not null && standing.All(other => other.Id != item.Id))
            .Select(item => (item.Id!, item.Reason))
            .ToImmutableArray();
        return (approves.Value, standing.Count) switch
        {
            (true, > 0) => (null, "The verdict approves but lists findings that still stand."),
            (false, 0) => (null, "The verdict asks for changes but lists no finding."),
            _ => (new Verdict(approves.Value, standing.ToImmutable(), withdrawn), null),
        };
    }

    /// <summary>The implementer's answers at the end of its report. A report without a readable block answers nothing.</summary>
    public static ImmutableArray<FindingAnswer> Answers(string? report) =>
        ResultBlock.Read(report) is ResultBlock.Readable { Status: "answers" } block
            ? [.. Items(block.Value, "answers")
                .Select(item => (Id: Id(item), Answer: Text(item, "answer"), Note: Text(item, "note") ?? ""))
                .Where(item => item.Id is not null && item.Answer is "fixed" or "disputed")
                .Select(item => new FindingAnswer(item.Id!, item.Answer == "disputed", item.Note))]
            : [];

    /// <summary>
    /// Applies a verdict. A listed finding stands, with the reviewer's latest words. A withdrawn finding is settled with its
    /// reason. Any other open finding is settled: withdrawn if the implementer disputed it, otherwise resolved.
    /// </summary>
    private static ReviewRound Judge(List<Finding> findings, ReviewRound round)
    {
        var verdict = round.Verdict!;
        foreach (var standing in verdict.Findings)
        {
            var index = findings.FindIndex(finding => finding.Id == standing.Id);
            var finding = new Finding(standing.Id, FindingState.Open, standing.Text, standing.Change, index < 0 ? null : findings[index].Answer, index < 0 ? round.Number : findings[index].Raised);
            if (index < 0)
            {
                findings.Add(finding);
            }
            else
            {
                findings[index] = finding;
            }
        }

        for (var index = 0; index < findings.Count; index++)
        {
            var finding = findings[index];
            if (!finding.IsOpen || verdict.Findings.Any(standing => standing.Id == finding.Id))
            {
                continue;
            }

            var reason = verdict.Withdrawn.FirstOrDefault(withdrawal => withdrawal.Id == finding.Id).Reason;
            findings[index] = finding.State == FindingState.Disputed || reason is not null
                ? finding with { State = FindingState.Withdrawn, Text = string.IsNullOrWhiteSpace(reason) ? finding.Text : reason }
                : finding with { State = FindingState.Resolved };
        }

        return round;
    }

    private static void Answer(List<Finding> findings, Verdict? verdict, ImmutableArray<FindingAnswer> answers)
    {
        foreach (var answer in answers)
        {
            var index = findings.FindIndex(finding => finding.Id == answer.Id && finding.State == FindingState.Open);
            if (index >= 0 && verdict is not null && verdict.Findings.Any(standing => standing.Id == answer.Id))
            {
                findings[index] = findings[index] with { State = answer.Disputes ? FindingState.Disputed : FindingState.Fixed, Answer = answer.Note };
            }
        }
    }

    /// <summary>Each finding the verdict kept standing, with the implementer's answer to it, in id order.</summary>
    private static string? Position(List<Finding> findings, Verdict? verdict) => verdict is null
        ? null
        : string.Join(" ", verdict.Findings
            .Select(standing => findings.First(finding => finding.Id == standing.Id))
            .OrderBy(finding => finding.Id, StringComparer.Ordinal)
            .Select(finding => $"{finding.Id}:{finding.State}"));

    private static IEnumerable<JsonElement> Items(JsonElement block, string property) =>
        block.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    /// <summary>A string id, or a number written as one, since agents number their findings.</summary>
    private static string? Id(JsonElement item) => item.TryGetProperty("id", out var id) ? id.ValueKind switch
    {
        JsonValueKind.String when !string.IsNullOrWhiteSpace(id.GetString()) => id.GetString()!.Trim(),
        JsonValueKind.Number => id.GetRawText(),
        _ => null,
    } : null;

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
