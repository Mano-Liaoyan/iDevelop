using IDevelop.Nodes;

namespace IDevelop.Desktop.Execution;

/// <summary>One finding of a review in the inspector: its state, the reviewer's latest words, and the implementer's.</summary>
public sealed class FindingViewModel(Finding finding)
{
    public string Heading => $"Finding {finding.Id} · {RunText.FindingState(finding.State)}";

    public string Reviewer => finding.Text;

    public string? Change => finding.IsOpen ? $"Settled by: {finding.Change}" : null;

    public string? Implementer => finding.Answer is { } answer ? $"Implementer: {answer}" : null;

    /// <summary>A settled finding fades.</summary>
    public double Emphasis => finding.IsOpen ? 1 : 0.6;
}
