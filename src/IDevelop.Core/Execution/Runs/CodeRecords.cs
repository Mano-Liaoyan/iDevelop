using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed record CodeSource(TaskId Task, ResultId Result, ImmutableArray<TaskId> Owners, CommitId AttemptBase, CommitId Commit);

internal sealed record JoinRecord(OperationId Operation, ImmutableArray<CodeSource> Sources, CommitId Commit, TreeId Tree, string Ref);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Root), "root")]
[JsonDerivedType(typeof(Single), "single")]
[JsonDerivedType(typeof(Joined), "joined")]
internal abstract record CodeSelection
{
    private CodeSelection() { }

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
[JsonDerivedType(typeof(Carried), "carried")]
internal abstract record CodeOutput
{
    private CodeOutput() { }

    internal sealed record Produced(OwnedCode Code) : CodeOutput;

    internal sealed record Forwarded(InputId Inputs) : CodeOutput;

    /// <summary>
    /// The code of a result carried from an earlier run whose work the branch the run started from does not hold: the
    /// earlier result's <paramref name="Commit"/>, which its <paramref name="Owners"/> changed from <paramref name="AttemptBase"/>.
    /// </summary>
    internal sealed record Carried(ImmutableArray<TaskId> Owners, CommitId AttemptBase, CommitId Commit) : CodeOutput
    {
        public bool Equals(Carried? other) => other is not null && Owners.SequenceEqual(other.Owners) && AttemptBase == other.AttemptBase &&
            Commit == other.Commit;

        public override int GetHashCode() => HashCode.Combine(AttemptBase, Commit);
    }
}

/// <summary>
/// A task's worktree in a run. An editing task's worktree is on its own <paramref name="Branch"/>, and a read-only task's
/// worktree is detached at the merge of its predecessors, with no branch (ADR 0032).
/// </summary>
internal sealed record WorktreeOwner(TaskId Task, string RelativePath, string? Branch);

internal sealed record ExecutionLocation(WorktreeOwner Owner, CommitId AttemptBase);

internal sealed record PreparedExecution(LaunchKey Launch, InputId Inputs, ExecutionLocation Location, string Prompt, Digest PromptHash,
    string OutboxPath);

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

internal static class GitFaults
{
    /// <summary>The materialization problem a Git failure of <paramref name="fault"/>'s kind is.</summary>
    public static MaterializationProblem Problem(this GitFault fault) => fault switch
    {
        GitFault.NotInWorkTree => MaterializationProblem.NotRepositoryRoot,
        GitFault.SymbolicRef => MaterializationProblem.UncertainOwnership,
        GitFault.HiddenChanges => MaterializationProblem.DirtyWorktree,
        _ => MaterializationProblem.GitFailed,
    };
}

/// <summary>A fan-in conflict as Git reported it, stored and replayed without interpreting the merge.</summary>
internal sealed record ConflictEvidence(ImmutableArray<CodeSource> Sources, int Step, ImmutableArray<string> Paths,
    ImmutableArray<StageEntry> Stages, ImmutableArray<MergeMessage> Messages, EvidenceFile Stdout, EvidenceFile Stderr, string GitVersion,
    ImmutableArray<ConfigEntry> MergeConfig, CommitId AttributeSource)
{
    /// <summary>The conflict a Merge node's agent resolves for this merge step.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MergeConflict? Merge { get; init; }
}

/// <summary>What a merge conflict is in: the merge of a task's predecessors, or the run's final merge into its branch.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Join), "join")]
[JsonDerivedType(typeof(Branch), "branch")]
internal abstract record MergeTarget
{
    private MergeTarget() { }

    /// <summary>The merge of <paramref name="Task"/>'s predecessors, which it starts from.</summary>
    internal sealed record Join([property: JsonPropertyName("task")] TaskId Task) : MergeTarget;

    /// <summary>The run's final merge into <paramref name="Name"/>, the branch it started from.</summary>
    internal sealed record Branch(string Name) : MergeTarget;
}

/// <summary>
/// A merge conflict that a Merge node's agent resolves: merging <paramref name="Theirs"/> into <paramref name="Ours"/>
/// conflicts in <paramref name="Paths"/>. <paramref name="Conflicted"/> is the commit iDevelop made of the tree Git's merge
/// left, its conflict markers included, on both parents, where the agent's worktree starts. Resolved, the merge is a
/// commit of the agent's files on the same two parents. <paramref name="Description"/> is what the agent reads about it.
/// </summary>
internal sealed record MergeConflict(MergeTarget Target, CommitId Ours, CommitId Theirs, CommitId Conflicted, ImmutableArray<string> Paths,
    string Description)
{
    /// <summary>
    /// The conflicting files that hold no conflict markers in <see cref="Conflicted"/>, such as a binary file, a file one side
    /// deleted, or a renamed one. Nothing checks them for markers, so the person and the agent are told to check them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<string> Unmarked { get; init; }

    public bool Equals(MergeConflict? other) => other is not null && Target == other.Target && Ours == other.Ours && Theirs == other.Theirs &&
        Conflicted == other.Conflicted && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal) && Description == other.Description &&
        Unmarked.IsDefaultOrEmpty == other.Unmarked.IsDefaultOrEmpty &&
        (Unmarked.IsDefaultOrEmpty || Unmarked.SequenceEqual(other.Unmarked, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Target, Ours, Theirs, Conflicted);

    /// <summary>Whether this conflict merges <paramref name="theirs"/> into <paramref name="ours"/>.</summary>
    public bool Merges(CommitId ours, CommitId theirs) => Ours == ours && Theirs == theirs;
}

internal sealed record MaterializationBlock(OperationId Operation, TaskId Task, AttemptId? Attempt, MaterializationProblem Problem,
    InputId? Inputs, ImmutableArray<EvidenceFile> Evidence, string Detail, ConflictEvidence? Conflict = null)
{
    public required BlockScope Scope { get; init; }
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

    // Naming no path or ref means the checkout as a whole.
    internal sealed record Checkout(ImmutableArray<string> Paths, bool Branch = false, bool Head = false) : BlockScope
    {
        public static Checkout Whole { get; } = new([]);

        public bool Equals(Checkout? other) => other is not null && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal) &&
            Branch == other.Branch && Head == other.Head;

        public override int GetHashCode() => HashCode.Combine(Paths.Length, Branch, Head);
    }

    internal sealed record Refs(ImmutableArray<string> Names) : BlockScope
    {
        public bool Equals(Refs? other) => other is not null && Names.SequenceEqual(other.Names, StringComparer.Ordinal);

        public override int GetHashCode() => Names.Length;
    }

    internal sealed record Ownership : BlockScope;

    internal sealed record Repository : BlockScope;

    internal sealed record Operation : BlockScope;
}
