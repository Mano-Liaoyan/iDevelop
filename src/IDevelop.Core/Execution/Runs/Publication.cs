namespace IDevelop.Execution;

internal abstract record Publication
{
    private Publication() { }
    internal sealed record Accepted(ResultRecord Result) : Publication;
    internal sealed record Blocked(MaterializationBlock Block) : Publication;
    internal sealed record Rejected(RunRejection Reason) : Publication;
}
