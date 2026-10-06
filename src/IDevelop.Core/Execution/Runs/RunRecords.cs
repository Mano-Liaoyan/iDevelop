using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal readonly record struct RunId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

internal readonly record struct InputId(Guid Value);

internal readonly record struct ResultId(Guid Value);

internal readonly record struct OperationId(Guid Value);

internal readonly record struct RevisionId(string Sha256);

internal readonly record struct Digest(string Sha256);

internal readonly record struct CommitId(string Hex);

internal readonly record struct LaunchKey(AttemptId Attempt, int Turn);

internal sealed record ApprovedRevision(RevisionId Id, Workflow Snapshot);

internal enum BaseChoice { Head, Snapshot }

internal sealed record RunBase(CommitId Commit, BaseChoice Choice);

internal sealed record LogCheckpoint(long ByteLength, Digest Content);

internal sealed record RunBinding(WorkflowId Workflow, RunId Run, RevisionId Revision, InputId InitialInputs);

internal sealed record StandaloneCapture(TaskDefinition Definition, string Inputs);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Owned), "owned")]
[JsonDerivedType(typeof(Standalone), "standalone")]
internal abstract record AttemptSource
{
    private AttemptSource() { }

    internal sealed record Owned(WorkflowId Workflow, RunId Run, TaskId Task, AttemptId Attempt) : AttemptSource;

    internal sealed record Standalone(TaskId Task, AttemptId Attempt) : AttemptSource;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Initial), "initial")]
[JsonDerivedType(typeof(Retry), "retry")]
[JsonDerivedType(typeof(Continue), "continue")]
[JsonDerivedType(typeof(ReviewFix), "reviewFix")]
internal abstract record AttemptCause
{
    private AttemptCause() { }

    internal sealed record Initial : AttemptCause;

    internal sealed record Retry(AttemptId Previous, OperationId Confirmation) : AttemptCause;

    internal sealed record Continue(AttemptId Previous, OperationId Confirmation) : AttemptCause;

    internal sealed record ReviewFix(ReviewLink Link) : AttemptCause;
}

internal sealed record RunAttempt(AttemptId Id, TaskId Task, RevisionId Revision, InputId InitialInputs, AttemptCause Cause);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Provided), "provided")]
[JsonDerivedType(typeof(MissingContext), "missingContext")]
internal abstract record InputBinding
{
    private InputBinding() { }

    internal sealed record Provided(ConnectionKey Edge, ConnectionKind Kind, ResultId Result) : InputBinding;

    internal sealed record MissingContext(ConnectionKey Edge) : InputBinding;
}

internal sealed record InputRecord(InputId Id, TaskId Task, RevisionId Revision,
    ImmutableArray<InputBinding> Bindings, CommitId CodeBase, string Text);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Person), "person")]
[JsonDerivedType(typeof(Planner), "planner")]
internal abstract record AmendmentOrigin
{
    private AmendmentOrigin() { }

    internal sealed record Person : AmendmentOrigin;

    internal sealed record Planner(AttemptId Attempt, int Turn) : AmendmentOrigin;
}

internal enum TerminalAttemptOutcome { Succeeded, Failed, Cancelled, Interrupted }

internal enum RecoveryOutcome { NotStarted, Stopped }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Logged), "logged")]
[JsonDerivedType(typeof(Recovered), "recovered")]
internal abstract record AttemptEnd
{
    private AttemptEnd() { }

    internal sealed record Logged(TerminalAttemptOutcome Outcome, LogCheckpoint Evidence) : AttemptEnd;

    internal sealed record Recovered(RecoveryOutcome Outcome, OperationId Confirmation, string Reason) : AttemptEnd;
}

internal sealed record ReuseEvidence(LogCheckpoint SourceLog, Digest Definition, Digest Inputs, Digest CodeTree, OperationId Confirmation);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Executed), "executed")]
[JsonDerivedType(typeof(Reused), "reused")]
internal abstract record ResultOrigin
{
    private ResultOrigin() { }

    internal sealed record Executed(AttemptId Attempt) : ResultOrigin;

    internal sealed record Reused(AttemptSource Source, ReuseEvidence Evidence) : ResultOrigin;
}

internal sealed record ResultRecord(ResultId Id, TaskId Task, RevisionId Revision, InputId Inputs,
    ResultOrigin Origin, string Report, ResultId? Supersedes);

internal enum RunPhase { Approved, StopRequested, Completed, Stopped, Failed, Abandoned }

internal enum RunOutcome { Completed, Stopped, Failed }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Approved), "approved")]
[JsonDerivedType(typeof(Amended), "amended")]
[JsonDerivedType(typeof(Reserved), "reserved")]
[JsonDerivedType(typeof(TurnClaimed), "turnClaimed")]
[JsonDerivedType(typeof(TurnClosed), "turnClosed")]
[JsonDerivedType(typeof(AttemptClosed), "attemptClosed")]
[JsonDerivedType(typeof(ResultAccepted), "resultAccepted")]
[JsonDerivedType(typeof(StopRequested), "stopRequested")]
[JsonDerivedType(typeof(Settled), "settled")]
[JsonDerivedType(typeof(Abandoned), "abandoned")]
internal abstract record RunEvent
{
    private RunEvent() { }

    internal sealed record Approved(RunId Run, ApprovedRevision Revision, RunBase Base) : RunEvent;

    internal sealed record Amended(RevisionId Previous, ApprovedRevision Revision, AmendmentOrigin Origin, OperationId Confirmation) : RunEvent;

    internal sealed record Reserved(RunAttempt Attempt, InputRecord Inputs) : RunEvent;

    internal sealed record TurnClaimed(LaunchKey Key, InputRecord Inputs, Digest Prompt) : RunEvent;

    internal sealed record TurnClosed(LaunchKey Key, LogCheckpoint Evidence) : RunEvent;

    internal sealed record AttemptClosed(AttemptId Attempt, AttemptEnd End) : RunEvent;

    internal sealed record ResultAccepted(ResultRecord Result, InputRecord Inputs) : RunEvent;

    internal sealed record StopRequested : RunEvent;

    internal sealed record Settled(RunOutcome Outcome) : RunEvent;

    internal sealed record Abandoned(OperationId Confirmation, string Reason) : RunEvent;
}

internal sealed record RunEntry(int Schema, long Sequence, OperationId Operation, Digest Command, DateTimeOffset At, RunEvent Event);

internal enum RunProblem
{
    InvalidData, UnsupportedSchema, UnsupportedEvent, SequenceGap, IncompleteTail, RevisionMismatch,

    NotApproved, IdentityMismatch, OperationConflict, StartConflict, ReplacementConflict, RevisionConflict,

    StartedTaskChanged, MissingDependencyResult, StaleInput, InputConflict, UnknownAttempt, UnknownInput,

    UnknownResult, InvalidClaim, OutcomeMismatch, EvidenceMismatch, RecoveryEvidenceInsufficient,

    ConfirmationRequired, TaskBusy, UnresolvedOwnership, RunStopped, RunBusy, UnclosedAttempts,

    IncompleteResults, UnsupportedWork, TaskUnconfigured, UnsupportedResult, ReuseUnverifiable, JournalBusy, StorageUnavailable,
}

internal sealed record RunRejection(RunProblem Problem, long Sequence = 0, TaskId? Task = null);

internal abstract record RunRead
{
    private RunRead() { }

    internal sealed record Loaded(RunRecord Record) : RunRead;

    internal sealed record Rejected(RunRejection Reason, RunRecord? Prefix = null) : RunRead;
}

internal abstract record RunDecision
{
    private RunDecision() { }

    internal sealed record Created(RunRecord Record, RunEvent Event) : RunDecision;

    internal sealed record Existing(RunRecord Record, RunEvent Event) : RunDecision;

    internal sealed record Granted(RunRecord Record, RunEvent.TurnClaimed Claim) : RunDecision;

    internal sealed record Recorded(RunRecord Record, RunEvent Event) : RunDecision;

    internal sealed record Rejected(RunRejection Reason) : RunDecision;
}

internal abstract record RecoveryRead
{
    private RecoveryRead() { }

    internal sealed record Loaded(ImmutableArray<AttemptRecovery> Attempts) : RecoveryRead;

    internal sealed record Rejected(RunRejection Reason) : RecoveryRead;
}

internal enum RecoveryState { RequestMissing, Reserved, Uncertain, Closed }

internal sealed record AttemptRecovery(AttemptId Attempt, RecoveryState State, ImmutableArray<LaunchKey> Claims);

/// <summary>The immutable run history, with current results and staleness derived from it.</summary>
internal sealed record RunRecord(RunId Id, WorkflowId Workflow, RunBase Base, ApprovedRevision Revision)
{
    public RunPhase Phase { get; internal init; } = RunPhase.Approved;

    public long Sequence
    {
        get; internal init;
    }

    public ImmutableDictionary<RevisionId, ApprovedRevision> Revisions
    {
        get; internal init;
    } =
        ImmutableDictionary<RevisionId, ApprovedRevision>.Empty.Add(Revision.Id, Revision);

    public ImmutableDictionary<InputId, InputRecord> Inputs { get; internal init; } = ImmutableDictionary<InputId, InputRecord>.Empty;

    public ImmutableDictionary<AttemptId, RunAttempt> Attempts { get; internal init; } = ImmutableDictionary<AttemptId, RunAttempt>.Empty;

    public ImmutableDictionary<LaunchKey, RunEvent.TurnClaimed> Claims { get; internal init; } = ImmutableDictionary<LaunchKey,
        RunEvent.TurnClaimed>.Empty;

    public ImmutableDictionary<LaunchKey, LogCheckpoint> TurnClosures { get; internal init; } = ImmutableDictionary<LaunchKey, LogCheckpoint>.Empty;

    public ImmutableDictionary<AttemptId, AttemptEnd> Closures { get; internal init; } = ImmutableDictionary<AttemptId, AttemptEnd>.Empty;

    public ImmutableList<ResultRecord> Results { get; internal init; } = [];

    public ImmutableDictionary<OperationId, RunEntry> Receipts { get; internal init; } = ImmutableDictionary<OperationId, RunEntry>.Empty;

    public ImmutableDictionary<TaskId, ResultRecord> CurrentResults => Results
        .GroupBy(result => result.Task).ToImmutableDictionary(group => group.Key, group => group.Last());

    public ImmutableHashSet<ResultId> StaleResults
    {
        get
        {
            var stale = Results.Where(result => result.Supersedes is not null).Select(result => result.Supersedes!.Value).ToHashSet();
            bool changed;
            do
            {
                changed = false;
                foreach (var result in Results)
                {
                    if (Inputs[result.Inputs].Bindings.OfType<InputBinding.Provided>().Any(input => stale.Contains(input.Result)))
                    {
                        changed |= stale.Add(result.Id);
                    }
                }
            } while (changed);
            return stale.ToImmutableHashSet();
        }
    }

    public bool IsStale(InputId inputs) => Inputs[inputs].Bindings.OfType<InputBinding.Provided>().Any(input => StaleResults.Contains(input.Result));

    public ImmutableArray<LaunchKey> UnresolvedClaims => [.. Claims.Keys.Where(key =>
        !TurnClosures.ContainsKey(key) && !Closures.ContainsKey(key.Attempt)).OrderBy(key => key.Attempt.Value).ThenBy(key => key.Turn)];

    public ReviewLink? ReviewOf(AttemptId attempt) => Attempts[attempt].Cause switch
    {
        AttemptCause.ReviewFix fix => fix.Link,
        AttemptCause.Retry retry => ReviewOf(retry.Previous),
        AttemptCause.Continue continued => ReviewOf(continued.Previous),
        _ => null,
    };
}
