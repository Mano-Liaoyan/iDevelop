using IDevelop.Execution;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// One input an Approval hands on, as its inspector shows it: the input's heading and report as Markdown, or for a review,
/// its heading, its verdict as a status, and every finding it raised with how each was settled, like the Review node's
/// Findings. A review's recorded report, with its raw verdict block, waits behind a disclosure.
/// </summary>
public sealed class GateInputViewModel
{
    private readonly GateInput _input;

    internal GateInputViewModel(GateInput input)
    {
        _input = input;
        Findings = input.Review is { } review ? [.. review.Findings.Select(finding => new FindingViewModel(finding))] : [];
    }

    /// <summary>The input's heading, such as "Review (dependency)": its task's title and connection kind.</summary>
    public string Heading => _input.Heading;

    /// <summary>"## A (dependency)" and the input's report, which a review shows only behind its disclosure.</summary>
    public string Markdown => _input.Markdown;

    public bool IsReview => _input.Review is not null;

    public bool Approves => _input.Review?.Verdict.Approves == true;

    /// <summary>"Approved in round 2", or null for an input that is not a review.</summary>
    public string? Verdict => _input.Review is { } review
        ? $"{(review.Verdict.Approves ? "Approved" : "Changes requested")} in round {review.Round}"
        : null;

    /// <summary>Every finding the review raised, in the order it raised them.</summary>
    public IReadOnlyList<FindingViewModel> Findings { get; }

    /// <summary>Said when a review raised no finding at all, or null.</summary>
    public string? NoFindings => IsReview && Findings.Count == 0 ? "The reviewer raised no findings." : null;

    /// <summary>A review's report as the approval records it: the reviewer's last message with its verdict block.</summary>
    public string? Raw => IsReview ? _input.Report : null;
}
