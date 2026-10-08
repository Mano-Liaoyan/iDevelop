using System.Collections.Immutable;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private void VerifyWriterIndexLock(GitRepository repository, string checkout, CoordinatorPermit permit, OperationId operation,
        ref ImmutableArray<EvidenceFile> evidence)
    {
        var indexLock = Value(repository.IndexPath(checkout)) + ".lock";
        if (!File.Exists(indexLock)) return;
        var storage = new RunStorage(_project, permit.Workflow, permit.Run);
        var bytes = File.ReadAllBytes(indexLock);
        evidence = [storage.WriteEvidence(OperationIds.Derive(operation, "index-lock-" + Revision.Hash(bytes).Sha256), "index.lock", bytes)];
        throw Fault(MaterializationProblem.DirtyWorktree, "An index.lock exists in the writer checkout; it is retained and was not removed.");
    }
}
