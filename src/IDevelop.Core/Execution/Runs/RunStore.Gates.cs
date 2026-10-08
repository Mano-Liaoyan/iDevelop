namespace IDevelop.Execution;

internal sealed partial class RunStore
{
    /// <summary>
    /// Records an Approval node's request for <paramref name="inputs"/>, numbered after the node's earlier requests. It
    /// creates no attempt. The reducer checks that the node has no live request or current result, and that the inputs are
    /// the current composition the materializer built, including a recorded join.
    /// </summary>
    public RunDecision RequestGate(CoordinatorPermit permit, OperationId operation, GateId gate, InputRecord inputs, ResultId result) =>
        Transact(permit, operation, Fingerprint("requestGate", new { gate, inputs, result }), (record, _) =>
        {
            if (record is null) return Missing();
            var sequence = record.Gates.Values.Count(state => state.Request.Task == inputs.Task) + 1;
            return new Mutation.Append(new RunEvent.GateRequested(new(gate, inputs.Task, inputs.Revision, inputs.Id, sequence, result), inputs));
        });

    /// <summary>
    /// Answers the request the person saw, named by its id and input id. Approval records a human-origin result with what the
    /// inputs forward, in one entry. Send back records the reason. A repeat of the recorded answer returns the original
    /// entry. Any other answer to a request that was answered, replaced, or whose inputs were superseded is refused as
    /// <see cref="RunProblem.StaleInput"/>.
    /// </summary>
    public RunDecision AnswerGate(CoordinatorPermit permit, OperationId operation, GateId gate, InputId inputs, GateAnswer answer) =>
        Transact(permit, operation, Fingerprint("answerGate", new { gate, inputs, sendBack = (answer as GateAnswer.SendBack)?.Reason }), (record, _) =>
        {
            if (record is null) return Missing();
            if (!record.Gates.TryGetValue(gate, out var state)) return Refuse(RunProblem.UnknownInput);
            var request = state.Request;
            Mutation Stale() => new Mutation.Rejected(new(RunProblem.StaleInput, Task: request.Task));
            if (state.Decision is { } decided)
            {
                var repeat = request.Inputs == inputs && (decided, answer) switch
                {
                    (GateDecision.Approved, GateAnswer.Approve) => true,
                    (GateDecision.SentBack sent, GateAnswer.SendBack again) => sent.Reason == again.Reason,
                    _ => false,
                };
                return repeat ? new Mutation.Existing(record.Receipts[decided.Operation].Event) : Stale();
            }
            if (request.Inputs != inputs || RunReducer.Superseded(record, state)) return Stale();
            // The reducer refuses an answer in a stopping run and a send back without a reason.
            var input = record.Inputs[request.Inputs];
            return new Mutation.Append(answer switch
            {
                GateAnswer.SendBack sendBack => new RunEvent.GateSentBack(gate, inputs, sendBack.Reason),
                _ => new RunEvent.ResultAccepted(new(request.Result, request.Task, request.Revision, request.Inputs, new ResultOrigin.Human(gate),
                    GateForwarding.Report(record, input), record.CurrentResults.GetValueOrDefault(request.Task)?.Id)
                {
                    Code = GateForwarding.Code(input),
                    Artifacts = GateForwarding.Artifacts(record, input, request.Result).Artifacts,
                }, input),
            });
        });
}
