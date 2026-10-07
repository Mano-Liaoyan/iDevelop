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
[JsonDerivedType(typeof(Join), "join")]
[JsonDerivedType(typeof(Salvage), "salvage")]
[JsonDerivedType(typeof(RetryReset), "retryReset")]
internal abstract record MaterializationPlan
{
    private MaterializationPlan() { }

    internal sealed record Preparation(AttemptId Attempt, InputId Inputs, TaskId Task, RevisionId Revision, AttemptCause Cause,
        ImmutableArray<InputBinding> Bindings, ImmutableArray<CodeSource> Sources, ReviewInput? Review) : MaterializationPlan;

    internal sealed record Join(TaskId Task, InputId Inputs, ImmutableArray<CodeSource> Sources, CommitRecipe Recipe,
        CommitId Commit, CommitId? Previous, string Ref) : MaterializationPlan;

    internal sealed record Publication(AttemptId Attempt, ResultId Result, ResultId? Supersedes, CommitId VerifiedTip,
        Digest? IndexBefore, CommitRecipe Recipe, CommitId Commit, string Report, ImmutableArray<ArtifactRecord> Artifacts) : MaterializationPlan;

    internal sealed record Salvage(TaskId Task, AttemptId Attempt, CommitId ObservedTip, Digest? IndexBefore, CommitRecipe Recipe,
        CommitId Commit, ImmutableArray<EvidenceFile> Untracked, string Ref) : MaterializationPlan;

    internal sealed record RetryReset(TaskId Task, AttemptId Salvaged, OperationId SalvagePlan, CommitId From, CommitId To,
        ImmutableArray<EvidenceFile> Remove) : MaterializationPlan;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateWorktree), "createWorktree")]
[JsonDerivedType(typeof(MoveRef), "moveRef")]
[JsonDerivedType(typeof(AlignIndex), "alignIndex")]
[JsonDerivedType(typeof(ResetCheckout), "resetCheckout")]
[JsonDerivedType(typeof(RemovePaths), "removePaths")]
internal abstract record GitMutation
{
    private GitMutation() { }

    internal sealed record CreateWorktree(WorktreeOwner Owner, CommitId Start, bool ExistingBranch) : GitMutation;

    internal sealed record MoveRef(RefChange Change) : GitMutation;

    internal sealed record AlignIndex(TaskId Task, Digest? Expected, TreeId Target) : GitMutation;

    internal sealed record ResetCheckout(TaskId Task, CommitId Target) : GitMutation;

    internal sealed record RemovePaths(TaskId Task, ImmutableArray<EvidenceFile> Paths) : GitMutation;
}

internal sealed record GitObservation(bool Adopted, string? Value);

internal sealed record MaterializationBlockState(MaterializationBlock Block, bool Resolved);
