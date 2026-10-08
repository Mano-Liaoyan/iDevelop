namespace IDevelop.Execution;

internal sealed record RecoveryBaseline(AttemptId Previous, OperationId Confirmation, string Session, OperationId Preservation);

internal abstract record RecoveryBaselining
{
    private RecoveryBaselining() { }
    internal sealed record Recorded(RunEvent.RecoveryBaselined Receipt) : RecoveryBaselining;
    internal sealed record Blocked(MaterializationBlock Block) : RecoveryBaselining;
    internal sealed record Rejected(RunRejection Reason) : RecoveryBaselining;
}

internal abstract record Salvage
{
    private Salvage() { }
    internal sealed record Retained(RunEvent.SalvageRetained Receipt, CommitId Commit) : Salvage;
    internal sealed record Blocked(MaterializationBlock Block) : Salvage;
    internal sealed record Rejected(RunRejection Reason) : Salvage;
}

internal abstract record RetryReset
{
    private RetryReset() { }
    internal sealed record Reset(CommitId Target) : RetryReset;
    internal sealed record Blocked(MaterializationBlock Block) : RetryReset;
    internal sealed record Rejected(RunRejection Reason) : RetryReset;
}

internal abstract record Preservation
{
    private Preservation() { }
    internal sealed record Preserved(RunEvent.Preserved Receipt, CommitId Commit) : Preservation;
    internal sealed record Blocked(MaterializationBlock Block) : Preservation;
    internal sealed record Rejected(RunRejection Reason) : Preservation;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : Preservation;
}

internal abstract record RestorePreviewRead
{
    private RestorePreviewRead() { }
    internal sealed record Previewed(RestorePreview Preview) : RestorePreviewRead;
    internal sealed record Refused(MaterializationProblem Problem, string Detail, BlockScope Scope) : RestorePreviewRead;
    internal sealed record Rejected(RunRejection Reason) : RestorePreviewRead;

    /// <summary>Another window controls the run.</summary>
    internal sealed record Unavailable(string Message) : RestorePreviewRead;
}

internal abstract record Restoration
{
    private Restoration() { }
    internal sealed record Restored(RunEvent.Restored Receipt) : Restoration;
    internal sealed record Blocked(MaterializationBlock Block) : Restoration;
    internal sealed record Refused(MaterializationProblem Problem, string Detail, BlockScope Scope) : Restoration;
    internal sealed record Rejected(RunRejection Reason) : Restoration;

    /// <summary>Another window controls the run. Nothing was recorded.</summary>
    internal sealed record Unavailable(string Message) : Restoration;
}
