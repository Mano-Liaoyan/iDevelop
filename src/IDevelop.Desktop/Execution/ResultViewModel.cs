using IDevelop.Desktop.Canvas;
using IDevelop.Engine;
using IDevelop.Nodes;

namespace IDevelop.Desktop.Execution;

/// <summary>One row of a node's result history in the inspector: its number, its report's first paragraph, and its commit.</summary>
/// <param name="current">The node's latest result, and nothing it rests on changed since.</param>
public sealed class ResultViewModel(NodeResult result, int number, bool current)
{
    public string Heading => current ? $"Result {number} · Current" : $"Result {number}";

    /// <summary>The report's first paragraph, which the row's tooltip gives whole.</summary>
    public string Summary => CardNow.FirstParagraph(ResultBlock.Prose(result.Report)) ?? "No report.";

    public string Report => ResultBlock.Prose(result.Report);

    /// <summary>"Commit 1a2b3c4" for a node that edits the project, or null.</summary>
    public string? Commit => result.Commit is { Length: >= 7 } commit ? $"Commit {commit[..7]}" : null;
}
