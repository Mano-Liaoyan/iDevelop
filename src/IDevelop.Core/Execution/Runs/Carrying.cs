using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Why a task's result from an earlier run cannot be carried into a run, though it still counts as complete (#90).</summary>
internal abstract record CarryRefusal
{
    private CarryRefusal() { }

    /// <summary>It took a result from a task this run runs again, or from one whose result in this run is another one.</summary>
    internal sealed record InputRuns(TaskId Input) : CarryRefusal;

    /// <summary>A task it took a result from cannot be carried either.</summary>
    internal sealed record InputRefused(TaskId Input) : CarryRefusal;

    /// <summary>The carried result does not fit the run, such as inputs the run's own materialization would not build.</summary>
    internal sealed record Unavailable(string Detail) : CarryRefusal;
}

/// <summary>The results a run carries from earlier runs, in the order their inputs need them, and the needed tasks that it could not carry and why.</summary>
internal sealed record CarryBuild(ImmutableArray<IncludedResult> Carried, ImmutableDictionary<TaskId, CarryRefusal> Refused)
{
    public static CarryBuild Empty { get; } = new([], ImmutableDictionary<TaskId, CarryRefusal>.Empty);
}

/// <summary>
/// Carrying earlier results into a run that a node's Run started (#90). A run needs a result of each dependency predecessor
/// of the tasks it may start that it cannot start itself. When the workflow's history has a current result of such a
/// task, the run carries it, with every result that one took that the run has none of. A carried result brings its report
/// and its artifacts. When the branch the run starts from holds the earlier result's commit, as after its run merged, it
/// brings no code. Otherwise, as after a run that was stopped before it merged, it brings that commit, so the tasks after
/// it start from it and the run's final merge takes it. Run Workflow carries nothing.
/// </summary>
internal static class Carrying
{
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
    /// has a current result of, under the event <paramref name="carrying"/>. It records and writes nothing, so a preview
    /// builds exactly what the approval does. <paramref name="held"/> says whether the run's base holds a commit, or null
    /// when the repository cannot read it; <see cref="Held"/> asks Git.
    /// </summary>
    public static CarryBuild Build(RunRecord record, RunHistory history, OperationId carrying, Func<CommitId, bool?> held)
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
                // A task whose input cannot be carried is refused below, once the input's own result is known too.
                else Visit(input);
                if (refused.ContainsKey(task)) return;
            }
            order.Add(task);
        }

        foreach (var task in needed.Where(task => history.CurrentOf(task) is not null)) Visit(task);

        var working = record;
        var carried = ImmutableArray.CreateBuilder<IncludedResult>();
        foreach (var task in order)
        {
            var source = history.CurrentOf(task)!;
            if (source.Run.Inputs[source.Result.Inputs].Bindings.OfType<InputBinding.Provided>()
                .FirstOrDefault(binding => refused.ContainsKey(binding.Edge.From)) is { } failed)
            {
                refused[task] = new CarryRefusal.InputRefused(failed.Edge.From);
                continue;
            }
            switch (Carry(working, source, task, carrying, held))
            {
                case (IncludedResult item, null):
                    if (RunReducer.CarriedProblem(working, item, carrying) is { } problem)
                    {
                        refused[task] = new CarryRefusal.Unavailable($"The carried result does not fit the run: {problem}.");
                        continue;
                    }
                    carried.Add(item);
                    working = RunReducer.WithCarried(working, item);
                    break;
                case (_, { } refusal):
                    refused[task] = refusal;
                    break;
            }
        }
        return new(carried.ToImmutable(), refused.ToImmutable());
    }

    /// <summary>
    /// Copies each carried artifact of <paramref name="build"/> from the run that stored it, checked by its digest. Repeating
    /// it after a crash finds what it copied. Null when done, else what failed.
    /// </summary>
    public static string? Copy(string project, RunId run, WorkflowId workflow, CarryBuild build, RunHistory history)
    {
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

    /// <summary>
    /// Removes the copied artifacts of <paramref name="carried"/>, which an event of <paramref name="run"/> that was refused
    /// would have carried. Best effort: what stays is only a file that nothing names.
    /// </summary>
    public static void Forget(string project, RunId run, WorkflowId workflow, ImmutableArray<IncludedResult> carried)
    {
        var storage = new RunStorage(project, workflow, run);
        foreach (var item in carried)
        {
            var copied = Path.Combine(storage.Folder, "results", item.Result.Id.Value.ToString("D"));
            try
            {
                if (Directory.Exists(copied)) Directory.Delete(copied, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Whether the run's base <paramref name="runBase"/> holds a commit of <paramref name="repository"/>, as
    /// <see cref="Build"/> asks it: null when Git cannot read the commit.
    /// </summary>
    public static Func<CommitId, bool?> Held(GitRepository repository, CommitId runBase) => commit =>
        commit == runBase ? true : repository.IsAncestor(commit, runBase) switch
        {
            GitAncestry.Yes => true,
            GitAncestry.No => false,
            _ => null,
        };

    /// <summary>
    /// The result <paramref name="working"/> would carry for <paramref name="task"/> from <paramref name="source"/>: its report
    /// and artifacts, and its code when the run's base does not hold it, or why not.
    /// </summary>
    private static (IncludedResult? Item, CarryRefusal? Refusal) Carry(RunRecord working, EarlierResult source, TaskId task, OperationId carrying,
        Func<CommitId, bool?> held)
    {
        var (resultId, inputsId) = RunReducer.CarriedIds(carrying, task);
        var snapshot = working.Revision.Snapshot;
        var current = working.CurrentResults;
        ImmutableArray<InputBinding> bindings = [.. source.Run.Inputs[source.Result.Inputs].Bindings.Select(binding => binding switch
        {
            InputBinding.Provided provided => (InputBinding)(provided with { Result = current[provided.Edge.From].Id }),
            _ => binding,
        })];
        InputRecord inputs;
        try
        {
            inputs = InputMaterial.Build(working, inputsId, task, working.Revision.Id, bindings, [], InputMaterial.Review(snapshot, task, bindings));
        }
        catch (ArgumentException error) { return (null, new CarryRefusal.Unavailable(error.Message)); }
        CodeOutput? code = null;
        if (InputMaterial.Code(source.Run, source.Result, task) is { } earlier)
        {
            switch (held(earlier.Commit))
            {
                case null: return (null, new CarryRefusal.Unavailable($"The repository no longer has its commit {earlier.Commit.Hex}."));
                case false: code = new CodeOutput.Carried(earlier.Owners, earlier.AttemptBase, earlier.Commit); break;
            }
        }
        var result = new ResultRecord(resultId, task, working.Revision.Id, inputsId, new ResultOrigin.Carried(source.Run.Id, source.Result.Id),
            source.Result.Report, null)
        {
            Code = code,
            Artifacts = [.. source.Result.Artifacts.Select(artifact => artifact with { StoredPath = RunStorage.ArtifactPath(resultId, artifact.Name) })],
        };
        return (new(result, inputs), null);
    }
}
