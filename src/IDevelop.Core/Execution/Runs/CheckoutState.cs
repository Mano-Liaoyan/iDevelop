using System.Collections.Immutable;

namespace IDevelop.Execution;

internal sealed record CheckoutState(CommitId? Branch, CommitId? Head, string? SymbolicHead, TreeId Files,
    EvidenceFile? Index, TreeId? IndexTree, ImmutableArray<EvidenceFile> Untracked, LockEvidence? IndexLock);
