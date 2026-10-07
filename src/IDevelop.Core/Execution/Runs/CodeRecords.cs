using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal readonly record struct TreeId(string Hex);

/// <summary>One accepted dependency result and the writer whose commit it resolves to.</summary>
internal sealed record CodeSource(TaskId Task, ResultId Result, TaskId Owner, CommitId AttemptBase, CommitId Commit);

internal sealed record JoinRecord(OperationId Operation, ImmutableArray<CodeSource> Sources, CommitId Commit, TreeId Tree, string Ref);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Root), "root")]
[JsonDerivedType(typeof(Single), "single")]
[JsonDerivedType(typeof(Joined), "joined")]
internal abstract record CodeSelection
{
    private CodeSelection() { }

    internal sealed record Legacy(CommitId Base) : CodeSelection;

    internal sealed record Root(CommitId Commit) : CodeSelection;

    internal sealed record Single(CodeSource Source) : CodeSelection;

    internal sealed record Joined(JoinRecord Join) : CodeSelection;
}

internal sealed record ArtifactRecord(string Name, string StoredPath, Digest Content, long ByteLength);

internal sealed record DeliveredFile(ResultId Source, string RelativePath, Digest Content, long ByteLength);

internal sealed record ReviewInput(TaskId Subject, ResultId SubjectResult);

internal sealed record OwnedCode(TaskId Owner, AttemptId Attempt, CommitId AttemptBase, CommitId Commit, TreeId Tree, string ResultRef);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Produced), "produced")]
[JsonDerivedType(typeof(Forwarded), "forwarded")]
internal abstract record CodeOutput
{
    private CodeOutput() { }

    internal sealed record Produced(OwnedCode Code) : CodeOutput;

    internal sealed record Forwarded(InputId Inputs) : CodeOutput;
}

internal sealed record WorktreeOwner(TaskId Task, string RelativePath, string Branch);

internal sealed record ExecutionLocation(WorktreeOwner Owner, CommitId AttemptBase);

internal sealed record PreparedExecution(LaunchKey Launch, InputId Inputs, ExecutionLocation Location, string Prompt, Digest PromptHash,
    string OutboxPath);

internal sealed record CommitRecipe(TreeId Tree, ImmutableArray<CommitId> Parents, string Message, string Author, string Committer,
    DateTimeOffset Timestamp);

internal sealed record RefChange(string Ref, CommitId? Expected, CommitId Target);

internal sealed record EvidenceFile(string RelativePath, Digest Content, long ByteLength);

internal sealed record JoinRequest(WorkflowId Workflow, OperationId Operation, RunId Run, TaskId Task, InputId Inputs, ImmutableArray<CodeSource> Sources,
    CommitId? ExpectedJoin);

internal abstract record JoinOutcome
{
    private JoinOutcome() { }

    internal sealed record Ready(JoinRecord Join) : JoinOutcome;

    internal sealed record Blocked(MaterializationBlock Block) : JoinOutcome;
}

internal interface IJoinComposer
{
    ValueTask<JoinOutcome> Compose(JoinRequest request, CancellationToken cancellation);
}

internal enum MaterializationProblem
{
    JoinRequired, FanInConflict, UncertainOwnership, LiveWriter, DirtyWorktree, InputUnavailable, SubmoduleUnavailable,

    NotRepositoryRoot, GitVersionUnsupported, GitFailed,
}

internal sealed record StageEntry(string Mode, string Object, int Stage, string Path);

internal sealed record MergeMessage(ImmutableArray<string> Paths, string Type, string Text);

internal sealed record ConfigEntry(string Key, string Value);

/// <summary>A fan-in conflict as Git reported it. E2a stores and replays it without interpreting the merge.</summary>
internal sealed record ConflictEvidence(ImmutableArray<CodeSource> Sources, int Step, ImmutableArray<string> Paths,
    ImmutableArray<StageEntry> Stages, ImmutableArray<MergeMessage> Messages, EvidenceFile Stdout, EvidenceFile Stderr, string GitVersion,
    ImmutableArray<ConfigEntry> MergeConfig);

internal sealed record MaterializationBlock(OperationId Operation, TaskId Task, AttemptId? Attempt, MaterializationProblem Problem,
    InputId? Inputs, ImmutableArray<EvidenceFile> Evidence, string Detail, ConflictEvidence? Conflict = null);
