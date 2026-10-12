using IDevelop.Nodes;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// One finding of a review in the inspector: its state, its severity and location, the reviewer's latest words, the
/// implementer's, and why the reviewer withdrew it. A verdict written before the contract asked for a severity and a
/// location shows neither.
/// </summary>
/// <param name="fades">Whether a settled finding fades, as it does beside the open ones of a review that goes on.</param>
public sealed class FindingViewModel(Finding finding, bool fades = true)
{
    public string Heading => $"Finding {finding.Id} · {RunText.FindingState(finding.State)}";

    /// <summary>"High", "Medium", or "Low", as the reviewer rated it, or null.</summary>
    public string? Severity => finding.Severity is { Length: > 0 } severity ? char.ToUpperInvariant(severity[0]) + severity[1..] : null;

    /// <summary>The file and line the finding concerns, such as "src/Calc.cs:12", or null.</summary>
    public string? Location => finding.Location;

    public string Reviewer => finding.Text;

    public string? Change => finding.IsOpen ? $"Settled by: {finding.Change}" : null;

    public string? Implementer => finding.Answer is { } answer ? $"Implementer: {answer}" : null;

    /// <summary>"Withdrawn: " and the reviewer's reason, or null.</summary>
    public string? Withdrawal => finding.Reason is { } reason ? $"Withdrawn: {reason}" : null;

    /// <summary>A settled finding fades, unless the finding never fades.</summary>
    public double Emphasis => finding.IsOpen || !fades ? 1 : 0.6;
}
