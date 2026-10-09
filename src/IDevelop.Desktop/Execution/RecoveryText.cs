using System.Diagnostics;
using IDevelop.Execution;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// What the Recovery and Updated inputs sections say: the evidence of a block or an unresolved turn, a restoration's
/// preview, a rebase's preview, and why a recovery command recorded nothing. Lists are never cut short: every path, ref,
/// and artifact shows, one per line, in a box that scrolls.
/// </summary>
internal static class RecoveryText
{
    /// <summary>What went wrong, in one sentence.</summary>
    public static string Problem(MaterializationBlock block) => (block.Problem, block.Scope) switch
    {
        (MaterializationProblem.UncertainOwnership, BlockScope.Refs) => "A shared ref changed that no task of the run changed.",
        (MaterializationProblem.SubmoduleUnavailable, _) => "A submodule of the checkout cannot be checked out.",
        (MaterializationProblem.JoinRequired, _) => "Its inputs need a join, which this run cannot build.",
        (MaterializationProblem.NotRepositoryRoot, _) => "The project folder is not the root of its Git repository.",
        (MaterializationProblem.GitFailed, _) => "A Git command failed.",
        (MaterializationProblem.ArtifactCollision, _) => "Two inputs hand on different artifacts with one name.",
        (var problem, _) => WorkflowRunText.Problem(problem),
    };

    /// <summary>
    /// The paths a conflict names, or what the block names in the checkout: paths, the branch, HEAD, and the index lock,
    /// one per line. Null for a block with neither.
    /// </summary>
    public static string? Paths(MaterializationBlock block) => block.Conflict is { Paths.IsEmpty: false } conflict ? Lines(conflict.Paths) : block.Scope switch
    {
        BlockScope.Checkout { Paths.IsEmpty: true, Branch: false, Head: false, IndexLock: false } => "The whole checkout",
        BlockScope.Checkout checkout => Lines([.. checkout.Paths, .. new[]
        {
            checkout.Branch ? "the task's branch" : null,
            checkout.Head ? "HEAD" : null,
            checkout.IndexLock ? "index.lock" : null,
        }.OfType<string>()]),
        BlockScope.Ownership => "The run's branches",
        BlockScope.Repository => "The repository",
        _ => null,
    };

    /// <summary>The refs a block names, one per line, or null.</summary>
    public static string? Refs(MaterializationBlock block) => block.Scope is BlockScope.Refs { Names: var names } ? Lines(names) : null;

    /// <summary>
    /// What the person does about a shared ref that changed: put it back where the turn found it, outside iDevelop, then
    /// preserve and restore, so Restore checks it again. Null for a block without refs.
    /// </summary>
    public static string? RefRepair(MaterializationBlock block, IEnumerable<SharedRefDrift> drifts)
    {
        if (block.Scope is not BlockScope.Refs { Names: var names }) return null;
        var known = drifts.Where(drift => drift.Known && names.Contains(drift.Name)).ToDictionary(drift => drift.Name);
        var each = names.Select(name => known.TryGetValue(name, out var drift)
            ? $"{name} pointed at {Commit(drift.Recorded)} when the turn started and pointed at {Commit(drift.Observed)} when this was found."
            : $"{name} changed while the turn ran.");
        return string.Join(" ", each) + " iDevelop does not move these refs. Put each one back outside iDevelop, then preserve and restore so " +
            "iDevelop checks them again. The block clears once they are back.";
    }

    /// <summary>"Initial attempt, turn 2" or "Fix round 1", with the attempt's short id.</summary>
    public static string Attempt(TaskEvidence evidence)
    {
        var cause = evidence.Cause switch
        {
            AttemptCause.Initial => "Initial attempt",
            AttemptCause.Retry => "Retry",
            AttemptCause.Continue => "Continuation",
            AttemptCause.ReviewFix fix => $"Fix round {fix.Link.Round}",
            _ => "Attempt",
        };
        var turn = evidence.Turn is { } number and > 1 ? $", turn {number}" : "";
        return $"{cause}{turn} · {evidence.Attempt!.Value.Value.ToString("D")[..8]}";
    }

    /// <summary>"Process 4242, started 10:02:11. It no longer runs.", or that the newest turn's launch logged no process.</summary>
    public static string? Client(TaskEvidence evidence) => evidence.Root switch
    {
        { } root => $"Process {root.Id}, started {root.StartedAt.ToLocalTime():HH:mm:ss}." + evidence.RootNow switch
        {
            ProcessMatch.Same => " It still runs.",
            ProcessMatch.Gone => " It no longer runs.",
            ProcessMatch.Reused => " Its id now belongs to another process.",
            null => "",
            _ => throw new UnreachableException(),
        },
        null when evidence.Turn is not null => "Its newest turn logged no client process.",
        null => null,
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
            CaptureDisposition.Diverged { Paths.IsEmpty: false } => $"{count}{recovery}. They differ in these paths:",
            CaptureDisposition.Diverged { Refs.IsEmpty: false } => $"{count}{recovery}. They show shared refs that no task of the run moved:",
            CaptureDisposition.Diverged => $"{count}{recovery}. They were not accepted.",
            _ => $"{count}{recovery}, not yet compared.",
        };
    }

    /// <summary>The paths in which the newest turn's captures differ, or the shared refs they show moved, one per line, or null.</summary>
    public static string? CapturePaths(TaskEvidence evidence) => evidence.Dispositions.LastOrDefault()?.Disposition switch
    {
        CaptureDisposition.Diverged { Paths.IsEmpty: false } diverged => Lines(diverged.Paths),
        CaptureDisposition.Diverged { Refs.IsEmpty: false } diverged => Lines(diverged.Refs),
        _ => null,
    };

    /// <summary>How the newest turn's cleanup ended, or null when none ran.</summary>
    public static string? Cleanup(TaskEvidence evidence) => evidence.Cleanup switch
    {
        null => null,
        { Result: CleanupResult.Completed } => "Completed.",
        { Detail: { Length: > 0 } detail } => $"Incomplete: {detail}",
        _ => "Incomplete.",
    };

    /// <summary>
    /// Why an uncertain turn whose client exited cannot be closed as stopped, and what the person can do instead: Resume
    /// tries its settlement again, and while the run stops, only opening the project again does.
    /// </summary>
    public static string Unsettled(TaskView task, bool stopping)
    {
        var why = task.Refusal?.Problem switch
        {
            RunProblem.EvidenceMismatch => "Its log does not match what the run recorded.",
            RunProblem.StorageUnavailable => "iDevelop could not read or write the run's records.",
            RunProblem.TaskBusy or RunProblem.UnresolvedOwnership => "Another holder has the task's lock.",
            _ when task.Unresolved == UnresolvedReason.OwnershipConflict => "Another owner holds the task.",
            _ => "Its turn's evidence is incomplete.",
        };
        return $"Its client exited, but iDevelop could not settle its turn. {why} " + (stopping
            ? "Close the project and open it again to try once more."
            : "Resume tries again.");
    }

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

    /// <summary>
    /// What else Restore changes: refs, the index lock, the blocks it clears, and the blocks it only checks again, which
    /// clear only if their refs are back as recorded.
    /// </summary>
    public static string Rest(RestorePreview preview)
    {
        var parts = new List<string>();
        if (preview.Paths.IsEmpty && preview.Refs.IsEmpty && preview.IndexLock is null)
        {
            parts.Add("Nothing moves: the checkout matches its baseline.");
        }

        if (!preview.Refs.IsEmpty)
        {
            parts.Add($"It moves {string.Join(", ", preview.Refs)} back.");
        }

        if (preview.IndexLock is not null)
        {
            parts.Add("It removes index.lock, whose bytes stay retained.");
        }

        parts.Add(preview.Repairs.Length switch
        {
            0 => "It clears no block.",
            1 => "It clears 1 block.",
            var cleared => $"It clears {cleared} blocks.",
        });
        if (!preview.Rechecks.IsEmpty)
        {
            parts.Add(preview.Rechecks.Length == 1
                ? "It checks 1 block on shared refs again, which clears only if those refs are back as recorded."
                : $"It checks {preview.Rechecks.Length} blocks on shared refs again, which clear only if those refs are back as recorded.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>The note that macOS gives Restore no file identity, so the person moves files by hand there.</summary>
    public const string HandRepair = "On macOS, Restore cannot move files or remove index.lock, because iDevelop cannot read file identities there. " +
        "Change them by hand, then preserve again so Restore can check the checkout.";

    /// <summary>What the clean candidate changes, or why there is none. The paths are in <see cref="CandidatePaths"/>.</summary>
    public static string Candidate(RebaseCandidate candidate) => candidate switch
    {
        RebaseCandidate.Clean { Updates.IsEmpty: true } => "Its change replays cleanly, and its checkout keeps its files.",
        RebaseCandidate.Clean => "Its change replays cleanly. Its checkout gets these files from the newer inputs:",
        RebaseCandidate.Conflicted { Paths.IsEmpty: true } conflicted => $"Its change does not replay cleanly. {conflicted.Detail}",
        RebaseCandidate.Conflicted conflicted => $"Its change conflicts in these files. {conflicted.Detail}",
        RebaseCandidate.Unavailable unavailable => unavailable.Detail,
        _ => throw new UnreachableException(),
    };

    /// <summary>The files the clean candidate updates, or the conflicting ones, one per line, or null.</summary>
    public static string? CandidatePaths(RebaseCandidate candidate) => candidate switch
    {
        RebaseCandidate.Clean { Updates.IsEmpty: false } clean => Lines(clean.Updates),
        RebaseCandidate.Conflicted { Paths.IsEmpty: false } conflicted => Lines(conflicted.Paths),
        _ => null,
    };

    /// <summary>A stale result without code of its own cannot be rebased; only running the task again would bring it up to date.</summary>
    public const string NoRebase = "Its result is a report, with no code of its own, so it cannot be rebased onto the newer inputs. Running the task " +
        "again with Retry would bring it up to date, and the app does not offer Retry yet. Stop Workflow ends the run, and finished work stays.";

    /// <summary>One sentence for a refusal of a recovery or rebase command.</summary>
    public static string Problem(RunRejection rejection) => rejection.Problem switch
    {
        RunProblem.UnresolvedOwnership => "A turn of this checkout is still unresolved. Close it as stopped first.",
        RunProblem.InvalidData => "The preservation is gone. Preserve the checkout again.",
        RunProblem.EvidenceMismatch => "The checkout or the inputs changed since the preview. Check them again.",
        RunProblem.ConfirmationRequired => "Say why you close it.",
        RunProblem.InvalidClaim => "This turn is no longer unresolved.",
        RunProblem.StartConflict => "A later attempt changed this task's checkout, so it cannot be rebased.",
        RunProblem.UnsupportedResult => "This result has no code of its own, so it cannot be rebased.",
        RunProblem.ReplacementConflict => "Another choice already replaced this fix round.",
        RunProblem.SessionUnavailable => "The fix's session cannot go on. Retry the fix instead.",
        _ => WorkflowRunText.Problem(rejection),
    };

    /// <summary>Every item, one per line.</summary>
    public static string Lines(IEnumerable<string> items) => string.Join("\n", items);

    private static string Commit(CommitId? commit) => commit is { } id ? id.Hex[..12] : "nothing";
}
