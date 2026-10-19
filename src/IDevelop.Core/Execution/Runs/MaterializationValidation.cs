using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static partial class RunValidation
{
    private static RunProblem? Materialization(RunEvent e) => (e switch
    {
        RunEvent.LayoutAllocated { Key: LayoutKey.Run key } => !string.IsNullOrEmpty(key.Key) && !string.IsNullOrEmpty(key.Repository),
        RunEvent.LayoutAllocated { Key: LayoutKey.Task key } => key.TaskId.Value != Guid.Empty && !string.IsNullOrEmpty(key.Key),
        RunEvent.Planned { Plan: var plan } => Plan(plan),
        RunEvent.GitIntended intent => intent.Plan.Value != Guid.Empty && Mutation(intent.Mutation),
        RunEvent.GitObserved observed => observed.Mutation.Value != Guid.Empty,
        RunEvent.Prepared { Execution: var execution } => Key(execution.Launch) && execution.Inputs.Value != Guid.Empty &&
            Owner(execution.Location.Owner) && Revision.IsCommit(execution.Location.AttemptBase.Hex) &&
            execution.Prompt is not null && Revision.IsHash(execution.PromptHash.Sha256) && execution.OutboxPath is not null,
        RunEvent.Blocked { Block: var block } => block.Operation.Value != Guid.Empty && block.Task.Value != Guid.Empty &&
            block.Attempt?.Value != Guid.Empty && block.Inputs?.Value != Guid.Empty && Enum.IsDefined(block.Problem) &&
            !block.Evidence.IsDefault && block.Evidence.All(Evidence) && block.Detail is not null &&
            (block.Conflict is null || Conflict(block.Conflict)) && Scope(block.Scope),
        RunEvent.BlockResolved resolved => resolved.Block.Value != Guid.Empty && !string.IsNullOrWhiteSpace(resolved.Reason),
        RunEvent.ConflictResolved resolved => MergeConflict(resolved.Conflict) && Revision.IsCommit(resolved.Merge.Hex),
        _ => false,
    }) ? null : RunProblem.InvalidData;

    private static bool Plan(MaterializationPlan plan) => plan switch
    {
        MaterializationPlan.Preparation p => Attempt(new(p.Attempt, p.Task, p.Revision, p.Inputs, p.Cause)) &&
            Bindings(p.Bindings) &&
            !p.Sources.IsDefault && p.Sources.All(Source) && Review(p.Review),
        MaterializationPlan.Refresh p => Key(p.Launch) && p.Launch.Turn > 1 && p.Inputs.Value != Guid.Empty &&
            Bindings(p.Bindings) && !p.Sources.IsDefault && p.Sources.All(Source) && Review(p.Review) && (p.Composed is null || Code(new CodeSelection.Joined(p.Composed))),
        MaterializationPlan.Join p => p.Task.Value != Guid.Empty && p.Inputs.Value != Guid.Empty &&
            !p.Sources.IsDefault && p.Sources.All(Source) && Recipe(p.Recipe) && Revision.IsCommit(p.Commit.Hex) &&
            (p.Previous is null || Revision.IsCommit(p.Previous.Value.Hex)) && Reference(p.Ref),
        _ => false,
    };

    private static bool Mutation(GitMutation mutation) => mutation switch
    {
        GitMutation.CreateWorktree m => Owner(m.Owner) && Revision.IsCommit(m.Start.Hex),
        GitMutation.MoveRef m => Reference(m.Change.Ref) && (m.Change.Expected is null || Revision.IsCommit(m.Change.Expected.Value.Hex)) &&
            Revision.IsCommit(m.Change.Target.Hex),
        GitMutation.ResetCheckout m => m.Task.Value != Guid.Empty && Revision.IsCommit(m.Target.Hex),
        _ => false,
    };

    private static bool Code(CodeSelection code) => code switch
    {
        CodeSelection.Root root => Revision.IsCommit(root.Commit.Hex),
        CodeSelection.Single single => Source(single.Source),
        CodeSelection.Joined joined => joined.Join.Operation.Value != Guid.Empty && !joined.Join.Sources.IsDefault &&
            joined.Join.Sources.All(Source) && joined.Join.Sources.Select(source => source.Commit).Distinct().Count() >= 2 &&
            Revision.IsCommit(joined.Join.Commit.Hex) && Revision.IsCommit(joined.Join.Tree.Hex) && Reference(joined.Join.Ref),
        _ => false,
    };

    private static bool Source(CodeSource source) => source.Task.Value != Guid.Empty && source.Result.Value != Guid.Empty &&
        !source.Owners.IsDefaultOrEmpty && source.Owners.All(owner => owner.Value != Guid.Empty) &&
        source.Owners.SequenceEqual(source.Owners.Distinct().OrderBy(owner => owner.ToString(), StringComparer.Ordinal)) && Revision.IsCommit(source.AttemptBase.Hex) && Revision.IsCommit(source.Commit.Hex);

    private static bool Review(ReviewInput? review) => review is null || review.Subject.Value != Guid.Empty && review.SubjectResult.Value != Guid.Empty;

    private static bool Owned(OwnedCode code) => code.Owner.Value != Guid.Empty && code.Attempt.Value != Guid.Empty &&
        Revision.IsCommit(code.AttemptBase.Hex) && Revision.IsCommit(code.Commit.Hex) && Revision.IsCommit(code.Tree.Hex) && Reference(code.ResultRef);

    private static bool Artifact(ArtifactRecord file) => Path(file.Name) && !file.Name.Contains('/') && Path(file.StoredPath) &&
        Revision.IsHash(file.Content.Sha256) && file.ByteLength >= 0;

    private static bool Scope(BlockScope scope) => scope switch
    {
        BlockScope.Checkout checkout => !checkout.Paths.IsDefault && checkout.Paths.All(Path),
        BlockScope.Refs refs => !refs.Names.IsDefaultOrEmpty && refs.Names.All(ScopedReference),
        BlockScope.Ownership or BlockScope.Repository or BlockScope.Operation => true,
        _ => false,
    };

    private static bool ScopedReference(string reference) => Reference(reference) && !reference.EndsWith('.') &&
        !reference.Contains("@{", StringComparison.Ordinal) && !reference.Any(c => c is '~' or '^' or '?' or '*' or '[' or '\u007f') &&
        reference.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));

    private static bool Evidence(EvidenceFile file) => Path(file.RelativePath) && Revision.IsHash(file.Content.Sha256) && file.ByteLength >= 0;

    private static bool Owner(WorktreeOwner owner) => owner.Task.Value != Guid.Empty && Path(owner.RelativePath) &&
        (owner.Branch is null || Reference(owner.Branch));

    private static bool Recipe(CommitRecipe recipe) => Revision.IsCommit(recipe.Tree.Hex) && !recipe.Parents.IsDefault &&
        recipe.Parents.All(parent => Revision.IsCommit(parent.Hex)) && recipe.Message is not null &&
        !string.IsNullOrWhiteSpace(recipe.Author) && !string.IsNullOrWhiteSpace(recipe.Committer);

    private static bool Path(string path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\\') && !path.Contains(':') &&
        !path.StartsWith('/') && path.Split('/').All(part => part is not ("" or "." or "..")) && !path.Any(char.IsControl);

    private static bool Reference(string reference) => Path(reference) && reference.StartsWith("refs/", StringComparison.Ordinal) &&
        !reference.Contains("..", StringComparison.Ordinal) && !reference.Any(char.IsWhiteSpace);

    private static bool MergeConflict(MergeConflict conflict) => Revision.IsCommit(conflict.Ours.Hex) && Revision.IsCommit(conflict.Theirs.Hex) &&
        Revision.IsCommit(conflict.Conflicted.Hex) && !conflict.Paths.IsDefault && conflict.Paths.All(Path) && conflict.Description is not null &&
        conflict.Target switch
        {
            MergeTarget.Join join => join.Task.Value != Guid.Empty,
            MergeTarget.Branch branch => Reference(branch.Name),
            _ => false,
        };

    private static bool Conflict(ConflictEvidence conflict) => (conflict.Merge is null || MergeConflict(conflict.Merge)) &&
        Revision.IsCommit(conflict.AttributeSource.Hex) && !conflict.Sources.IsDefault && conflict.Sources.All(Source) && conflict.Step > 0 &&
        !conflict.Paths.IsDefault && conflict.Paths.All(Path) && !conflict.Stages.IsDefault && conflict.Stages.All(stage =>
            Path(stage.Path) && stage.Stage is >= 1 and <= 3 && Revision.IsCommit(stage.Object) && !string.IsNullOrWhiteSpace(stage.Mode)) &&
        !conflict.Messages.IsDefault && conflict.Messages.All(message => !message.Paths.IsDefault && message.Paths.All(Path) &&
            !string.IsNullOrWhiteSpace(message.Type) && message.Text is not null) && Evidence(conflict.Stdout) && Evidence(conflict.Stderr) &&
        !string.IsNullOrWhiteSpace(conflict.GitVersion) && !conflict.MergeConfig.IsDefault && conflict.MergeConfig.All(config =>
            !string.IsNullOrWhiteSpace(config.Key) && config.Value is not null);
}
