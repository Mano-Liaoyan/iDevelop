using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// A confirmed approval, recorded before any of its side effects: the snapshot commit and the run's journal. A run folder
/// that holds an intent and no journal is an approval that has not finished. Repeating any confirmation it lists, or
/// confirming its content again, finishes it as the same run.
/// </summary>
/// <param name="Operation">The <see cref="RunStore.Approve"/> operation, derived from the first confirmation.</param>
/// <param name="Confirmations">Every confirmation that approved through this intent, the first one first.</param>
/// <param name="Snapshot">For a snapshot base, the work tree's tree the person confirmed.</param>
/// <param name="Recorded">When the intent was recorded, in whole seconds: the snapshot commit's time, so a retry rebuilds the same commit.</param>
internal sealed record ApprovalIntent(int Schema, RunId Run, OperationId Operation, ImmutableArray<OperationId> Confirmations,
    ApprovedRevision Revision, BaseChoice Choice, CommitId Head, TreeId? Snapshot, DateTimeOffset Recorded)
{
    private const string Identity = "iDevelop <idevelop@localhost>";

    /// <summary>The reports the confirmation included, by task. A waiting planner among them is marked done before the approval.</summary>
    public ImmutableArray<ReportInclusion> Inclusions { get; init; } = [];

    /// <summary>The node whose Run asked for the run, or null for Run Workflow (#90).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskId? Node { get; init; }

    /// <summary>Whether the intent approves what <paramref name="live"/> shows with <paramref name="choice"/> and <paramref name="inclusions"/>.</summary>
    public bool Matches(RunPreflight live, BaseChoice choice, ImmutableArray<ReportInclusion> inclusions) => live.Base is { } found &&
        Revision.Id == live.Revision.Id && Choice == choice && Head == found.Head && (choice == BaseChoice.Head || Snapshot == found.WorkTree) &&
        Inclusions.SequenceEqual(inclusions) && Node == live.Node;

    /// <summary>The snapshot commit: the confirmed tree over HEAD, by iDevelop, at the intent's time.</summary>
    public CommitRecipe SnapshotRecipe() => new(Snapshot ?? throw new InvalidOperationException("The intent has no snapshot."), [Head],
        $"iDevelop run base for run {Run}\n\nA snapshot of the uncommitted work, which leaves the person's branch, index, and files as they were.\n",
        Identity, Identity, Recorded);
}

/// <summary>
/// The ref that keeps an approved run's base commit, <c>refs/idp/approvals/&lt;run-id&gt;</c>, from its approval until the
/// run's own <c>refs/idp/&lt;run&gt;/base</c> holds it. A snapshot commit is referenced by nothing else, and the base ref
/// is written only at the run's first preparation. <c>approvals</c> is never a run key, because run keys are hexadecimal,
/// and no ownership snapshot reads it.
/// </summary>
internal static class ApprovalPin
{
    public static string Name(RunId run) => $"refs/idp/approvals/{run}";

    /// <summary>Creates the pin from absent, or finds it already there. False when it names another commit or Git fails.</summary>
    public static bool Hold(GitRepository repository, RunId run, CommitId commit) =>
        repository.MoveRef(new(Name(run), null, commit)) is RefMove.Moved or RefMove.AlreadyAtTarget;

    /// <summary>
    /// Removes the pin once <paramref name="baseRef"/> holds <paramref name="commit"/>, or, without a base ref, for a run
    /// that was never approved. Best effort: a pin left behind only keeps a commit, and the next preparation or
    /// confirmation of the workflow removes it.
    /// </summary>
    public static void Release(GitRepository repository, RunId run, CommitId? commit = null, string? baseRef = null)
    {
        if (repository.ReadRef(Name(run)) is not GitRead<CommitId?>.Read { Value: { } pinned }) return;
        if (baseRef is not null && (pinned != commit || repository.ReadRef(baseRef) is not GitRead<CommitId?>.Read { Value: { } held } || held != pinned))
            return;
        repository.DeleteRef(Name(run), pinned);
    }
}

/// <summary>The approval intents of one workflow's runs, and the lock that orders its confirmations.</summary>
internal sealed class ApprovalIntents
{
    private const string FileName = "approval.json";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Options = new(RunJournal.Options)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _project;
    private readonly string _folder;

    public ApprovalIntents(string project, WorkflowId workflow)
    {
        _project = project;
        _folder = Path.Combine(DataFolder.Runs(project), workflow.ToString());
    }

    /// <summary>Waits up to ten seconds for the workflow's approval lock. Null when another confirmation keeps it.</summary>
    public FileStream? Lock()
    {
        DataFolder.EnsureGitIgnore(_project);
        Directory.CreateDirectory(_folder);
        var patience = Stopwatch.StartNew();
        do
        {
            try
            {
                return new FileStream(Path.Combine(_folder, "approval.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        } while (patience.Elapsed < Patience);
        return null;
    }

    /// <summary>Every readable intent, by run. An intent that cannot be read is left alone.</summary>
    public ImmutableArray<ApprovalIntent> All()
    {
        if (!Directory.Exists(_folder)) return [];
        var intents = ImmutableArray.CreateBuilder<ApprovalIntent>();
        foreach (var run in Directory.EnumerateDirectories(_folder).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(run, FileName);
            if (!File.Exists(path) || !Guid.TryParse(Path.GetFileName(run), out var id)) continue;
            try
            {
                if (JsonSerializer.Deserialize<ApprovalIntent>(File.ReadAllBytes(path), Options) is { Schema: 1 } intent && intent.Run.Value == id &&
                    !intent.Confirmations.IsDefaultOrEmpty)
                    intents.Add(intent);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or KeyNotFoundException or
                FormatException or ArgumentException or ProjectException or BlueprintException or OverflowException) { }
        }
        return intents.ToImmutable();
    }

    public void Write(ApprovalIntent intent)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, intent.Run.ToString())).FullName;
        AtomicFile.Replace(Path.Combine(folder, FileName), JsonSerializer.SerializeToUtf8Bytes(intent, Options));
    }

    /// <summary>
    /// Removes an intent whose run was never approved: first its pin, then the intent, then its folder once nothing else
    /// is in it. A crash in between leaves a pending intent without a pin, which a later confirmation pins again or removes.
    /// </summary>
    public void Remove(RunId run, GitRepository? repository)
    {
        if (repository is not null) ApprovalPin.Release(repository, run);
        var folder = Path.Combine(_folder, run.ToString());
        File.Delete(Path.Combine(folder, FileName));
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
}
