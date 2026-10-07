using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static class InputMaterial
{
    public static InputRecord Build(RunRecord record, MaterializationPlan.Preparation plan, JoinRecord? join)
    {
        var commits = plan.Sources.Select(source => source.Commit).Distinct().ToArray();
        CodeSelection code = commits.Length switch
        {
            0 when join is null => new CodeSelection.Root(record.Base.Commit),
            1 when join is null => new CodeSelection.Single(plan.Sources.OrderBy(source => source.Task.ToString(), StringComparer.Ordinal).First()),
            >= 2 when join is not null && RunJournal.Canonical(join.Sources) == RunJournal.Canonical(plan.Sources) => new CodeSelection.Joined(join),
            _ => throw new ArgumentException("Join does not match the frozen sources.", nameof(join)),
        };
        var files = ImmutableArray.CreateBuilder<DeliveredFile>();
        var sections = new List<string>();
        var inlined = 0;
        var workflow = record.Revisions[plan.Revision].Snapshot;
        foreach (var binding in plan.Bindings)
        {
            if (binding is InputBinding.MissingContext missing)
            {
                sections.Add($"## {workflow.Tasks[missing.Edge.From].Title} (context)\n\nContext was unavailable.");
                continue;
            }

            var provided = (InputBinding.Provided)binding;
            var result = record.Results.Single(result => result.Id == provided.Result);
            var prefix = $".idp/inputs/{plan.Inputs.Value:D}/{result.Id.Value:D}";
            var reportPath = prefix + "/report.md";
            var bytes = Encoding.UTF8.GetBytes(result.Report);
            files.Add(new(result.Id, reportPath, Revision.Hash(bytes), bytes.LongLength));
            var text = new StringBuilder($"## {workflow.Tasks[provided.Edge.From].Title} ({provided.Kind.ToString().ToLowerInvariant()})\n\nResult: {result.Id.Value:D}\n");
            if (Source(record, provided) is { } source)
            {
                text.Append($"Code: {source.Commit.Hex}\nOwner: {source.Owner}\n");
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
        return new(plan.Inputs, plan.Task, plan.Revision, plan.Bindings, code, string.Join("\n\n", sections), files.ToImmutable(), plan.Review);
    }

    internal static ImmutableArray<CodeSource> Sources(RunRecord record, ImmutableArray<InputBinding> bindings) =>
        [.. bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency)
            .Select(binding => Source(record, binding)).OfType<CodeSource>().OrderBy(source => source.Task.ToString(), StringComparer.Ordinal)];

    internal static ReviewInput? Review(Workflow workflow, TaskId task, ImmutableArray<InputBinding> bindings) =>
        workflow.SubjectOf(task) is { } subject && bindings.OfType<InputBinding.Provided>().FirstOrDefault(binding =>
            binding.Edge.From == subject && binding.Kind == ConnectionKind.Dependency) is { } provided
            ? new(subject, provided.Result) : null;

    private static CodeSource? Source(RunRecord record, InputBinding.Provided binding)
    {
        var result = record.Results.Single(result => result.Id == binding.Result);
        return result.Code switch
        {
            CodeOutput.Produced produced => new(binding.Edge.From, result.Id, produced.Code.Owner, produced.Code.AttemptBase, produced.Code.Commit),
            CodeOutput.Forwarded forwarded => record.Inputs[forwarded.Inputs].Code switch
            {
                CodeSelection.Single single => new(binding.Edge.From, result.Id, single.Source.Owner, single.Source.AttemptBase, single.Source.Commit),
                CodeSelection.Joined joined => new(binding.Edge.From, result.Id, record.Inputs[forwarded.Inputs].Task, joined.Join.Commit, joined.Join.Commit),
                _ => null,
            },
            _ => null,
        };
    }
}
