using IDevelop.Workflows;

namespace IDevelop.Execution;

internal abstract record Preparation
{
    private Preparation() { }
    internal sealed record Ready(PreparedExecution Execution, string Checkout) : Preparation;
    internal sealed record Blocked(MaterializationBlock Block) : Preparation;
    internal sealed record Rejected(RunRejection Reason) : Preparation;
}

internal abstract record WriterState
{
    private WriterState() { }
    internal sealed record Quiescent(AttemptId Attempt, string Mechanism) : WriterState;
    internal sealed record Unproven(string Reason) : WriterState;
}

internal interface IExecutionBoundary
{
    WriterState Inspect(AttemptId attempt);
}

internal sealed class UnprovenBoundary : IExecutionBoundary
{
    public WriterState Inspect(AttemptId attempt) => new WriterState.Unproven("No launch authority owns this attempt's process tree yet.");
}

internal sealed class UnavailableJoins : IJoinComposer
{
    public ValueTask<JoinOutcome> Compose(JoinRequest request, CancellationToken cancellation) =>
        ValueTask.FromResult<JoinOutcome>(new JoinOutcome.Blocked(new(request.Operation, request.Task, null,
            MaterializationProblem.JoinRequired, request.Inputs, [], "Multiple code revisions require a join.")));
}
