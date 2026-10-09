using System.Collections.Immutable;

namespace IDevelop.Execution;

/// <summary>
/// The paragraph that ends the first prompt of every task that may edit the project in a run: where to declare its
/// artifacts, and which of its earlier result's artifacts it keeps. It is iDevelop's own instruction to the agent, so the
/// conversation folds it away from the person's reading.
/// </summary>
internal static class ArtifactInstructions
{
    private const string Opening = "Declare artifacts in ";

    // Every outbox sits under this folder, as RunLayout.Outbox names it.
    private const string Marker = Opening + ".idp/outbox/";

    public static string For(AttemptId attempt, ImmutableArray<ArtifactRecord> kept)
    {
        var text = Opening + RunLayout.Outbox(attempt) +
            "/manifest.json using {\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}. " +
            "Artifact paths are relative to that folder.";
        return kept.IsDefaultOrEmpty
            ? text
            : text + " Your earlier result's artifacts stay with your new result unless you declare one with the same name: " +
                string.Join(", ", kept.Select(artifact => artifact.Name)) + ".";
    }

    /// <summary>
    /// The prompt without the artifact paragraph that ends it, and that paragraph, which opens as <see cref="For"/> does
    /// with an outbox under <c>.idp/outbox/</c>. A paragraph like it anywhere else stays in the text.
    /// </summary>
    public static (string Text, string? Instructions) Split(string prompt)
    {
        var start = prompt.LastIndexOf("\n\n", StringComparison.Ordinal);
        var last = start < 0 ? prompt : prompt[(start + 2)..];
        return last.StartsWith(Marker, StringComparison.Ordinal) && start >= 0 ? (prompt[..start], last) : (prompt, null);
    }
}
