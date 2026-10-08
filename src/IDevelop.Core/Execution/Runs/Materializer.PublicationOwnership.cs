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
        var names = UnexplainedPublicationRefs(record, repository, prepared, operation, ref evidence, out var bytes, out var before, out var after);
        if (names.IsEmpty) return;
        var name = names[0];
        CommitId? previous = before.TryGetValue(name, out var old) ? old : null;
        CommitId? current = after.TryGetValue(name, out var tip) ? tip : null;
        var storage = new RunStorage(_project, workflow, run);
        var observed = Encoding.UTF8.GetBytes(RunJournal.Canonical(after));
        var identity = OperationIds.Derive(operation, "ownership-" + Revision.Hash(observed).Sha256);
        evidence = [storage.WriteEvidence(identity, "refs-before.json", bytes), storage.WriteEvidence(identity, "refs-after.json", observed)];
        throw Fault(MaterializationProblem.UncertainOwnership,
            $"Attempt {prepared.Launch.Attempt.Value:D}: unexplained shared ref {name}, {previous?.Hex ?? "absent"} to {current?.Hex ?? "absent"}.");
    }

    private static SortedDictionary<string, CommitId> SharedRefSnapshot(GitRepository repository, RunRecord record) =>
        Value(repository.RefSnapshot(["refs/stash", $"refs/heads/idp/{record.RunKey}/", $"refs/idp/{record.RunKey}/"],
            RunLayout.PinPrefix(record.RunKey!)));

    private ImmutableArray<string> UnexplainedPublicationRefs(RunRecord record, GitRepository repository, PreparedExecution prepared,
        OperationId operation, ref ImmutableArray<EvidenceFile> evidence, out byte[] bytes,
        out SortedDictionary<string, CommitId> before, out SortedDictionary<string, CommitId> after)
    {
        var snapshot = record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Prepared>()
            .Single(e => e.Execution.Launch == prepared.Launch).SharedRefs;
        var storage = new RunStorage(_project, record.Workflow, record.Id);
        bytes = RunStorage.Read(storage.Folder, snapshot.RelativePath, snapshot.Content, snapshot.ByteLength);
        before = JsonSerializer.Deserialize<SortedDictionary<string, CommitId>>(bytes, RunJournal.Options) ??
            throw Fault(MaterializationProblem.InputUnavailable, "The prepared shared-ref snapshot is absent.");
        after = new(StringComparer.Ordinal);
        var stable = false;
        for (var snapshotNumber = 0; snapshotNumber < 3; snapshotNumber++)
        {
            record = Read(record.Workflow, record.Id);
            after = SharedRefSnapshot(repository, record);
            _probe?.Invoke("refs.snapshot.after");
            var latest = Read(record.Workflow, record.Id);
            if (record.Sequence != latest.Sequence) continue;
            stable = true;
            break;
        }
        if (!stable)
        {
            var observed = Encoding.UTF8.GetBytes(RunJournal.Canonical(after));
            var identity = OperationIds.Derive(operation, "ownership-" + Revision.Hash(observed).Sha256);
            evidence = [storage.WriteEvidence(identity, "refs-before.json", bytes), storage.WriteEvidence(identity, "refs-after.json", observed)];
            throw Fault(MaterializationProblem.UncertainOwnership, "The journal changed during every shared-ref snapshot.");
        }
        var owned = record.GitIntents.Values.Select(intent => intent.Mutation switch
        {
            GitMutation.CreateWorktree create => create.Owner.Branch,
            GitMutation.MoveRef move => move.Change.Ref,
            _ => null,
        }).OfType<string>();
        var unexplained = ImmutableArray.CreateBuilder<string>();
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal).Union(owned, StringComparer.Ordinal))
        {
            CommitId? previous = before.TryGetValue(name, out var old) ? old : null;
            CommitId? current = after.TryGetValue(name, out var tip) ? tip : null;
            if (name == "refs/stash")
            {
                if (previous == current) continue;
            }
            else if (RefOwnership.Accepts(record, repository, name, current)) continue;
            unexplained.Add(name);
        }
        return unexplained.ToImmutable();
    }
}
