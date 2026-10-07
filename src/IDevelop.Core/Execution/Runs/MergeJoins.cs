using System.Collections.Immutable;
using System.Text;

namespace IDevelop.Execution;

internal sealed class MergeJoins(string projectFolder, RunStore store,
    IReadOnlyDictionary<string, string> environment, Action<string>? probe = null) : IJoinComposer
{
    private GitRepository? _repository;

    public static Materializer Open(string projectFolder, RunStore store) => Materializer.Open(projectFolder, store,
        new MergeJoins(projectFolder, store, new Dictionary<string, string>()));

    internal static Materializer Open(string projectFolder, RunStore store, IExecutionBoundary boundary, TimeProvider clock,
        IReadOnlyDictionary<string, string>? environment, Action<string>? probe = null)
    {
        var settings = environment ?? new Dictionary<string, string>();
        return Materializer.Open(projectFolder, store, new MergeJoins(projectFolder, store, settings, probe), boundary, clock, settings, probe);
    }

    public ValueTask<JoinOutcome> Compose(JoinRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try { return ValueTask.FromResult(Compose(request)); }
        catch (MergeFailure failed) { return ValueTask.FromResult(Block(request, failed.Problem, failed.Message)); }
    }

    private JoinOutcome Compose(JoinRequest request)
    {
        var read = store.Read(request.Workflow, request.Run);
        if (read is RunRead.Rejected rejected) return Block(request, MaterializationProblem.InputUnavailable, rejected.Reason.Problem.ToString());
        var record = ((RunRead.Loaded)read).Record;
        if (request.Sources.Any(source => record.CurrentResults.GetValueOrDefault(source.Task)?.Id != source.Result))
            return Block(request, MaterializationProblem.InputUnavailable, "A join source is no longer its task's current result.");
        var reference = RunLayout.JoinBranch(record.RunKey!, record.TaskKeys[request.Task]);
        ImmutableArray<CommitId> parents = [.. request.Sources.Select(source => source.Commit).Distinct()];
        var existing = record.Plans.GetValueOrDefault(request.Operation) as MaterializationPlan.Join;
        if (existing is not null && (existing.Task != request.Task || existing.Inputs != request.Inputs || !RunReducer.Same(existing.Sources, request.Sources)))
            return Block(request, MaterializationProblem.InputUnavailable, $"Join operation {request.Operation.Value:D} has different inputs.");
        var repository = Repository();
        if (GitRepository.ParseVersion(repository.Version) is not { } version || version < new Version(2, 43, 0))
            return Block(request, MaterializationProblem.GitVersionUnsupported, $"Joins need Git 2.43 or later. Installed: {repository.Version}.");
        var timestamps = Value(repository.CommitterTimestamps(parents));
        var timestamp = timestamps[0];
        var accumulator = parents[0];
        TreeId tree = default;
        for (var step = 1; step < parents.Length; step++)
        {
            if (timestamps[step] > timestamp) timestamp = timestamps[step];
            switch (Mutate("join-merge-" + step, () => repository.MergeTrees(accumulator, parents[step], record.Base.Commit)))
            {
                case TreeMerge.Clean clean:
                    tree = clean.Tree;
                    if (step < parents.Length - 1)
                        accumulator = Value(Mutate("join-accumulator-" + step, () => repository.CreateCommit(Recipe(tree, [accumulator, parents[step]], timestamp))));
                    break;
                case TreeMerge.Conflicted conflict:
                {
                    var storage = new RunStorage(projectFolder, request.Workflow, request.Run);
                    var stderrBytes = Encoding.UTF8.GetBytes(conflict.Stderr);
                    var name = $"join-step-{step}-{Revision.Hash([.. conflict.Stdout, .. stderrBytes]).Sha256[..12]}";
                    var stdout = Mutate("join-evidence-stdout", () => storage.WriteEvidence(request.Operation, name + ".stdout", conflict.Stdout));
                    var stderr = Mutate("join-evidence-stderr", () => storage.WriteEvidence(request.Operation, name + ".stderr", stderrBytes));
                    ImmutableArray<string> paths = [.. conflict.Stages.Select(entry => entry.Path).Concat(conflict.Messages
                        .Where(message => message.Type.StartsWith("CONFLICT", StringComparison.Ordinal)).SelectMany(message => message.Paths))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                    var detail = $"Merge step {step} of {parents.Length - 1} conflicts" + (paths.IsEmpty ? "." : " in " + string.Join(", ", paths) + ".");
                    return new JoinOutcome.Blocked(new(request.Operation, request.Task, null, MaterializationProblem.FanInConflict,
                        request.Inputs, [stdout, stderr], detail, new(request.Sources, step, paths, conflict.Stages, conflict.Messages,
                            stdout, stderr, repository.Version, GitRepository.MergeSettings, record.Base.Commit)));
                }
                case TreeMerge.Failed failed:
                    return Block(request, MaterializationProblem.GitFailed, failed.Detail);
                default:
                    throw new InvalidOperationException();
            }
        }
        MaterializationPlan.Join plan;
        if (existing is not null)
        {
            if (tree != existing.Recipe.Tree || !parents.SequenceEqual(existing.Recipe.Parents))
                return Block(request, MaterializationProblem.InputUnavailable, "The recorded join is not the clean merge of its sources.");
            var restored = Value(Mutate("join-commit", () => repository.CreateCommit(existing.Recipe)));
            if (restored != existing.Commit)
                return Block(request, MaterializationProblem.InputUnavailable, "The recorded join commit does not match its recipe.");
            plan = existing;
        }
        else
        {
            if (!RefOwnership.Accepts(record, repository, reference, request.ExpectedJoin))
                return Block(request, MaterializationProblem.UncertainOwnership, $"Join ref {reference} has unexpected value {request.ExpectedJoin?.Hex ?? "absent"}.");
            var recipe = Recipe(tree, parents, timestamp);
            var commit = Value(Mutate("join-commit", () => repository.CreateCommit(recipe)));
            plan = new(request.Task, request.Inputs, request.Sources, recipe, commit, request.ExpectedJoin, reference);
            probe?.Invoke("journal.join-plan.before");
            var decision = store.Record(request.Workflow, request.Run, request.Operation, new RunEvent.Planned(plan));
            probe?.Invoke("journal.join-plan.after");
            if (decision is RunDecision.Rejected refused)
                return Block(request, MaterializationProblem.InputUnavailable, refused.Reason.Problem.ToString());
        }
        return new RefPublisher(store, probe).Publish(request.Workflow, request.Run, request.Operation, request.Operation, "join",
            repository, new(reference, plan.Previous, plan.Commit)) switch
        {
            RefPublication.Completed => new JoinOutcome.Ready(new(request.Operation, request.Sources, plan.Commit, plan.Recipe.Tree, reference)),
            RefPublication.Blocked blocked => Block(request, blocked.Problem, blocked.Detail),
            RefPublication.Rejected refused => Block(request, MaterializationProblem.InputUnavailable, refused.Reason.Problem.ToString()),
            _ => throw new InvalidOperationException(),
        };

        CommitRecipe Recipe(TreeId merged, ImmutableArray<CommitId> ancestry, DateTimeOffset at) => new(merged, ancestry,
            $"Join dependency results\n\nIDP-Run: {request.Run.Value:D}\nIDP-Task: {request.Task.Value:D}\n",
            "iDevelop <idevelop@localhost>", "iDevelop <idevelop@localhost>", at);
    }

    private GitRepository Repository() => _repository ??= GitRepository.Open(projectFolder, environment) switch
    {
        RepositoryOpen.Opened opened => opened.Repository,
        RepositoryOpen.Refused refused => throw new MergeFailure(refused.Problem, refused.Detail),
        _ => throw new InvalidOperationException(),
    };

    private T Mutate<T>(string step, Func<T> action)
    {
        probe?.Invoke("git." + step + ".before");
        var result = action();
        probe?.Invoke("git." + step + ".after");
        return result;
    }

    private static T Value<T>(GitRead<T> read) => read switch
    {
        GitRead<T>.Read value => value.Value,
        GitRead<T>.Failed failed => throw new MergeFailure(failed.Problem, failed.Detail),
        _ => throw new InvalidOperationException(),
    };

    private static JoinOutcome Block(JoinRequest request, MaterializationProblem problem, string detail) =>
        new JoinOutcome.Blocked(new(request.Operation, request.Task, null, problem, request.Inputs, [], detail));

    private sealed class MergeFailure(MaterializationProblem problem, string detail) : Exception(detail)
    { public MaterializationProblem Problem { get; } = problem; }
}
