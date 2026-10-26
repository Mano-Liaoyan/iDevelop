using IDevelop.Nodes;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// One finding of a review's verdict in the inspector: its severity and location, the reviewer's words, and the change
/// that would settle it.
/// </summary>
public sealed class FindingViewModel(Standing finding)
{
    public string Heading => $"Finding {finding.Id}";

    /// <summary>"High", "Medium", or "Low", as the reviewer rated it, or null.</summary>
    public string? Severity => finding.Severity is { Length: > 0 } severity ? char.ToUpperInvariant(severity[0]) + severity[1..] : null;

    /// <summary>The file and line the finding concerns, such as "src/Calc.cs:12", or null.</summary>
    public string? Location => finding.Location;

    public string Reviewer => finding.Text;

    public string? Change => finding.Change is { Length: > 0 } change ? $"Settled by: {change}" : null;
}
