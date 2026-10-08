using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal readonly record struct GateId(Guid Value);

/// <summary>
/// An Approval node's durable request for the person's decision on fixed inputs. It creates no attempt.
/// <paramref name="Sequence"/> counts the node's requests from 1, and <paramref name="Result"/> is the id an approval's
/// result takes, under which the request already stored the artifacts it forwards.
/// </summary>
internal sealed record GateRequest(GateId Id, TaskId Task, RevisionId Revision, InputId Inputs, int Sequence, ResultId Result);

/// <summary>How the person answered a request, with the operation that recorded it.</summary>
internal abstract record GateDecision
{
    private GateDecision() { }

    public abstract OperationId Operation { get; }

    internal sealed record Approved(ResultId Result, OperationId Operation) : GateDecision
    {
        public override OperationId Operation { get; } = Operation;
    }

    internal sealed record SentBack(string Reason, OperationId Operation) : GateDecision
    {
        public override OperationId Operation { get; } = Operation;
    }
}

internal sealed record GateState(GateRequest Request, GateDecision? Decision);

/// <summary>A person's answer to a request.</summary>
internal abstract record GateAnswer
{
    private GateAnswer() { }

    internal sealed record Approve : GateAnswer;

    internal sealed record SendBack(string Reason) : GateAnswer;
}

/// <summary>
/// What an approval hands on: the code of its inputs, their dependency artifacts under the approval's result, and their
/// reports. The person decides on these, so the request fixes them and the approval records them unchanged.
/// </summary>
internal static class GateForwarding
{
    /// <summary>Each input's report under its task's title, in binding order, unshortened.</summary>
    public static string Report(RunRecord record, InputRecord inputs)
    {
        var workflow = record.Revisions[inputs.Revision].Snapshot;
        return string.Join("\n\n", inputs.Bindings.Select(binding => binding switch
        {
            InputBinding.Provided provided =>
                $"## {workflow.Tasks[provided.Edge.From].Title} ({provided.Kind.ToString().ToLowerInvariant()})\n\n" +
                record.Results.Single(result => result.Id == provided.Result).Report,
            InputBinding.MissingContext missing => $"## {workflow.Tasks[missing.Edge.From].Title} (context)\n\nContext was unavailable.",
            _ => throw new InvalidOperationException("Unknown input binding."),
        }));
    }

    /// <summary>The code an approval forwards: its inputs' code, or none on the approved base.</summary>
    public static CodeOutput? Code(InputRecord inputs) =>
        inputs.Code is CodeSelection.Single or CodeSelection.Joined ? new CodeOutput.Forwarded(inputs.Id) : null;

    /// <summary>
    /// The dependency inputs' artifacts, stored under <paramref name="result"/> with their names, bytes, and digests kept.
    /// An artifact reached through two inputs counts once. Two different artifacts whose names differ at most in case are a
    /// collision: the approval could not hand on both, so <c>Collision</c> names the second.
    /// </summary>
    public static (ImmutableArray<ArtifactRecord> Artifacts, string? Collision) Artifacts(RunRecord record, InputRecord inputs, ResultId result)
    {
        var forwarded = ImmutableArray.CreateBuilder<ArtifactRecord>();
        foreach (var binding in inputs.Bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == ConnectionKind.Dependency))
        {
            foreach (var artifact in record.Results.Single(source => source.Id == binding.Result).Artifacts)
            {
                var rehomed = artifact with { StoredPath = RunStorage.ArtifactPath(result, artifact.Name) };
                if (forwarded.FirstOrDefault(prior => string.Equals(prior.Name, artifact.Name, StringComparison.OrdinalIgnoreCase)) is { } prior)
                {
                    if (prior != rehomed) return ([], artifact.Name);
                    continue;
                }
                forwarded.Add(rehomed);
            }
        }
        return (forwarded.ToImmutable(), null);
    }
}
