using IDevelop.Workflows;

namespace IDevelop.Execution;

internal abstract record Preparation
{
    private Preparation() { }
    internal sealed record Ready(PreparedExecution Execution, string Checkout) : Preparation;
    internal sealed record Blocked(MaterializationBlock Block) : Preparation;
    internal sealed record Rejected(RunRejection Reason) : Preparation;
}

internal sealed class UnavailableJoins : IJoinComposer
{
    public ValueTask<JoinOutcome> Compose(JoinRequest request, CancellationToken cancellation) =>
        ValueTask.FromResult<JoinOutcome>(new JoinOutcome.Blocked(new(request.Operation, request.Task, null,
            MaterializationProblem.JoinRequired, request.Inputs, [], "Multiple code revisions require a join.")));
}
