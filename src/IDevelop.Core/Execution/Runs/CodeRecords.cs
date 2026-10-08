using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal readonly record struct TreeId(string Hex);

internal sealed record CodeSource(TaskId Task, ResultId Result, ImmutableArray<TaskId> Owners, CommitId AttemptBase, CommitId Commit);

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

internal sealed record JoinRequest(CoordinatorPermit Permit, OperationId Operation, TaskId Task, InputId Inputs, ImmutableArray<CodeSource> Sources,
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

    NotRepositoryRoot, GitVersionUnsupported, GitFailed, ArtifactCollision,
}

internal sealed record StageEntry(string Mode, string Object, int Stage, string Path);

internal sealed record MergeMessage(ImmutableArray<string> Paths, string Type, string Text);

internal sealed record ConfigEntry(string Key, string Value);

/// <summary>A fan-in conflict as Git reported it, stored and replayed without interpreting the merge.</summary>
internal sealed record ConflictEvidence(ImmutableArray<CodeSource> Sources, int Step, ImmutableArray<string> Paths,
    ImmutableArray<StageEntry> Stages, ImmutableArray<MergeMessage> Messages, EvidenceFile Stdout, EvidenceFile Stderr, string GitVersion,
    ImmutableArray<ConfigEntry> MergeConfig, CommitId AttributeSource);

internal sealed record MaterializationBlock(OperationId Operation, TaskId Task, AttemptId? Attempt, MaterializationProblem Problem,
    InputId? Inputs, ImmutableArray<EvidenceFile> Evidence, string Detail, ConflictEvidence? Conflict = null)
{
    public required BlockScope Scope { get; init; } = BlockScope.Unrecorded.Value;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Checkout), "checkout")]
[JsonDerivedType(typeof(Refs), "refs")]
[JsonDerivedType(typeof(Ownership), "ownership")]
[JsonDerivedType(typeof(Repository), "repository")]
[JsonDerivedType(typeof(Operation), "operation")]
internal abstract record BlockScope
{
    private BlockScope() { }

    // Naming no path, ref or lock means the checkout as a whole.
    internal sealed record Checkout(ImmutableArray<string> Paths, bool Branch = false, bool Head = false, bool IndexLock = false) : BlockScope
    {
        public static Checkout Whole { get; } = new([]);

        public bool Equals(Checkout? other) => other is not null && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal) &&
            Branch == other.Branch && Head == other.Head && IndexLock == other.IndexLock;

        public override int GetHashCode() => HashCode.Combine(Paths.Length, Branch, Head, IndexLock);
    }

    internal sealed record Refs(ImmutableArray<string> Names) : BlockScope
    {
        public bool Equals(Refs? other) => other is not null && Names.SequenceEqual(other.Names, StringComparer.Ordinal);

        public override int GetHashCode() => Names.Length;
    }

    internal sealed record Ownership : BlockScope;

    internal sealed record Repository : BlockScope;

    internal sealed record Operation : BlockScope;

    // Journaled before blocks named their scope; never written.
    internal sealed record Unrecorded : BlockScope
    {
        private Unrecorded() { }

        public static Unrecorded Value { get; } = new();
    }
}

internal sealed record PreservationObservation(int Ordinal, DateTimeOffset Started, DateTimeOffset Completed, CheckoutState State,
    CommitRecipe Recipe, CommitId Commit, ImmutableArray<ArtifactRecord> Outbox, ImmutableArray<StageEntry> Stages);
