namespace IDevelop.Execution;

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
}
