using System.Collections.Immutable;

namespace IDevelop.Execution;

internal sealed record TaskWorkspace(WorktreeOwner? Owner, string Checkout, bool Registered, CommitId? BranchTip, bool? Clean,
    CommitId? LatestAcceptedCommit, ImmutableArray<MaterializationBlock> Blocks, ImmutableArray<RunEvent.SalvageRetained> Salvages);
