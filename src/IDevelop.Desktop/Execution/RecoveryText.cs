using System.Diagnostics;
using IDevelop.Execution;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// What the Recovery and Updated inputs sections say: the evidence of a block or an unresolved turn, a restoration's
/// preview, a rebase's preview, and why a recovery command recorded nothing.
/// </summary>
internal static class RecoveryText
{
    /// <summary>What the block names in the checkout: paths, the branch, HEAD, and the index lock. Null for a block with no checkout scope.</summary>
    public static string? Paths(MaterializationBlock block) => block.Scope switch
    {
        BlockScope.Checkout { Paths.IsEmpty: true, Branch: false, Head: false, IndexLock: false } => "The whole checkout",
        BlockScope.Checkout checkout => string.Join(", ", new[]
        {
            checkout.Paths.IsEmpty ? null : List(checkout.Paths),
            checkout.Branch ? "the task's branch" : null,
            checkout.Head ? "HEAD" : null,
            checkout.IndexLock ? "index.lock" : null,
        }.OfType<string>()),
        BlockScope.Ownership => "The run's branches",
        BlockScope.Repository => "The repository",
        _ => null,
    };

    /// <summary>The refs a block names, or null.</summary>
    public static string? Refs(MaterializationBlock block) => block.Scope is BlockScope.Refs { Names: var names } ? List(names) : null;

    /// <summary>"Process 4242, started 10:02:11. It no longer runs.", or null without a recorded launch.</summary>
    public static string? Client(TaskEvidence evidence) => evidence.Root is not { } root ? null
        : $"Process {root.Id}, started {root.StartedAt.ToLocalTime():HH:mm:ss}." + evidence.RootNow switch
        {
            ProcessMatch.Same => " It still runs.",
            ProcessMatch.Gone => " It no longer runs.",
            ProcessMatch.Reused => " Its id now belongs to another process.",
            null => "",
            _ => throw new UnreachableException(),
        };

    /// <summary>How the newest turn's client ended, as the journal recorded it.</summary>
    public static string TurnEnd(TaskEvidence evidence) => evidence.Exit?.Exit switch
    {
        null => "No exit was recorded.",
        RootExit.Exited exited => $"The client exited with code {exited.Code}.",
        RootExit.NotStarted notStarted => $"The client did not start: {notStarted.Detail}",
        _ => throw new UnreachableException(),
    };

    /// <summary>The newest turn's turn-end captures, and whether they matched.</summary>
    public static string Captures(TaskEvidence evidence)
    {
        if (evidence.Captures.IsEmpty)
        {
            return "No turn-end capture was recorded.";
        }

        var count = evidence.Captures.Length == 1 ? "1 capture" : $"{evidence.Captures.Length} captures";
        var recovery = evidence.Captures.Any(capture => capture.Recovery) ? ", one taken on recovery" : "";
        return evidence.Dispositions.LastOrDefault()?.Disposition switch
        {
            CaptureDisposition.Matched => $"{count}{recovery}. They match.",
            CaptureDisposition.Diverged diverged => $"{count}{recovery}. They differ{(diverged.Paths.IsEmpty ? "" : " in " + List(diverged.Paths))}.",
            _ => $"{count}{recovery}, not yet compared.",
        };
    }

    /// <summary>How the newest turn's cleanup ended, or null when none ran.</summary>
    public static string? Cleanup(TaskEvidence evidence) => evidence.Cleanup switch
    {
        null => null,
        { Result: CleanupResult.Completed } => "Completed.",
        { Detail: { Length: > 0 } detail } => $"Incomplete: {detail}",
        _ => "Incomplete.",
    };

    /// <summary>"Restore puts the checkout back to the result it handed on."</summary>
    public static string Target(RestorePreview preview) =>
        "Restore puts the checkout back to " + ((preview.Baseline.GetValueOrDefault("branch") is ComponentBaseline.Fixed { Source: RestoreTarget.Result } branch
            ? branch : preview.Baseline.GetValueOrDefault("files")) switch
        {
            ComponentBaseline.Fixed { Source: RestoreTarget.Result } => "the result it handed on.",
            ComponentBaseline.Fixed { Source: RestoreTarget.Capture } => "the end of its turn.",
            ComponentBaseline.Fixed { Source: RestoreTarget.Preparation } => "the start of its attempt.",
            ComponentBaseline.Fixed { Source: RestoreTarget.Recovery } => "its recovery baseline.",
            ComponentBaseline.Fixed { Source: RestoreTarget.Reset } => "its last reset.",
            _ => "its recorded state.",
        });

    /// <summary>Each path Restore moves, and what it does to it.</summary>
    public static IReadOnlyList<string> Moves(RestorePreview preview) => [.. preview.Paths.Select(path => (path.From, path.To) switch
    {
        (null, _) => $"{path.Path} comes back",
        (_, null) => $"{path.Path} is removed",
        _ => $"{path.Path} gets its recorded content back",
    })];

    /// <summary>What else Restore changes: refs, the index lock, and the blocks it clears.</summary>
    public static string Rest(RestorePreview preview)
    {
        var parts = new List<string>();
        if (preview.Paths.IsEmpty && preview.Refs.IsEmpty && preview.IndexLock is null)
        {
            parts.Add("Nothing moves: the checkout matches its baseline.");
        }

        if (!preview.Refs.IsEmpty)
        {
            parts.Add($"It moves {List(preview.Refs)} back.");
        }

        if (preview.IndexLock is not null)
        {
            parts.Add("It removes index.lock, whose bytes stay retained.");
        }

        var cleared = preview.Repairs.Length + preview.Rechecks.Length;
        parts.Add(cleared switch
        {
            0 => "It clears no block.",
            1 => "It clears 1 block.",
            _ => $"It clears {cleared} blocks.",
        });
        return string.Join(" ", parts);
    }

    /// <summary>The note that macOS gives Restore no file identity, so the person moves files by hand there.</summary>
    public const string HandRepair = "On macOS, Restore cannot move files or remove index.lock, because iDevelop cannot read file identities there. " +
        "Change them by hand, then preserve again so Restore can check the checkout.";

    /// <summary>Why a rebase preview shows no clean candidate, or what the clean one changes.</summary>
    public static string Candidate(RebaseCandidate candidate) => candidate switch
    {
        RebaseCandidate.Clean { Updates.IsEmpty: true } => "Its change replays cleanly, and its checkout keeps its files.",
        RebaseCandidate.Clean clean => $"Its change replays cleanly. Its checkout gets {List(clean.Updates)} from the newer inputs.",
        RebaseCandidate.Conflicted { Paths.IsEmpty: true } conflicted => $"Its change does not replay cleanly. {conflicted.Detail}",
        RebaseCandidate.Conflicted conflicted => $"Its change conflicts in {List(conflicted.Paths)}. {conflicted.Detail}",
        RebaseCandidate.Unavailable unavailable => unavailable.Detail,
        _ => throw new UnreachableException(),
    };

    /// <summary>One sentence for a refusal of a recovery or rebase command.</summary>
    public static string Problem(RunRejection rejection) => rejection.Problem switch
    {
        RunProblem.UnresolvedOwnership => "A turn of this checkout is still unresolved. Close it as stopped first.",
        RunProblem.InvalidData => "The preservation is gone. Preserve the checkout again.",
        RunProblem.EvidenceMismatch => "The checkout or the inputs changed since the preview. Check them again.",
        RunProblem.ConfirmationRequired => "Say why you close it.",
        RunProblem.InvalidClaim => "This turn is no longer unresolved.",
        RunProblem.StartConflict => "A later attempt changed this task's checkout, so it cannot be rebased.",
        RunProblem.ReplacementConflict => "Another choice already replaced this fix round.",
        RunProblem.SessionUnavailable => "The fix's session cannot go on. Retry the fix instead.",
        _ => WorkflowRunText.Problem(rejection),
    };

    /// <summary>"a.txt, b.txt, and 3 more".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        <= 4 => string.Join(", ", items),
        _ => $"{string.Join(", ", items.Take(3))}, and {items.Count - 3} more",
    };
}
