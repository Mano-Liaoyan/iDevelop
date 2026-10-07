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
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal))
        {
            CommitId? previous = before.TryGetValue(name, out var old) ? old : null;
            CommitId? current = after.TryGetValue(name, out var tip) ? tip : null;
            if (previous == current) continue;
            if (name != "refs/stash" && current is { } target && (
                ExplainedRef(record, name, target) || name == prepared.Location.Owner.Branch &&
                    repository.IsAncestor(prepared.Location.AttemptBase, target) is GitAncestry.Yes ||
                SiblingFastForward(record, repository, prepared.Location.Owner.Task, name, target))) continue;
            var observed = Encoding.UTF8.GetBytes(RunJournal.Canonical(after));
            var identity = OperationIds.Derive(operation, "ownership-" + Revision.Hash(observed).Sha256);
            evidence = [storage.WriteEvidence(identity, "refs-before.json", bytes), storage.WriteEvidence(identity, "refs-after.json", observed)];
            throw Fault(MaterializationProblem.UncertainOwnership,
                $"Attempt {prepared.Launch.Attempt.Value:D}: unexplained shared ref {name}, {previous?.Hex ?? "absent"} to {current?.Hex ?? "absent"}.");
        }
    }

    private static bool ExplainedRef(RunRecord record, string name, CommitId target) => record.GitIntents.Any(pair =>
        record.GitObservations.GetValueOrDefault(pair.Key)?.Value == target.Hex && pair.Value.Mutation switch
        {
            GitMutation.MoveRef move => move.Change.Ref == name && move.Change.Target == target,
            GitMutation.ResetCheckout reset => record.RunKey is { } run && record.TaskKeys.TryGetValue(reset.Task, out var task) &&
                RunLayout.TaskBranch(run, task) == name && reset.Target == target,
            _ => false,
        });

    private static bool SiblingFastForward(RunRecord record, GitRepository repository, TaskId publisher, string name, CommitId target)
    {
        foreach (var prepared in record.Preparations.Values.Where(p => p.Location.Owner.Task != publisher &&
            p.Location.Owner.Branch == name && !record.Closures.ContainsKey(p.Launch.Attempt)))
        {
            var known = record.GitIntents.Where(pair => record.GitObservations.ContainsKey(pair.Key)).OrderByDescending(pair =>
                record.Receipts.Values.Single(entry => entry.Event is RunEvent.GitObserved observed && observed.Mutation == pair.Key).Sequence)
                .Select(pair => pair.Value.Mutation switch
                {
                    GitMutation.MoveRef move when move.Change.Ref == name => (CommitId?)move.Change.Target,
                    GitMutation.ResetCheckout reset when reset.Task == prepared.Location.Owner.Task => reset.Target,
                    GitMutation.CreateWorktree create when create.Owner == prepared.Location.Owner => create.Start,
                    _ => null,
                }).FirstOrDefault(commit => commit is not null) ?? prepared.Location.AttemptBase;
            if (repository.IsAncestor(known, target) is GitAncestry.Yes) return true;
        }
        return false;
    }
}
