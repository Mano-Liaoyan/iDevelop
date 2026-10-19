using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static class InputMaterial
{
    /// <summary>
    /// The inputs a plan's attempt starts with. A Merge node's attempt takes only the conflict it resolves, and its worktree
    /// starts at the conflict's commit with the conflict markers.
    /// </summary>
    public static InputRecord Build(RunRecord record, MaterializationPlan.Preparation plan, JoinRecord? join) =>
        RunReducer.MergeOf(record, plan.Cause) is { } conflict
            ? join is null && plan.Bindings.IsEmpty && plan.Sources.IsEmpty && plan.Review is null
                ? new(plan.Inputs, plan.Task, plan.Revision, [], new CodeSelection.Root(conflict.Conflicted), "", [], null) { Conflict = conflict }
                : throw new ArgumentException("A Merge node's attempt takes no results.", nameof(plan))
            : Build(record, plan.Inputs, plan.Task, plan.Revision, plan.Bindings, plan.Sources, plan.Review, join);

    public static InputRecord Build(RunRecord record, InputId inputs, TaskId task, RevisionId revision,
        ImmutableArray<InputBinding> bindings, ImmutableArray<CodeSource> sources, ReviewInput? review, JoinRecord? join = null)
    {
        var commits = sources.Select(source => source.Commit).Distinct().ToArray();
        CodeSelection code = commits.Length switch
        {
            0 when join is null => new CodeSelection.Root(record.Base.Commit),
            1 when join is null => new CodeSelection.Single(sources.OrderBy(source => source.Task.ToString(), StringComparer.Ordinal).First()),
            >= 2 when join is not null && RunJournal.Canonical(join.Sources) == RunJournal.Canonical(sources) => new CodeSelection.Joined(join),
            _ => throw new ArgumentException("Join does not match the frozen sources.", nameof(join)),
        };
        var files = ImmutableArray.CreateBuilder<DeliveredFile>();
        var sections = new List<string>();
        var inlined = 0;
        var workflow = record.Revisions[revision].Snapshot;
        foreach (var binding in bindings)
        {
            if (binding is InputBinding.MissingContext missing)
            {
                sections.Add($"## {workflow.Tasks[missing.Edge.From].Title} (context)\n\nContext was unavailable.");
                continue;
            }

            var provided = (InputBinding.Provided)binding;
            var result = record.Results.Single(result => result.Id == provided.Result);
            var prefix = $"{RunLayout.InputFolder(inputs)}/{result.Id.Value:D}";
            var reportPath = prefix + "/report.md";
            var bytes = Encoding.UTF8.GetBytes(result.Report);
            files.Add(new(result.Id, reportPath, Revision.Hash(bytes), bytes.LongLength));
            var text = new StringBuilder($"## {workflow.Tasks[provided.Edge.From].Title} ({provided.Kind.ToString().ToLowerInvariant()})\n\nResult: {result.Id.Value:D}\n");
            if (Source(record, provided) is { } source)
            {
                text.Append($"Code: {source.Commit.Hex}\n{(source.Owners.Length == 1 ? "Owner" : "Owners")}: {string.Join(", ", source.Owners)}\n");
            }
            text.Append("\nReport:\n\n");
            if (inlined + bytes.Length <= 65536)
            {
                text.Append(result.Report);
                inlined += bytes.Length;
            }
            else
            {
                text.Append($"{reportPath} ({bytes.LongLength.ToString(CultureInfo.InvariantCulture)} bytes)");
            }

            if (provided.Kind == ConnectionKind.Dependency && result.Artifacts.Length > 0)
            {
                text.Append("\n\nArtifacts:\n");
                foreach (var artifact in result.Artifacts)
                {
                    var path = prefix + "/artifacts/" + artifact.Name;
                    files.Add(new(result.Id, path, artifact.Content, artifact.ByteLength));
                    text.Append($"\n- {artifact.Name}: {path} ({artifact.ByteLength.ToString(CultureInfo.InvariantCulture)} bytes, SHA-256 {artifact.Content.Sha256})");
                }
            }
            sections.Add(text.ToString());
        }
        return new(inputs, task, revision, bindings, code, string.Join("\n\n", sections), files.ToImmutable(), review);
    }

    internal static ImmutableArray<CodeSource> Sources(RunRecord record, ImmutableArray<InputBinding> bindings) =>
        [.. bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency)
            .Select(binding => Source(record, binding)).OfType<CodeSource>().OrderBy(source => source.Task.ToString(), StringComparer.Ordinal)];

    /// <summary>
    /// The code an attempt of <paramref name="task"/> with <paramref name="cause"/> starts from: its dependency inputs' code,
    /// and for an editing task's Retry, once that code changed since its current result, the result's own code too. The
    /// Retry then continues in its worktree with its earlier change, and the new work merges into it before its agent
    /// starts (#125).
    /// </summary>
    internal static ImmutableArray<CodeSource> Sources(RunRecord record, TaskId task, RevisionId revision, AttemptCause cause,
        ImmutableArray<InputBinding> bindings)
    {
        var sources = Sources(record, bindings);
        if (cause is not AttemptCause.Retry ||
            record.Revisions[revision].Snapshot.Tasks[task].Blueprint.Work is not WorkSpec.Agent { Access: AgentAccess.Edit } ||
            record.CurrentResults.GetValueOrDefault(task) is not { Code: CodeOutput.Produced produced } result ||
            !record.Inputs.TryGetValue(result.Inputs, out var built))
            return sources;
        var earlier = Sources(record, built.Bindings).Select(source => source.Commit).ToHashSet();
        return sources.All(source => earlier.Contains(source.Commit)) ? sources
            : [.. sources.Append(new(task, result.Id, [task], produced.Code.AttemptBase, produced.Code.Commit))
                .OrderBy(source => source.Task.ToString(), StringComparer.Ordinal)];
    }

    internal static ReviewInput? Review(Workflow workflow, TaskId task, ImmutableArray<InputBinding> bindings) =>
        workflow.SubjectOf(task) is { } subject && bindings.OfType<InputBinding.Provided>().FirstOrDefault(binding =>
            binding.Edge.From == subject && binding.Kind == ConnectionKind.Dependency) is { } provided
            ? new(subject, provided.Result) : null;

    private static CodeSource? Source(RunRecord record, InputBinding.Provided binding)
    {
        var result = record.Results.Single(result => result.Id == binding.Result);
        return result.Code switch
        {
            CodeOutput.Produced produced => new(binding.Edge.From, result.Id, [produced.Code.Owner], produced.Code.AttemptBase, produced.Code.Commit),
            CodeOutput.Forwarded forwarded => record.Inputs[forwarded.Inputs].Code switch
            {
                CodeSelection.Single single => new(binding.Edge.From, result.Id, single.Source.Owners, single.Source.AttemptBase, single.Source.Commit),
                CodeSelection.Joined joined => new(binding.Edge.From, result.Id,
                    [.. joined.Join.Sources.SelectMany(source => source.Owners).Distinct().OrderBy(owner => owner.ToString(), StringComparer.Ordinal)],
                    joined.Join.Commit, joined.Join.Commit),
                _ => null,
            },
            _ => null,
        };
    }
}
