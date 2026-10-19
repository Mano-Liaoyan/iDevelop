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

/// <summary>The commit a run starts from, the current branch's HEAD at its approval.</summary>
internal sealed record RunBase(CommitId Commit)
{
    /// <summary>
    /// The branch HEAD named at the approval, such as <c>refs/heads/main</c>, which the run's finished work merges into.
    /// Null when HEAD was detached, and then nothing merges.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Branch { get; init; }
}

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
[JsonDerivedType(typeof(Merge), "merge")]
internal abstract record AttemptCause
{
    private AttemptCause() { }

    /// <summary>A Merge node's attempt that resolves <paramref name="Conflict"/>.</summary>
    internal sealed record Merge(MergeConflict Conflict) : AttemptCause;

    internal sealed record Initial : AttemptCause;

    internal sealed record Retry(AttemptId Previous, OperationId Confirmation) : AttemptCause
    {
        /// <summary>The person's note to the agent, which its first prompt ends with. Null when they wrote none (#126).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; init; }
    }

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
    /// <summary>
    /// The conflict a Merge node's attempt resolves, which is all its inputs are: it takes no results from the tasks before
    /// it. Null for every other attempt.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MergeConflict? Conflict { get; init; }

    [JsonIgnore]
    public CommitId CodeBase => Code switch
    {
        CodeSelection.Root root => root.Commit,
        CodeSelection.Single single => single.Source.Commit,
        CodeSelection.Joined joined => joined.Join.Commit,
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

/// <summary>What the person chose from a proposal: its items, in id order, and the agent a new task takes when its type has none.</summary>
internal sealed record AmendmentChoice(ImmutableArray<TaskId> Chosen, ExecutionSettings? Fallback)
{
    /// <summary>The planner's choices that the person's review found usable, by task (#99). Null for none, as before.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ChosenAgent>? Planned { get; init; }

    /// <summary>The person's own choices in the review, by task, which win over every other agent (#99). Null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ChosenAgent>? Changed { get; init; }

    /// <summary>The choices in task id order, or null for none, so an acceptance without agents records as it always did.</summary>
    public static ImmutableArray<ChosenAgent>? Agents(IReadOnlyDictionary<TaskId, ExecutionSettings>? agents) =>
        agents is null || agents.Count == 0 ? null : [.. agents.OrderBy(pair => pair.Key).Select(pair => new ChosenAgent(pair.Key, pair.Value))];
}

/// <summary>The agent a task takes from a proposal's review.</summary>
internal sealed record ChosenAgent(TaskId Task, ExecutionSettings Agent);

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

/// <summary>
/// What a turn left in its worktree when it ended, captured once: the commit an editing task's turn ends with, its report,
/// and its artifacts (ADR 0032). A capture that could not be taken records a block under <see cref="RunReducer.CaptureBlock"/>.
/// </summary>
internal sealed record CaptureObservation(CaptureId Capture, LaunchKey Launch, LogCheckpoint Log,
    DateTimeOffset Started, DateTimeOffset Completed, CommitRecipe Recipe, CommitId? Candidate, CommitId? Tip, string? Head,
    EvidenceFile? Index, string? Report, ImmutableArray<ArtifactRecord> Artifacts, EvidenceFile SharedRefs, ImmutableArray<string> UnexplainedRefs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TreeId? IndexTree { get; init; }
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

/// <summary>
/// What a run checked before including a standalone planner's report: the log as it was read, its final proposal turn,
/// the approved task, the digest of the proposal and of the part of it the approved workflow holds, the code it read, and
/// the person's confirmation.
/// </summary>
internal sealed record InclusionEvidence(LogCheckpoint SourceLog, int Turn, Digest Definition, Digest Selection, Digest CodeTree,
    OperationId Confirmation);

/// <summary>A report the person includes in a run at confirmation: <paramref name="Source"/>'s report, as of its turn <paramref name="Turn"/>.</summary>
internal sealed record ReportInclusion(TaskId Task, AttemptId Source, int Turn);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Executed), "executed")]
[JsonDerivedType(typeof(Reused), "reused")]
[JsonDerivedType(typeof(Included), "included")]
[JsonDerivedType(typeof(Human), "human")]
[JsonDerivedType(typeof(Carried), "carried")]
[JsonDerivedType(typeof(Merged), "merged")]
internal abstract record ResultOrigin
{
    private ResultOrigin() { }

    /// <summary>
    /// A Merge node's step: it handed on the merge of its predecessors' code, as its request <paramref name="Request"/>
    /// composed it, with their reports and artifacts. No client ran for the step itself.
    /// </summary>
    internal sealed record Merged(GateId Request) : ResultOrigin;

    internal sealed record Executed(AttemptId Attempt) : ResultOrigin;

    internal sealed record Reused(AttemptSource Source, ReuseEvidence Evidence) : ResultOrigin;

    /// <summary>A standalone planner's report, included at approval. No code, ownership, or session comes with it.</summary>
    internal sealed record Included(AttemptSource.Standalone Source, InclusionEvidence Evidence) : ResultOrigin;

    /// <summary>A person approved this Approval node's request. No client ran.</summary>
    internal sealed record Human(GateId Request) : ResultOrigin;

    /// <summary>
    /// The current result <paramref name="Result"/> of the earlier run <paramref name="Run"/>, which a run that a node's Run
    /// started takes as the result of a task it does not run (#90). Its report and artifacts are copied; it brings no code.
    /// No client ran in this run.
    /// </summary>
    internal sealed record Carried(RunId Run, ResultId Result) : ResultOrigin;
}

/// <summary>A result the approval itself records, with its empty inputs.</summary>
internal sealed record IncludedResult(ResultRecord Result, InputRecord Inputs);

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
[JsonDerivedType(typeof(BlockResolved), "blockResolved")]
[JsonDerivedType(typeof(OwnershipFenced), "ownershipFenced")]
[JsonDerivedType(typeof(RootExitObserved), "rootExitObserved")]
[JsonDerivedType(typeof(TurnCaptured), "turnCaptured")]
[JsonDerivedType(typeof(GateRequested), "gateRequested")]
[JsonDerivedType(typeof(GateSentBack), "gateSentBack")]
[JsonDerivedType(typeof(GateAskedAgain), "gateAskedAgain")]
[JsonDerivedType(typeof(Requested), "requested")]
[JsonDerivedType(typeof(ConflictResolved), "conflictResolved")]
internal abstract record RunEvent
{
    private RunEvent() { }

    internal sealed record LayoutAllocated(LayoutKey Key) : RunEvent;

    internal sealed record Planned(MaterializationPlan Plan) : RunEvent;

    internal sealed record GitIntended(OperationId Plan, GitMutation Mutation) : RunEvent;

    internal sealed record GitObserved(OperationId Mutation, GitObservation Observation) : RunEvent;

    internal sealed record Prepared(PreparedExecution Execution) : RunEvent;

    internal sealed record Blocked(MaterializationBlock Block) : RunEvent;

    internal sealed record BlockResolved(OperationId Block, string Reason) : RunEvent;

    /// <summary>
    /// The person resolved <paramref name="Conflict"/> in its worktree and marked it resolved: <paramref name="Merge"/> is the
    /// commit of their files on the conflict's two parents, which serves as a Merge node's merge does (#148).
    /// </summary>
    internal sealed record ConflictResolved(MergeConflict Conflict, CommitId Merge) : RunEvent;

    internal sealed record Approved(RunId Run, ApprovedRevision Revision, RunBase Base) : RunEvent
    {
        /// <summary>The reports the person included at confirmation, accepted with the approval. Null when none was.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ImmutableArray<IncludedResult>? Included { get; init; }

        /// <summary>
        /// The node whose Run started the run. Null for Run Workflow, which runs every root. A run
        /// started from a node starts only that node and the tasks after it, each once its predecessors have results.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TaskId? Node { get; init; }

        /// <summary>
        /// The results of earlier runs that a node's run takes for the tasks it does not run, each <see cref="ResultOrigin.Carried"/>,
        /// in the order their inputs need them. Null when it carries none, and always for Run Workflow (#90).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ImmutableArray<IncludedResult>? Carried { get; init; }
    }

    internal sealed record Amended(RevisionId Previous, ApprovedRevision Revision, AmendmentOrigin Origin, OperationId Confirmation) : RunEvent
    {
        /// <summary>For a planner's amendment, the items the person chose and the agent new tasks took. Null for a person's.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AmendmentChoice? Choice { get; init; }
    }

    internal sealed record Reserved(RunAttempt Attempt, InputRecord Inputs) : RunEvent;

    internal sealed record TurnClaimed(LaunchKey Key, InputRecord Inputs, Digest Prompt) : RunEvent;

    internal sealed record RootExitObserved(LaunchKey Launch, RootExit Exit, DateTimeOffset At, CommitId Tip, string? Head,
        TipOwnership Ownership) : RunEvent;

    internal sealed record TurnCaptured(CaptureObservation Observation) : RunEvent;

    internal sealed record TurnClosed(LaunchKey Key, LogCheckpoint Evidence) : RunEvent;

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

    /// <summary>
    /// The person asked again after sending the request back. The request stays sent back, and the node takes a new request
    /// on the same inputs (#126).
    /// </summary>
    internal sealed record GateAskedAgain(GateId Request) : RunEvent;

    /// <summary>
    /// The person ran another node of a run that a node's Run started, which adds that node to the run. Its dependency
    /// predecessors all had current results when it was recorded.
    /// </summary>
    internal sealed record Requested(TaskId Task) : RunEvent
    {
        /// <summary>The results of earlier runs that the tasks it adds need, carried as an approval carries them. Null when none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ImmutableArray<IncludedResult>? Carried { get; init; }
    }
}

internal sealed record RunEntry(int Schema, long Sequence, OperationId Operation, Digest Command, DateTimeOffset At, RunEvent Event);

internal enum RunProblem
{
    InvalidData, UnsupportedSchema, UnsupportedEvent, SequenceGap, IncompleteTail, RevisionMismatch,

    NotApproved, IdentityMismatch, OperationConflict, StartConflict, ReplacementConflict, RevisionConflict,

    StartedTaskChanged, MissingDependencyResult, StaleInput, InputConflict, UnknownAttempt, UnknownInput,

    UnknownResult, InvalidClaim, OutcomeMismatch, EvidenceMismatch, RecoveryEvidenceInsufficient,

    ConfirmationRequired, TaskBusy, UnresolvedOwnership, RunStopped, RunBusy, UnclosedAttempts,

    IncompleteResults, UnsupportedWork, TaskUnconfigured, UnsupportedResult, ReuseUnverifiable, JournalBusy, StorageUnavailable, NotSettled, SettlementPending, SessionUnavailable,

    /// <summary>The task is not part of a run that a node's Run started: nobody ran it, and no task before it is part of it.</summary>
    NotRequested,

    /// <summary>
    /// The run's journal is of an earlier format (#138). It stays on disk; the app reads nothing from it, writes nothing to it,
    /// and lets it block no workflow.
    /// </summary>
    EarlierFormat,
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
    public string? RunKey { get; internal init; }

    public string? Repository { get; internal init; }

    public ImmutableDictionary<TaskId, string> TaskKeys { get; internal init; } = ImmutableDictionary<TaskId, string>.Empty;

    /// <summary>
    /// The nodes the person ran, for a run that a node's Run started, or null for a run of every root, as Run Workflow
    /// starts (#90). <see cref="RunScope"/> derives which tasks may start from it.
    /// </summary>
    public ImmutableSortedSet<TaskId>? Requested { get; internal init; }

    public ImmutableDictionary<OperationId, MaterializationPlan> Plans { get; internal init; } = ImmutableDictionary<OperationId, MaterializationPlan>.Empty;

    public ImmutableDictionary<OperationId, RunEvent.GitIntended> GitIntents { get; internal init; } = ImmutableDictionary<OperationId, RunEvent.GitIntended>.Empty;

    public ImmutableDictionary<OperationId, GitObservation> GitObservations { get; internal init; } = ImmutableDictionary<OperationId, GitObservation>.Empty;

    public ImmutableDictionary<LaunchKey, PreparedExecution> Preparations { get; internal init; } = ImmutableDictionary<LaunchKey, PreparedExecution>.Empty;

    public ImmutableDictionary<OperationId, MaterializationBlockState> Blocks { get; internal init; } = ImmutableDictionary<OperationId, MaterializationBlockState>.Empty;

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

    /// <summary>The one capture each turn took when its client exited, by turn.</summary>
    public ImmutableDictionary<LaunchKey, CaptureObservation> Captures { get; internal init; } =
        ImmutableDictionary<LaunchKey, CaptureObservation>.Empty;

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

    /// <summary>
    /// The review round <paramref name="attempt"/> fixes: a fix's own, or the round of the fix that a Retry or a Continue
    /// takes over. A Retry of an attempt that published a result redoes the task, and fixes no round (#125).
    /// </summary>
    public ReviewLink? ReviewOf(AttemptId attempt) => Attempts[attempt].Cause switch
    {
        AttemptCause.ReviewFix fix => fix.Link,
        AttemptCause.Retry retry when Results.Any(result => result.Origin is ResultOrigin.Executed executed && executed.Attempt == retry.Previous) => null,
        AttemptCause.Retry retry => ReviewOf(retry.Previous),
        AttemptCause.Continue continued => ReviewOf(continued.Previous),
        _ => null,
    };
}
