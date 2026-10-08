using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static class OperationIds
{
    public static OperationId Derive(OperationId parent, string label)
    {
        var bytes = SHA256.HashData([.. parent.Value.ToByteArray(), .. Encoding.UTF8.GetBytes(label)]);
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(new Guid(bytes.AsSpan(0, 16)));
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Run), "run")]
[JsonDerivedType(typeof(Task), "task")]
internal abstract record LayoutKey
{
    private LayoutKey() { }

    internal sealed record Run(string Key, string Repository) : LayoutKey;

    internal sealed record Task([property: JsonPropertyName("task")] TaskId TaskId, string Key) : LayoutKey;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Preparation), "preparation")]
[JsonDerivedType(typeof(Publication), "publication")]
[JsonDerivedType(typeof(Refresh), "refresh")]
[JsonDerivedType(typeof(Join), "join")]
[JsonDerivedType(typeof(Salvage), "salvage")]
[JsonDerivedType(typeof(RetryReset), "retryReset")]
[JsonDerivedType(typeof(Preservation), "preservation")]
[JsonDerivedType(typeof(Restoration), "restoration")]
[JsonDerivedType(typeof(Rebase), "rebase")]
internal abstract record MaterializationPlan
{
    private MaterializationPlan() { }

    internal sealed record Preparation(AttemptId Attempt, InputId Inputs, TaskId Task, RevisionId Revision, AttemptCause Cause,
        ImmutableArray<InputBinding> Bindings, ImmutableArray<CodeSource> Sources, ReviewInput? Review) : MaterializationPlan;

    internal sealed record Refresh(LaunchKey Launch, InputId Inputs, ImmutableArray<InputBinding> Bindings,
        ImmutableArray<CodeSource> Sources, ReviewInput? Review, JoinRecord? Composed = null) : MaterializationPlan;

    internal sealed record Join(TaskId Task, InputId Inputs, ImmutableArray<CodeSource> Sources, CommitRecipe Recipe,
        CommitId Commit, CommitId? Previous, string Ref) : MaterializationPlan;

    internal sealed record Publication(AttemptId Attempt, ResultId Result, ResultId? Supersedes, CommitId VerifiedTip,
        Digest? IndexBefore, CommitRecipe Recipe, CommitId Commit, string Report, ImmutableArray<ArtifactRecord> Artifacts) : MaterializationPlan
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CaptureId? Capture { get; init; }
    }

    internal sealed record Salvage(TaskId Task, AttemptId Attempt, CommitId ObservedTip, CommitId? BranchTip, Digest? IndexBefore, CommitRecipe Recipe,
        CommitId Commit, ImmutableArray<EvidenceFile> Untracked, string Ref) : MaterializationPlan
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CheckoutState? Preserved { get; init; }
    }

    internal sealed record Preservation(TaskId Task, AttemptId Attempt, CheckoutState Preserved, CommitRecipe Recipe,
        CommitId Commit, ImmutableArray<ArtifactRecord> Outbox, string Ref) : MaterializationPlan;

    internal sealed record Restoration(TaskId Task, AttemptId Attempt, OperationId Preservation, CheckoutState From, CheckoutState To,
        ImmutableArray<PathRestore> Paths, ImmutableArray<OperationId> Repairs, ImmutableArray<OperationId> Rechecks,
        OperationId Confirmation, Digest Preview) : MaterializationPlan
    {
        public new OperationId Preservation { get; init; } = Preservation;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OperationId? Supersedes { get; init; }
    }

    internal sealed record RetryReset(TaskId Task, AttemptId Salvaged, OperationId SalvagePlan, CommitId? From, CommitId To,
        ImmutableArray<EvidenceFile> Remove) : MaterializationPlan;

    /// <summary>
    /// A person's approved rebase of <paramref name="Task"/>'s stale result <paramref name="Source"/>: the candidate
    /// <paramref name="Commit"/>, built by <paramref name="Recipe"/> on the current inputs' code base, replaces the branch
    /// tip <paramref name="From"/>, and <paramref name="Ref"/> retains it. <paramref name="Inputs"/> are the current inputs
    /// it was approved against. <paramref name="Preview"/> is the identity the person approved under <paramref name="Approval"/>.
    /// </summary>
    internal sealed record Rebase(TaskId Task, ResultId Source, ResultId Result, InputRecord Inputs, CommitId From,
        CommitRecipe Recipe, CommitId Commit, string Ref, OperationId Approval, Digest Preview) : MaterializationPlan
    {
        /// <summary>The recipe of the join commit the candidate is based on, when the current inputs carry several code results.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CommitRecipe? JoinRecipe { get; init; }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateWorktree), "createWorktree")]
[JsonDerivedType(typeof(MoveRef), "moveRef")]
[JsonDerivedType(typeof(AlignIndex), "alignIndex")]
[JsonDerivedType(typeof(ResetCheckout), "resetCheckout")]
[JsonDerivedType(typeof(RemovePaths), "removePaths")]
[JsonDerivedType(typeof(AttachHead), "attachHead")]
[JsonDerivedType(typeof(RestoreRef), "restoreRef")]
[JsonDerivedType(typeof(RestoreFiles), "restoreFiles")]
[JsonDerivedType(typeof(RemoveIndexLock), "removeIndexLock")]
internal abstract record GitMutation
{
    private GitMutation() { }

    internal sealed record CreateWorktree(WorktreeOwner Owner, CommitId Start, bool ExistingBranch) : GitMutation;

    internal sealed record MoveRef(RefChange Change) : GitMutation;

    internal sealed record AlignIndex(TaskId Task, Digest? Expected, TreeId Target) : GitMutation;

    internal sealed record ResetCheckout(TaskId Task, CommitId Target) : GitMutation;

    internal sealed record AttachHead(TaskId Task, string Branch) : GitMutation;

    internal sealed record RestoreRef(RefChange Change) : GitMutation;

    internal sealed record RestoreFiles(TaskId Task, ImmutableArray<PathRestore> Paths) : GitMutation;

    internal sealed record RemoveIndexLock(TaskId Task, LockEvidence Lock) : GitMutation;

    internal sealed record RemovePaths(TaskId Task, ImmutableArray<EvidenceFile> Paths) : GitMutation;
}

internal sealed record GitObservation(bool Adopted, string? Value);

internal sealed record MaterializationBlockState(MaterializationBlock Block, bool Resolved);
