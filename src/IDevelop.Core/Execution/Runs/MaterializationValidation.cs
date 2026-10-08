using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static partial class RunValidation
{
    private static RunProblem? SchemaProblem(RunEntry entry)
    {
        if (entry.Schema is not (1 or 2 or 3))
        {
            return RunProblem.UnsupportedSchema;
        }
        if (entry.Schema < 3 && entry.Event is RunEvent.OwnershipFenced or RunEvent.RootExitObserved or
            RunEvent.TurnCaptured or RunEvent.CaptureDisposed or RunEvent.Preserved or RunEvent.PreservationDiverged or
            RunEvent.Restored or RunEvent.GitIntended { Mutation: GitMutation.RestoreRef or GitMutation.RestoreFiles or GitMutation.RemoveIndexLock } or
            RunEvent.Planned { Plan: MaterializationPlan.Preservation or MaterializationPlan.Restoration } or RunEvent.TurnClosed { Capture: not null })
        {
            return RunProblem.UnsupportedSchema;
        }
        if (entry.Schema == 1 && entry.Event is RunEvent.LayoutAllocated or RunEvent.Planned or RunEvent.GitIntended or RunEvent.GitObserved or
            RunEvent.Prepared or RunEvent.Blocked or RunEvent.SalvageRetained or RunEvent.BlockResolved)
        {
            return RunProblem.UnsupportedEvent;
        }
        var input = entry.Event switch
        {
            RunEvent.Reserved reserved => reserved.Inputs,
            RunEvent.TurnClaimed claim => claim.Inputs,
            RunEvent.ResultAccepted accepted => accepted.Inputs,
            _ => null,
        };
        if (input is not null && ((entry.Schema == 1) != (input.Code is CodeSelection.Legacy) ||
            entry.Schema == 1 && (!input.Files.IsEmpty || input.Review is not null)))
        {
            return RunProblem.InvalidData;
        }
        if (entry.Schema == 1 && entry.Event is RunEvent.ResultAccepted { Result: var result } &&
            (result.Code is not null || !result.Artifacts.IsEmpty))
        {
            return RunProblem.InvalidData;
        }
        return null;
    }

    private static RunProblem? Materialization(RunEvent e) => (e switch
    {
        RunEvent.LayoutAllocated { Key: LayoutKey.Run key } => !string.IsNullOrEmpty(key.Key) && !string.IsNullOrEmpty(key.Repository),
        RunEvent.LayoutAllocated { Key: LayoutKey.Task key } => key.TaskId.Value != Guid.Empty && !string.IsNullOrEmpty(key.Key),
        RunEvent.Planned { Plan: var plan } => Plan(plan),
        RunEvent.GitIntended intent => intent.Plan.Value != Guid.Empty && Mutation(intent.Mutation),
        RunEvent.GitObserved observed => observed.Mutation.Value != Guid.Empty,
        RunEvent.Prepared { Execution: var execution, SharedRefs: var refs } => Evidence(refs) &&
            StoredEvidence(refs.RelativePath) && Key(execution.Launch) && execution.Inputs.Value != Guid.Empty &&
            Owner(execution.Location.Owner) && Revision.IsCommit(execution.Location.AttemptBase.Hex) &&
            execution.Prompt is not null && Revision.IsHash(execution.PromptHash.Sha256) && execution.OutboxPath is not null,
        RunEvent.Blocked { Block: var block } => block.Operation.Value != Guid.Empty && block.Task.Value != Guid.Empty &&
            block.Attempt?.Value != Guid.Empty && block.Inputs?.Value != Guid.Empty && Enum.IsDefined(block.Problem) &&
            !block.Evidence.IsDefault && block.Evidence.All(Evidence) && block.Detail is not null &&
            (block.Conflict is null || Conflict(block.Conflict)) && (block.Scope is null || Scope(block.Scope)),
        RunEvent.SalvageRetained retained => retained.Plan.Value != Guid.Empty && Reference(retained.Ref) && Revision.IsCommit(retained.Commit.Hex),
        RunEvent.Restored restored => restored.Plan.Value != Guid.Empty,
        RunEvent.Preserved retained => retained.Plan.Value != Guid.Empty && Reference(retained.Ref) && Revision.IsCommit(retained.Commit.Hex),
        RunEvent.PreservationDiverged diverged => diverged.Operation.Value != Guid.Empty &&
            Observation(diverged.First, 1) && Observation(diverged.Second, 2) && Scope(diverged.Scope),
        RunEvent.BlockResolved resolved => resolved.Block.Value != Guid.Empty && !string.IsNullOrWhiteSpace(resolved.Reason),
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
        MaterializationPlan.Publication p => p.Attempt.Value != Guid.Empty && p.Result.Value != Guid.Empty && p.Supersedes?.Value != Guid.Empty &&
            Revision.IsCommit(p.VerifiedTip.Hex) && Hash(p.IndexBefore) && Recipe(p.Recipe) && Revision.IsCommit(p.Commit.Hex) &&
            p.Report is not null && !p.Artifacts.IsDefault && p.Artifacts.All(Artifact),
        MaterializationPlan.Salvage p => p.Task.Value != Guid.Empty && p.Attempt.Value != Guid.Empty && Revision.IsCommit(p.ObservedTip.Hex) && (p.BranchTip is null || Revision.IsCommit(p.BranchTip.Value.Hex)) &&
            Hash(p.IndexBefore) && Recipe(p.Recipe) && Revision.IsCommit(p.Commit.Hex) && !p.Untracked.IsDefault && p.Untracked.All(Evidence) && Reference(p.Ref) && (p.Preserved is null || State(p.Preserved)),
        MaterializationPlan.Preservation p => p.Task.Value != Guid.Empty && p.Attempt.Value != Guid.Empty && State(p.Preserved) &&
            Recipe(p.Recipe) && p.Recipe.Tree == p.Preserved.Files && Revision.IsCommit(p.Commit.Hex) &&
            !p.Outbox.IsDefault && p.Outbox.All(PreservedArtifact) && Reference(p.Ref),
        MaterializationPlan.Restoration p => p.Task.Value != Guid.Empty && p.Attempt.Value != Guid.Empty && p.Preservation.Value != Guid.Empty &&
            State(p.From) && State(p.To) && !p.Paths.IsDefault && p.Paths.All(RestorePath) &&
            !p.Repairs.IsDefault && p.Repairs.All(id => id.Value != Guid.Empty) && !p.Rechecks.IsDefault && p.Rechecks.All(id => id.Value != Guid.Empty) &&
            p.Confirmation.Value != Guid.Empty && Revision.IsHash(p.Preview.Sha256) && p.Supersedes?.Value != Guid.Empty,
        MaterializationPlan.RetryReset p => p.Task.Value != Guid.Empty && p.Salvaged.Value != Guid.Empty && p.SalvagePlan.Value != Guid.Empty &&
            (p.From is null || Revision.IsCommit(p.From.Value.Hex)) && Revision.IsCommit(p.To.Hex) && !p.Remove.IsDefault && p.Remove.All(Evidence),
        _ => false,
    };

    private static bool Mutation(GitMutation mutation) => mutation switch
    {
        GitMutation.CreateWorktree m => Owner(m.Owner) && Revision.IsCommit(m.Start.Hex),
        GitMutation.MoveRef m => Reference(m.Change.Ref) && (m.Change.Expected is null || Revision.IsCommit(m.Change.Expected.Value.Hex)) &&
            Revision.IsCommit(m.Change.Target.Hex),
        GitMutation.RestoreRef m => Reference(m.Change.Ref) && (m.Change.Expected is null || Revision.IsCommit(m.Change.Expected.Value.Hex)) && Revision.IsCommit(m.Change.Target.Hex),
        GitMutation.RestoreFiles m => m.Task.Value != Guid.Empty && !m.Paths.IsDefault && m.Paths.All(RestorePath),
        GitMutation.RemoveIndexLock m => m.Task.Value != Guid.Empty && Evidence(m.Lock.Bytes) && m.Lock.Identity is not null,
        GitMutation.AlignIndex m => m.Task.Value != Guid.Empty && Hash(m.Expected) && Revision.IsCommit(m.Target.Hex),
        GitMutation.ResetCheckout m => m.Task.Value != Guid.Empty && Revision.IsCommit(m.Target.Hex),
        GitMutation.AttachHead m => m.Task.Value != Guid.Empty && Reference(m.Branch),
        GitMutation.RemovePaths m => m.Task.Value != Guid.Empty && !m.Paths.IsDefault && m.Paths.All(Evidence),
        _ => false,
    };

    private static bool RestorePath(PathRestore path) => Path(path.Path) && Hash(path.From) && Hash(path.To);

    private static bool Code(CodeSelection code) => code switch
    {
        CodeSelection.Legacy legacy => Revision.IsCommit(legacy.Base.Hex),
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

    private static bool PreservedArtifact(ArtifactRecord file) => Path(file.Name) && Path(file.StoredPath) &&
        Revision.IsHash(file.Content.Sha256) && file.ByteLength >= 0;

    private static bool State(CheckoutState state) =>
        (state.Branch is null || Revision.IsCommit(state.Branch.Value.Hex)) &&
        (state.Head is null || Revision.IsCommit(state.Head.Value.Hex)) &&
        (state.SymbolicHead is null || Reference(state.SymbolicHead)) && Revision.IsCommit(state.Files.Hex) &&
        (state.Index is null || Evidence(state.Index)) && (state.IndexTree is null || Revision.IsCommit(state.IndexTree.Value.Hex)) &&
        !state.Untracked.IsDefault && state.Untracked.All(Evidence) && (state.IndexLock is null || Evidence(state.IndexLock.Bytes));

    private static bool Scope(BlockScope scope) => !scope.Paths.IsDefault && scope.Paths.All(Path) &&
        !scope.Refs.IsDefault && scope.Refs.All(reference => reference == "HEAD" || ScopedReference(reference));

    private static bool ScopedReference(string reference) => Reference(reference) && !reference.EndsWith('.') &&
        !reference.Contains("@{", StringComparison.Ordinal) && !reference.Any(c => c is '~' or '^' or '?' or '*' or '[' or '\u007f') &&
        reference.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));

    private static bool Observation(PreservationObservation observation, int ordinal) => observation.Ordinal == ordinal &&
        observation.Completed >= observation.Started && State(observation.State) && Recipe(observation.Recipe) &&
        observation.Recipe.Tree == observation.State.Files && Revision.IsCommit(observation.Commit.Hex) &&
        !observation.Outbox.IsDefault && observation.Outbox.All(PreservedArtifact) &&
        !observation.Stages.IsDefault && observation.Stages.All(stage => stage.Stage is >= 1 and <= 3 &&
            Path(stage.Path) && Revision.IsCommit(stage.Object) && stage.Mode.Length == 6 && stage.Mode.All(c => c is >= '0' and <= '7'));

    private static bool StoredEvidence(string path) => path.Split('/') is ["evidence", var operation, var name] &&
        Guid.TryParseExact(operation, "D", out var id) && id != Guid.Empty && Path(name);

    private static bool Evidence(EvidenceFile file) => Path(file.RelativePath) && Revision.IsHash(file.Content.Sha256) && file.ByteLength >= 0;

    private static bool Owner(WorktreeOwner owner) => owner.Task.Value != Guid.Empty && Path(owner.RelativePath) && Reference(owner.Branch);

    private static bool Recipe(CommitRecipe recipe) => Revision.IsCommit(recipe.Tree.Hex) && !recipe.Parents.IsDefault &&
        recipe.Parents.All(parent => Revision.IsCommit(parent.Hex)) && recipe.Message is not null &&
        !string.IsNullOrWhiteSpace(recipe.Author) && !string.IsNullOrWhiteSpace(recipe.Committer);

    private static bool Hash(Digest? hash) => hash is null || Revision.IsHash(hash.Value.Sha256);

    private static bool Path(string path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\\') && !path.Contains(':') &&
        !path.StartsWith('/') && path.Split('/').All(part => part is not ("" or "." or "..")) && !path.Any(char.IsControl);

    private static bool Reference(string reference) => Path(reference) && reference.StartsWith("refs/", StringComparison.Ordinal) &&
        !reference.Contains("..", StringComparison.Ordinal) && !reference.Any(char.IsWhiteSpace);

    private static bool Conflict(ConflictEvidence conflict) => Revision.IsCommit(conflict.AttributeSource.Hex) && !conflict.Sources.IsDefault && conflict.Sources.All(Source) && conflict.Step > 0 &&
        !conflict.Paths.IsDefault && conflict.Paths.All(Path) && !conflict.Stages.IsDefault && conflict.Stages.All(stage =>
            Path(stage.Path) && stage.Stage is >= 1 and <= 3 && Revision.IsCommit(stage.Object) && !string.IsNullOrWhiteSpace(stage.Mode)) &&
        !conflict.Messages.IsDefault && conflict.Messages.All(message => !message.Paths.IsDefault && message.Paths.All(Path) &&
            !string.IsNullOrWhiteSpace(message.Type) && message.Text is not null) && Evidence(conflict.Stdout) && Evidence(conflict.Stderr) &&
        !string.IsNullOrWhiteSpace(conflict.GitVersion) && !conflict.MergeConfig.IsDefault && conflict.MergeConfig.All(config =>
            !string.IsNullOrWhiteSpace(config.Key) && config.Value is not null);
}
