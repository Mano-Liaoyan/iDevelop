using System.Diagnostics;
using IDevelop.Execution;

namespace IDevelop.Desktop.Execution;

/// <summary>
/// What the Recovery section says: the evidence of a block or an unresolved turn,
/// and why a recovery command recorded nothing. Lists are never cut short: every path and artifact shows, one per line, in
/// a box that scrolls.
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

    /// <summary>One sentence for a refusal of a recovery command.</summary>
    public static string Problem(RunRejection rejection) => rejection.Problem switch
    {
        RunProblem.UnresolvedOwnership => "A turn of this checkout is still unresolved. Close it as stopped first.",
        RunProblem.EvidenceMismatch => "The checkout or the inputs changed since the preview. Check them again.",
        RunProblem.ConfirmationRequired => "Say why you close it.",
        RunProblem.InvalidClaim => "This turn is no longer unresolved.",
        RunProblem.ReplacementConflict => "Another choice already replaced this fix round.",
        RunProblem.SessionUnavailable => "The fix's session cannot go on. Retry the fix instead.",
        _ => WorkflowRunText.Problem(rejection),
    };

    /// <summary>Every item, one per line.</summary>
    public static string Lines(IEnumerable<string> items) => string.Join("\n", items);
}
