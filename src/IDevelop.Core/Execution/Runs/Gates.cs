using System.Collections.Immutable;
using IDevelop.Nodes;
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

/// <summary>
/// A request and its answer. <see cref="AskedAgain"/> is set once the person asked again after sending it back: the request
/// stays sent back in the run's history, and the node takes a new request on the same inputs.
/// </summary>
internal sealed record GateState(GateRequest Request, GateDecision? Decision)
{
    public bool AskedAgain { get; init; }
}

/// <summary>A person's answer to a request.</summary>
internal abstract record GateAnswer
{
    private GateAnswer() { }

    internal sealed record Approve : GateAnswer;

    internal sealed record SendBack(string Reason) : GateAnswer;
}

/// <summary>
/// One input that approving a request hands on, as the person reads it before answering: the heading the approval's report
/// gives it, its task's title and connection kind, then its report. <paramref name="Review"/> is a review's conclusion, or
/// null for any other input and for a review whose record cannot be read.
/// </summary>
internal sealed record GateInput(string Heading, string Report, HandedReview? Review)
{
    /// <summary>The input as the approval's report records it.</summary>
    public string Markdown => $"## {Heading}\n\n{Report}";
}

/// <summary>
/// A review's conclusion as an approval hands it on: the verdict it agreed with in round <paramref name="Round"/>, and every
/// finding it raised, each with how it was settled.
/// </summary>
internal sealed record HandedReview(Verdict Verdict, int Round, ImmutableArray<Finding> Findings);

/// <summary>
/// What an approval hands on: the code of its inputs, their dependency artifacts under the approval's result, and their
/// reports. The person decides on these, so the request fixes them and the approval records them unchanged.
/// </summary>
internal static class GateForwarding
{
    /// <summary>Each input's report under its task's title, in binding order, unshortened.</summary>
    public static string Report(RunRecord record, InputRecord inputs) =>
        string.Join("\n\n", Inputs(record, inputs, _ => null).Select(input => input.Markdown));

    /// <summary>
    /// Each input as <see cref="Report"/> records it, and for a review's input the review's conclusion, folded from the log of
    /// the review attempt that <paramref name="reviewLog"/> finds for its result. A log that cannot be read, or that holds no
    /// verdict, gives no conclusion.
    /// </summary>
    public static ImmutableArray<GateInput> Inputs(RunRecord record, InputRecord inputs, Func<ResultRecord, AttemptRecord?> reviewLog)
    {
        var workflow = record.Revisions[inputs.Revision].Snapshot;
        return [.. inputs.Bindings.Select(binding => binding switch
        {
            InputBinding.Provided provided => Provided(record, workflow.Tasks[provided.Edge.From], provided, reviewLog),
            InputBinding.MissingContext missing => new GateInput($"{workflow.Tasks[missing.Edge.From].Title} (context)", "Context was unavailable.", null),
            _ => throw new InvalidOperationException("Unknown input binding."),
        })];
    }

    private static GateInput Provided(RunRecord record, TaskDefinition from, InputBinding.Provided provided, Func<ResultRecord, AttemptRecord?> reviewLog)
    {
        var result = record.Results.Single(result => result.Id == provided.Result);
        return new GateInput($"{from.Title} ({provided.Kind.ToString().ToLowerInvariant()})", result.Report,
            from.Blueprint.Work is WorkSpec.Review ? Conclusion(reviewLog(result)) : null);
    }

    private static HandedReview? Conclusion(AttemptRecord? log) =>
        log is not null && ReviewLedger.Fold(log) is { Rounds: [.., { Verdict: { } verdict }] } ledger
            ? new HandedReview(verdict, ledger.Round, ledger.Findings)
            : null;

    /// <summary>The code an approval forwards: its inputs' code, or none on the approved base.</summary>
    public static CodeOutput? Code(InputRecord inputs) =>
        inputs.Code is CodeSelection.Single or CodeSelection.Joined ? new CodeOutput.Forwarded(inputs.Id) : null;

    /// <summary>
    /// The dependency inputs' artifacts, stored under <paramref name="result"/> with their names, bytes, and digests kept.
    /// An artifact reached through two inputs counts once. Two different artifacts whose names differ at most in case are a
    /// collision: the approval could not hand on both, so <c>Collision</c> names the second.
    /// </summary>
    public static (ImmutableArray<ArtifactRecord> Artifacts, string? Collision) Artifacts(RunRecord record, InputRecord inputs, ResultId result) =>
        ArtifactForwarding.Artifacts(record, inputs.Bindings, result);
}
