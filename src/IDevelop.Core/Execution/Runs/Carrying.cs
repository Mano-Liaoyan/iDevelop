using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Why a task's result from an earlier run cannot be carried into a run, though it still counts as complete (#90).</summary>
internal abstract record CarryRefusal
{
    private CarryRefusal() { }

    /// <summary>The task's code does not apply to the run's base, so the task must run again.</summary>
    internal sealed record Conflict(ImmutableArray<string> Paths) : CarryRefusal
    {
        public bool Equals(Conflict? other) => other is not null && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal);

        public override int GetHashCode() => Paths.Length;
    }

    /// <summary>The results the task took code from do not join cleanly on the run's base.</summary>
    internal sealed record JoinConflict(ImmutableArray<string> Paths) : CarryRefusal
    {
        public bool Equals(JoinConflict? other) => other is not null && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal);

        public override int GetHashCode() => Paths.Length;
    }

    /// <summary>It took a result from a task this run runs again, or from one whose result in this run is another one.</summary>
    internal sealed record InputRuns(TaskId Input) : CarryRefusal;

    /// <summary>A task it took a result from cannot be carried either.</summary>
    internal sealed record InputRefused(TaskId Input) : CarryRefusal;

    /// <summary>Git cannot replay it here, such as an older Git that cannot merge, or a failed Git command.</summary>
    internal sealed record Unavailable(string Detail) : CarryRefusal;
}

/// <summary>
/// The results a run carries from earlier runs, in the order their inputs need them, the Git commits that keep their code,
/// and the needed tasks that it could not carry and why.
/// </summary>
internal sealed record CarryBuild(ImmutableArray<IncludedResult> Carried, ImmutableArray<(string Ref, CommitId Commit)> Kept,
    ImmutableDictionary<TaskId, CarryRefusal> Refused)
{
    public static CarryBuild Empty { get; } = new([], [], ImmutableDictionary<TaskId, CarryRefusal>.Empty);
}

/// <summary>
/// Carrying earlier results into a run that a node's Run started (#90). A run needs a result of each dependency predecessor
/// of the tasks it may start that it cannot start itself. When the workflow's history has a current result of such a
/// task, the run carries it, with every result that one took that the run has none of: its code replayed onto the run's
/// base as Review updated inputs replays a stale result (ADR 0009), its report, and its artifacts. A code that conflicts
/// with the base is not carried, and the task must run again. Run Workflow carries nothing.
/// </summary>
internal static class Carrying
{
    private const string Identity = "iDevelop <idevelop@localhost>";

    /// <summary>
    /// The tasks <paramref name="record"/> needs results of and cannot start: each dependency predecessor of a task it may
    /// start that is outside those tasks and has no result in the run, in task order.
    /// </summary>
    public static ImmutableArray<TaskId> Needed(RunRecord record)
    {
        if (record.Requested is null) return [];
        var flow = RunScope.InFlow(record);
        var predecessors = RunScope.Predecessors(record.Revision.Snapshot);
        return [.. flow.SelectMany(task => predecessors[task]).Where(task => !flow.Contains(task) && !record.Results.Any(result => result.Task == task))
            .Distinct().Order()];
    }

    /// <summary>
    /// Builds the results <paramref name="record"/> would carry for each of <see cref="Needed"/> that <paramref name="history"/>
    /// has a current result of, under the event <paramref name="carrying"/>. It writes the commits the carried results name,
    /// which <see cref="Keep"/> then keeps, so a preview merges exactly as the approval does, and nothing references
    /// what a preview wrote. It records nothing.
    /// </summary>
    /// <param name="repository">The project's repository, or null where no result needs Git, as for reports.</param>
    public static CarryBuild Build(GitRepository? repository, RunRecord record, RunHistory history, OperationId carrying)
    {
        var needed = Needed(record);
        if (needed.IsEmpty) return CarryBuild.Empty;
        var flow = RunScope.InFlow(record);
        var order = new List<TaskId>();
        var refused = ImmutableDictionary.CreateBuilder<TaskId, CarryRefusal>();
        var visited = new HashSet<TaskId>();

        // Each task the needed ones took a result from comes first, so its carried result is there to bind.
        void Visit(TaskId task)
        {
            if (!visited.Add(task)) return;
            var source = history.CurrentOf(task)!;
            foreach (var binding in source.Run.Inputs[source.Result.Inputs].Bindings.OfType<InputBinding.Provided>())
            {
                var input = binding.Edge.From;
                if (record.CurrentResults.GetValueOrDefault(input) is { } own)
                {
                    if (history.Root(record, own.Id) != history.Root(source.Run, binding.Result)) refused[task] = new CarryRefusal.InputRuns(input);
                }
                else if (flow.Contains(input)) refused[task] = new CarryRefusal.InputRuns(input);
                // A task whose input cannot be carried is refused below, once the input's own replay is known too.
                else Visit(input);
                if (refused.ContainsKey(task)) return;
            }
            order.Add(task);
        }

        foreach (var task in needed.Where(task => history.CurrentOf(task) is not null)) Visit(task);

        var working = record;
        var carried = ImmutableArray.CreateBuilder<IncludedResult>();
        var kept = ImmutableArray.CreateBuilder<(string, CommitId)>();
        var merges = repository is not null && GitRepository.ParseVersion(repository.Version) is { } version && version >= new Version(2, 43, 0);
        foreach (var task in order)
        {
            var source = history.CurrentOf(task)!;
            if (source.Run.Inputs[source.Result.Inputs].Bindings.OfType<InputBinding.Provided>()
                .FirstOrDefault(binding => refused.ContainsKey(binding.Edge.From)) is { } failed)
            {
                refused[task] = new CarryRefusal.InputRefused(failed.Edge.From);
                continue;
            }
            switch (Replay(repository, working, source, task, carrying, merges))
            {
                case (IncludedResult item, var commits, null):
                    if (RunReducer.CarriedProblem(working, item, carrying) is { } problem)
                    {
                        refused[task] = new CarryRefusal.Unavailable($"The carried result does not fit the run: {problem}.");
                        continue;
                    }
                    carried.Add(item);
                    kept.AddRange(commits);
                    working = RunReducer.WithCarried(working, item);
                    break;
                case (_, _, { } refusal):
                    refused[task] = refusal;
                    break;
            }
        }
        return new(carried.ToImmutable(), kept.ToImmutable(), refused.ToImmutable());
    }

    /// <summary>
    /// Keeps each commit of <paramref name="build"/> under the run's carried refs, and copies each carried artifact from the
    /// run that stored it, checked by its digest. Repeating it after a crash finds what it kept and copied. Null when done,
    /// else what failed.
    /// </summary>
    public static string? Keep(GitRepository repository, string project, RunId run, WorkflowId workflow, CarryBuild build, RunHistory history)
    {
        foreach (var (name, commit) in build.Kept)
        {
            if (repository.MoveRef(new(name, null, commit)) is not (RefMove.Moved or RefMove.AlreadyAtTarget))
                return $"Git could not keep a carried result under {name}.";
        }
        var storage = new RunStorage(project, workflow, run);
        foreach (var item in build.Carried)
        {
            var origin = (ResultOrigin.Carried)item.Result.Origin;
            if (history.Runs.FirstOrDefault(record => record.Id == origin.Run) is not { } source ||
                source.Results.FirstOrDefault(result => result.Id == origin.Result) is not { } result)
                return $"The earlier run {origin.Run} is no longer readable.";
            var from = new RunStorage(project, workflow, source.Id);
            foreach (var artifact in result.Artifacts)
            {
                try
                {
                    var bytes = from.ReadArtifact(result.Id, artifact);
                    RunStorage.Publish(storage.Folder, RunStorage.ArtifactPath(item.Result.Id, artifact.Name), bytes, artifact.Content, artifact.ByteLength);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return $"The artifact {artifact.Name} of an earlier result could not be copied: {error.Message}";
                }
            }
        }
        return null;
    }

    /// <summary>Removes the refs that keep the commits of <paramref name="run"/>'s carried results, for a run that was never approved.</summary>
    public static void Release(GitRepository repository, RunId run)
    {
        if (repository.RefSnapshot(RunLayout.CarriedPrefix(run)) is not GitRead<SortedDictionary<string, CommitId>>.Read refs) return;
        foreach (var (name, commit) in refs.Value) repository.DeleteRef(name, commit);
    }

    /// <summary>The result <paramref name="working"/> would carry for <paramref name="task"/> from <paramref name="source"/>, with the commits to keep, or why not.</summary>
    private static (IncludedResult? Item, ImmutableArray<(string, CommitId)> Kept, CarryRefusal? Refusal) Replay(GitRepository? repository, RunRecord working,
        EarlierResult source, TaskId task, OperationId carrying, bool merges)
    {
        var (resultId, inputsId) = RunReducer.CarriedIds(carrying, task);
        var snapshot = working.Revision.Snapshot;
        var definition = snapshot.Tasks[task];
        var current = working.CurrentResults;
        ImmutableArray<InputBinding> bindings = [.. source.Run.Inputs[source.Result.Inputs].Bindings.Select(binding => binding switch
        {
            InputBinding.Provided provided => (InputBinding)(provided with { Result = current[provided.Edge.From].Id }),
            _ => binding,
        })];
        var sources = InputMaterial.Sources(working, bindings);
        var kept = ImmutableArray.CreateBuilder<(string, CommitId)>();
        var runBase = working.Base.Commit;
        JoinRecord? join = null;
        ImmutableArray<CommitId> commits = [.. sources.Select(item => item.Commit).Distinct()];
        if (commits.Length >= 2)
        {
            if (!merges) return (null, [], new CarryRefusal.Unavailable($"Joining carried results needs Git 2.43 or later. Installed: {repository?.Version}."));
            var joined = Join(repository!, working, task, commits);
            if (joined.Refusal is { } refusal) return (null, [], refusal);
            join = new(RunReducer.CarriedJoinOperation(carrying, resultId), sources, joined.Commit, joined.Tree, RunLayout.CarriedJoin(working.Id, resultId));
            kept.Add((join.Ref, join.Commit));
        }
        InputRecord inputs;
        try
        {
            inputs = InputMaterial.Build(working, inputsId, task, working.Revision.Id, bindings, sources, InputMaterial.Review(snapshot, task, bindings), join);
        }
        catch (ArgumentException error) { return (null, [], new CarryRefusal.Unavailable(error.Message)); }
        CodeOutput? code;
        if (definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.Edit })
        {
            if (source.Result.Code is not CodeOutput.Produced { Code: var owned })
                return (null, [], new CarryRefusal.Unavailable("The earlier result has no code of its own."));
            var oldBase = source.Run.Inputs[source.Result.Inputs].CodeBase;
            var newBase = inputs.CodeBase;
            var (commit, tree) = (owned.Commit, owned.Tree);
            if (newBase != oldBase)
            {
                if (!merges) return (null, [], new CarryRefusal.Unavailable($"Replaying an earlier result needs Git 2.43 or later. Installed: {repository?.Version}."));
                switch (repository!.MergeTrees(newBase, owned.Commit, runBase, oldBase))
                {
                    case TreeMerge.Clean clean:
                        tree = clean.Tree;
                        break;
                    case TreeMerge.Conflicted conflicted:
                        return (null, [], new CarryRefusal.Conflict(Paths(conflicted)));
                    case TreeMerge.Failed failed:
                        return (null, [], new CarryRefusal.Unavailable(failed.Detail));
                }
                if (repository.CommitterTimestamps([newBase, owned.Commit]) is not GitRead<ImmutableArray<DateTimeOffset>>.Read times)
                    return (null, [], new CarryRefusal.Unavailable("Git could not read the commits to replay."));
                var recipe = new CommitRecipe(tree, [newBase], $"Carry an earlier result onto the run base\n\nIDP-Run: {working.Id.Value:D}\n" +
                    $"IDP-Task: {task.Value:D}\nIDP-Carried-From: {owned.Commit.Hex}\n", Identity, Identity, times.Value.Max());
                if (repository.CreateCommit(recipe) is not GitRead<CommitId>.Read created)
                    return (null, [], new CarryRefusal.Unavailable("Git could not write the replayed commit."));
                commit = created.Value;
            }
            var reference = RunLayout.CarriedCode(working.Id, resultId);
            kept.Add((reference, commit));
            code = new CodeOutput.Produced(new(task, owned.Attempt, newBase, commit, tree, reference));
        }
        else code = inputs.Code is CodeSelection.Single or CodeSelection.Joined ? new CodeOutput.Forwarded(inputs.Id) : null;
        var result = new ResultRecord(resultId, task, working.Revision.Id, inputsId, new ResultOrigin.Carried(source.Run.Id, source.Result.Id),
            source.Result.Report, null)
        {
            Code = code,
            Artifacts = [.. source.Result.Artifacts.Select(artifact => artifact with { StoredPath = RunStorage.ArtifactPath(resultId, artifact.Name) })],
        };
        return (new(result, inputs), kept.ToImmutable(), null);
    }

    /// <summary>
    /// The join of a carried task's code inputs, merged as a run's own join merges them (<see cref="MergeJoins"/>): one
    /// source after another, with the merge base Git finds from their history, the run base as the attribute source, and a
    /// commit for each step.
    /// </summary>
    private static (CommitId Commit, TreeId Tree, CarryRefusal? Refusal) Join(GitRepository repository, RunRecord working, TaskId task,
        ImmutableArray<CommitId> parents)
    {
        var runBase = working.Base.Commit;
        if (repository.CommitterTimestamps(parents) is not GitRead<ImmutableArray<DateTimeOffset>>.Read times)
            return (default, default, new CarryRefusal.Unavailable("Git could not read the commits to join."));
        var timestamp = times.Value.Max();
        var accumulator = parents[0];
        TreeId tree = default;
        for (var step = 1; step < parents.Length; step++)
        {
            switch (repository.MergeTrees(accumulator, parents[step], runBase))
            {
                case TreeMerge.Clean clean:
                    tree = clean.Tree;
                    if (step == parents.Length - 1) break;
                    if (repository.CreateCommit(Recipe(tree, [accumulator, parents[step]])) is GitRead<CommitId>.Read partial) accumulator = partial.Value;
                    else return (default, default, new CarryRefusal.Unavailable("Git could not write the join."));
                    break;
                case TreeMerge.Conflicted conflicted:
                    return (default, default, new CarryRefusal.JoinConflict(Paths(conflicted)));
                case TreeMerge.Failed failed:
                    return (default, default, new CarryRefusal.Unavailable(failed.Detail));
            }
        }
        return repository.CreateCommit(Recipe(tree, parents)) is GitRead<CommitId>.Read joined
            ? (joined.Value, tree, null)
            : (default, default, new CarryRefusal.Unavailable("Git could not write the join."));

        CommitRecipe Recipe(TreeId merged, ImmutableArray<CommitId> ancestry) => new(merged, ancestry,
            $"Join dependency results\n\nIDP-Run: {working.Id.Value:D}\nIDP-Task: {task.Value:D}\n", Identity, Identity, timestamp);
    }

    private static ImmutableArray<string> Paths(TreeMerge.Conflicted conflict) =>
        [.. conflict.Stages.Select(entry => entry.Path).Concat(conflict.Messages
            .Where(message => message.Type.StartsWith("CONFLICT", StringComparison.Ordinal)).SelectMany(message => message.Paths))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
