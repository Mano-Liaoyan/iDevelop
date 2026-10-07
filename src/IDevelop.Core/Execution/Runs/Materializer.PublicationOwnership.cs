using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private void VerifyPublicationRefs(RunRecord record, GitRepository repository, PreparedExecution prepared, OperationId operation,
        WorkflowId workflow, RunId run, ref ImmutableArray<EvidenceFile> evidence)
    {
        var snapshot = record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Prepared>()
            .Single(e => e.Execution.Launch == prepared.Launch).SharedRefs;
        var storage = new RunStorage(_project, workflow, run);
        var bytes = RunStorage.Read(storage.Folder, snapshot.RelativePath, snapshot.Content, snapshot.ByteLength);
        var before = JsonSerializer.Deserialize<SortedDictionary<string, CommitId>>(bytes, RunJournal.Options) ??
            throw Fault(MaterializationProblem.InputUnavailable, "The prepared shared-ref snapshot is absent.");
        var after = Value(repository.RefSnapshot("refs/stash", $"refs/heads/idp/{record.RunKey}/", $"refs/idp/{record.RunKey}/"));
        var owned = record.GitIntents.Values.Select(intent => intent.Mutation switch
        {
            GitMutation.CreateWorktree create => create.Owner.Branch,
            GitMutation.MoveRef move => move.Change.Ref,
            _ => null,
        }).OfType<string>();
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal).Union(owned, StringComparer.Ordinal))
        {
            CommitId? previous = before.TryGetValue(name, out var old) ? old : null;
            CommitId? current = after.TryGetValue(name, out var tip) ? tip : null;
            if (name == "refs/stash" ? previous == current : RefOwnership.Accepts(record, repository, name, current)) continue;
            var observed = Encoding.UTF8.GetBytes(RunJournal.Canonical(after));
            var identity = OperationIds.Derive(operation, "ownership-" + Revision.Hash(observed).Sha256);
            evidence = [storage.WriteEvidence(identity, "refs-before.json", bytes), storage.WriteEvidence(identity, "refs-after.json", observed)];
            throw Fault(MaterializationProblem.UncertainOwnership,
                $"Attempt {prepared.Launch.Attempt.Value:D}: unexplained shared ref {name}, {previous?.Hex ?? "absent"} to {current?.Hex ?? "absent"}.");
        }
    }

}
