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
    ImmutableArray<InputBinding> Bindings, CodeSelection Code, string Text, ImmutableArray<DeliveredFile> Files, ReviewInput? Review)
{
    [JsonIgnore]
    public CommitId CodeBase => Code switch
    {
        CodeSelection.Root root => root.Commit,
        CodeSelection.Single single => single.Source.Commit,
        CodeSelection.Joined joined => joined.Join.Commit,
        CodeSelection.Legacy legacy => legacy.Base,
        _ => throw new InvalidOperationException("Unknown code selection."),
    };
}

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

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Exited), "exited")]
[JsonDerivedType(typeof(NotStarted), "notStarted")]
internal abstract record RootExit
{
    private RootExit() { }

    internal sealed record Exited(int Code) : RootExit;

    internal sealed record NotStarted(string Detail) : RootExit;
}

internal readonly record struct CaptureId(Guid Value);

internal sealed record CaptureObservation(CaptureId Capture, int Ordinal, LaunchKey Launch, LogCheckpoint Log,
    DateTimeOffset Started, DateTimeOffset Completed, CommitRecipe Recipe, CommitId Candidate, CommitId? Tip, string? Head,
    EvidenceFile? Index, string? Report, ImmutableArray<ArtifactRecord> Artifacts, EvidenceFile SharedRefs, ImmutableArray<string> UnexplainedRefs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Recovery { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TreeId? IndexTree { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Matched), "matched")]
[JsonDerivedType(typeof(Diverged), "diverged")]
[JsonDerivedType(typeof(Failed), "failed")]
internal abstract record CaptureDisposition
{
    private CaptureDisposition() { }

    internal sealed record Matched : CaptureDisposition;

    internal sealed record Diverged(MaterializationProblem Problem, ImmutableArray<string> Paths, ImmutableArray<string> Refs,
        string Detail) : CaptureDisposition;

    internal sealed record Failed(MaterializationProblem Problem, string Detail, ImmutableArray<EvidenceFile> Evidence) : CaptureDisposition;
}

internal enum TipOwnership { Explained, Unexplained }

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
[JsonDerivedType(typeof(Rebased), "rebased")]
[JsonDerivedType(typeof(Human), "human")]
internal abstract record ResultOrigin
{
    private ResultOrigin() { }

    internal sealed record Executed(AttemptId Attempt) : ResultOrigin;

    internal sealed record Reused(AttemptSource Source, ReuseEvidence Evidence) : ResultOrigin;

    /// <summary>
    /// A person approved replaying <paramref name="Source"/>'s recorded change onto its updated inputs, as the rebase
    /// plan <paramref name="Plan"/> recorded. No client ran; the source's provenance stays with the source.
    /// </summary>
    internal sealed record Rebased(ResultId Source, OperationId Plan) : ResultOrigin;

    /// <summary>A person approved this Approval node's request. No client ran.</summary>
    internal sealed record Human(GateId Request) : ResultOrigin;
}

internal sealed record ResultRecord(ResultId Id, TaskId Task, RevisionId Revision, InputId Inputs,
    ResultOrigin Origin, string Report, ResultId? Supersedes)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodeOutput? Code { get; init; }

    public ImmutableArray<ArtifactRecord> Artifacts { get; init; } = [];
}

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
[JsonDerivedType(typeof(LayoutAllocated), "layoutAllocated")]
[JsonDerivedType(typeof(Planned), "planned")]
[JsonDerivedType(typeof(GitIntended), "gitIntended")]
[JsonDerivedType(typeof(GitObserved), "gitObserved")]
[JsonDerivedType(typeof(Prepared), "prepared")]
[JsonDerivedType(typeof(Blocked), "blocked")]
[JsonDerivedType(typeof(SalvageRetained), "salvageRetained")]
[JsonDerivedType(typeof(Preserved), "preserved")]
[JsonDerivedType(typeof(Restored), "restored")]
[JsonDerivedType(typeof(RecoveryBaselined), "recoveryBaselined")]
[JsonDerivedType(typeof(PreservationDiverged), "preservationDiverged")]
[JsonDerivedType(typeof(BlockResolved), "blockResolved")]
[JsonDerivedType(typeof(OwnershipFenced), "ownershipFenced")]
[JsonDerivedType(typeof(RootExitObserved), "rootExitObserved")]
[JsonDerivedType(typeof(TurnCaptured), "turnCaptured")]
[JsonDerivedType(typeof(CaptureDisposed), "captureDisposed")]
[JsonDerivedType(typeof(GateRequested), "gateRequested")]
[JsonDerivedType(typeof(GateSentBack), "gateSentBack")]
internal abstract record RunEvent
{
    private RunEvent() { }

    internal sealed record LayoutAllocated(LayoutKey Key) : RunEvent;

    internal sealed record Planned(MaterializationPlan Plan) : RunEvent;

    internal sealed record GitIntended(OperationId Plan, GitMutation Mutation) : RunEvent;

    internal sealed record GitObserved(OperationId Mutation, GitObservation Observation) : RunEvent;

    internal sealed record Prepared(PreparedExecution Execution, EvidenceFile SharedRefs) : RunEvent;

    internal sealed record Blocked(MaterializationBlock Block) : RunEvent;

    internal sealed record SalvageRetained(OperationId Plan, string Ref, CommitId Commit) : RunEvent;

    internal sealed record Restored(OperationId Plan, ImmutableArray<OperationId> Resolved) : RunEvent
    {
        public bool Equals(Restored? other) => other is not null && Plan == other.Plan && Resolved.SequenceEqual(other.Resolved);

        public override int GetHashCode() => HashCode.Combine(Plan, Resolved.Length);
    }

    internal sealed record RecoveryBaselined(RecoveryBaseline Baseline, ImmutableArray<OperationId> Resolved) : RunEvent
    {
        public bool Equals(RecoveryBaselined? other) => other is not null && Baseline == other.Baseline && Resolved.SequenceEqual(other.Resolved);

        public override int GetHashCode() => HashCode.Combine(Baseline, Resolved.Length);
    }

    internal sealed record Preserved(OperationId Plan, string Ref, CommitId Commit) : RunEvent;

    internal sealed record PreservationDiverged(OperationId Operation, PreservationObservation First, PreservationObservation Second,
        BlockScope.Checkout Scope) : RunEvent;

    internal sealed record BlockResolved(OperationId Block, string Reason) : RunEvent;

    internal sealed record Approved(RunId Run, ApprovedRevision Revision, RunBase Base) : RunEvent;

    internal sealed record Amended(RevisionId Previous, ApprovedRevision Revision, AmendmentOrigin Origin, OperationId Confirmation) : RunEvent;

    internal sealed record Reserved(RunAttempt Attempt, InputRecord Inputs) : RunEvent;

    internal sealed record TurnClaimed(LaunchKey Key, InputRecord Inputs, Digest Prompt) : RunEvent;

    internal sealed record RootExitObserved(LaunchKey Launch, RootExit Exit, DateTimeOffset At, CommitId Tip, string? Head,
        TipOwnership Ownership) : RunEvent;

    internal sealed record TurnCaptured(CaptureObservation Observation) : RunEvent;

    internal sealed record CaptureDisposed(CaptureId Capture, LaunchKey Launch, CaptureDisposition Disposition) : RunEvent;

    internal sealed record TurnClosed(LaunchKey Key, LogCheckpoint Evidence) : RunEvent
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CaptureId? Capture { get; init; }
    }

    internal sealed record AttemptClosed(AttemptId Attempt, AttemptEnd End) : RunEvent;

    internal sealed record ResultAccepted(ResultRecord Result, InputRecord Inputs) : RunEvent;

    internal sealed record OwnershipFenced(ImmutableArray<LaunchKey> Claims) : RunEvent;

    internal sealed record StopRequested : RunEvent;

    internal sealed record Settled(RunOutcome Outcome) : RunEvent;

    internal sealed record Abandoned(OperationId Confirmation, string Reason) : RunEvent;

    /// <summary>An Approval node asks the person to decide on these inputs. It creates no attempt.</summary>
    internal sealed record GateRequested(GateRequest Request, InputRecord Inputs) : RunEvent;

    /// <summary>The person sent the request back with a reason. Its dependents stay held.</summary>
    internal sealed record GateSentBack(GateId Request, InputId Inputs, string Reason) : RunEvent;
}

internal sealed record RunEntry(int Schema, long Sequence, OperationId Operation, Digest Command, DateTimeOffset At, RunEvent Event);

internal enum RunProblem
{
    InvalidData, UnsupportedSchema, UnsupportedEvent, SequenceGap, IncompleteTail, RevisionMismatch,

    NotApproved, IdentityMismatch, OperationConflict, StartConflict, ReplacementConflict, RevisionConflict,

    StartedTaskChanged, MissingDependencyResult, StaleInput, InputConflict, UnknownAttempt, UnknownInput,

    UnknownResult, InvalidClaim, OutcomeMismatch, EvidenceMismatch, RecoveryEvidenceInsufficient,

    ConfirmationRequired, TaskBusy, UnresolvedOwnership, RunStopped, RunBusy, UnclosedAttempts,

    IncompleteResults, UnfinishedPublication, UnsupportedWork, TaskUnconfigured, UnsupportedResult, ReuseUnverifiable, JournalBusy, StorageUnavailable, NotSettled, SettlementPending, SessionUnavailable,
}

internal sealed record RunRejection(RunProblem Problem, long Sequence = 0, TaskId? Task = null);

internal abstract record TaskRunOwnership
{
    private TaskRunOwnership() { }

    internal sealed record Free : TaskRunOwnership;
    internal sealed record Owned(string Workflow) : TaskRunOwnership;
    internal sealed record Unreadable(string Detail) : TaskRunOwnership;
}

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

internal enum RecoveryState { RequestMissing, Reserved, Uncertain, Closed, Settling }

internal sealed record AttemptRecovery(AttemptId Attempt, RecoveryState State, ImmutableArray<LaunchKey> Claims);

/// <summary>The immutable run history, with current results and staleness derived from it.</summary>
internal sealed record RunRecord(RunId Id, WorkflowId Workflow, RunBase Base, ApprovedRevision Revision)
{
    public int Schema { get; internal init; } = 2;

    public string? RunKey { get; internal init; }

    public string? Repository { get; internal init; }

    public ImmutableDictionary<TaskId, string> TaskKeys { get; internal init; } = ImmutableDictionary<TaskId, string>.Empty;

    public ImmutableDictionary<OperationId, MaterializationPlan> Plans { get; internal init; } = ImmutableDictionary<OperationId, MaterializationPlan>.Empty;

    public ImmutableDictionary<OperationId, RunEvent.GitIntended> GitIntents { get; internal init; } = ImmutableDictionary<OperationId, RunEvent.GitIntended>.Empty;

    public ImmutableDictionary<OperationId, GitObservation> GitObservations { get; internal init; } = ImmutableDictionary<OperationId, GitObservation>.Empty;

    public ImmutableDictionary<LaunchKey, PreparedExecution> Preparations { get; internal init; } = ImmutableDictionary<LaunchKey, PreparedExecution>.Empty;

    public ImmutableDictionary<OperationId, MaterializationBlockState> Blocks { get; internal init; } = ImmutableDictionary<OperationId, MaterializationBlockState>.Empty;

    public ImmutableDictionary<(AttemptId Previous, OperationId Confirmation), RunEvent.RecoveryBaselined> Baselines { get; internal init; } =
        ImmutableDictionary<(AttemptId, OperationId), RunEvent.RecoveryBaselined>.Empty;

    public ImmutableDictionary<OperationId, RunEvent.Restored> Restorations { get; internal init; } = ImmutableDictionary<OperationId, RunEvent.Restored>.Empty;

    public ImmutableDictionary<OperationId, RunEvent.Preserved> Preservations { get; internal init; } = ImmutableDictionary<OperationId, RunEvent.Preserved>.Empty;

    public ImmutableDictionary<OperationId, RunEvent.PreservationDiverged> PreservationDivergences { get; internal init; } = ImmutableDictionary<OperationId, RunEvent.PreservationDiverged>.Empty;

    public ImmutableDictionary<OperationId, RunEvent.SalvageRetained> Salvages { get; internal init; } = ImmutableDictionary<OperationId, RunEvent.SalvageRetained>.Empty;

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

    public ImmutableDictionary<LaunchKey, RunEvent.RootExitObserved> RootExits { get; internal init; } =
        ImmutableDictionary<LaunchKey, RunEvent.RootExitObserved>.Empty;

    public ImmutableDictionary<CaptureId, ImmutableList<CaptureObservation>> Captures { get; internal init; } =
        ImmutableDictionary<CaptureId, ImmutableList<CaptureObservation>>.Empty;

    public ImmutableDictionary<CaptureId, RunEvent.CaptureDisposed> Dispositions { get; internal init; } =
        ImmutableDictionary<CaptureId, RunEvent.CaptureDisposed>.Empty;

    public ImmutableDictionary<LaunchKey, CaptureId> Settlements { get; internal init; } = ImmutableDictionary<LaunchKey, CaptureId>.Empty;

    public ImmutableHashSet<LaunchKey> Fenced { get; internal init; } = ImmutableHashSet<LaunchKey>.Empty;

    public ImmutableDictionary<LaunchKey, LogCheckpoint> TurnClosures { get; internal init; } = ImmutableDictionary<LaunchKey, LogCheckpoint>.Empty;

    public ImmutableDictionary<AttemptId, AttemptEnd> Closures { get; internal init; } = ImmutableDictionary<AttemptId, AttemptEnd>.Empty;

    public ImmutableList<ResultRecord> Results { get; internal init; } = [];

    /// <summary>Every Approval node request and its answer, by request.</summary>
    public ImmutableDictionary<GateId, GateState> Gates { get; internal init; } = ImmutableDictionary<GateId, GateState>.Empty;

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

    public bool Settling(LaunchKey launch) => RootExits.ContainsKey(launch) &&
        !TurnClosures.ContainsKey(launch) && !Closures.ContainsKey(launch.Attempt);

    public ReviewLink? ReviewOf(AttemptId attempt) => Attempts[attempt].Cause switch
    {
        AttemptCause.ReviewFix fix => fix.Link,
        AttemptCause.Retry retry => ReviewOf(retry.Previous),
        AttemptCause.Continue continued => ReviewOf(continued.Previous),
        _ => null,
    };
}
