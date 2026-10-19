using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class WorkflowRunCoordinator
{
    /// <summary>
    /// Approves the request the person saw. The approval records the node's human-origin result, which hands on the
    /// request's code, reports, and artifacts, and releases its dependents. No client runs. An approval needs no Resume,
    /// but its dependents start only in a resumed run.
    /// </summary>
    public Task<GateReply> Approve(RunAddress address, GateResponse response, OperationId command, CancellationToken wait = default) =>
        Answer(address, response, new GateAnswer.Approve(), command, wait);

    /// <summary>
    /// Sends the request back with the person's reason. Its dependents stay held, and nothing is chosen to run again. A new
    /// request follows once the request's inputs are superseded, or once the person asks again.
    /// </summary>
    public Task<GateReply> SendBack(RunAddress address, GateResponse response, string reason, OperationId command, CancellationToken wait = default) =>
        Answer(address, response, new GateAnswer.SendBack(reason), command, wait);

    /// <summary>
    /// Asks again after the person sent <paramref name="request"/> back, as they confirmed under <paramref name="command"/>.
    /// The request stays sent back in the run's history, and the node takes a new request on the same inputs (#126). A
    /// repeat returns the recorded entry; a request that is not the node's newest sent-back one on current inputs is
    /// <see cref="GateReply.Stale"/>.
    /// </summary>
    public async Task<GateReply> AskAgain(RunAddress address, GateId request, OperationId command, CancellationToken wait = default)
    {
        GateReply? reply = null;
        var outcome = await Command(address, _ =>
        {
            reply = _store.AskGateAgain(_permit!, command, request) switch
            {
                RunDecision.Rejected { Reason.Problem: RunProblem.StaleInput } => new GateReply.Stale(Read() is { } record &&
                    record.Gates.TryGetValue(request, out var asked) && RunReducer.LiveGate(record, asked.Request.Task) is { Decision: null } open
                        ? open.Request : null),
                RunDecision.Rejected rejected => new GateReply.Refused(rejected.Reason),
                RunDecision.Existing existing => new GateReply.Recorded(Asked(existing.Record, request), Repeat: true),
                RunDecision.Created created => new GateReply.Recorded(Asked(created.Record, request), Repeat: false),
                RunDecision.Recorded recorded => new GateReply.Recorded(Asked(recorded.Record, request), Repeat: false),
                _ => throw new InvalidOperationException(),
            };
            return Accepted;
        }, wait).ConfigureAwait(false);
        return reply ?? outcome switch
        {
            RunCommand.Unavailable unavailable => new GateReply.Unavailable(unavailable.Message),
            RunCommand.Refused refused => new GateReply.Refused(refused.Reason),
            _ => throw new InvalidOperationException(),
        };
    }

    private static RunEntry Asked(RunRecord record, GateId request) =>
        record.Receipts.Values.First(entry => entry.Event is RunEvent.GateAskedAgain asked && asked.Request == request);

    /// <summary>
    /// The note a Retry of <paramref name="task"/> starts with: the reasons of the sent-back Approval nodes it feeds through its
    /// dependency connections, each still on current inputs and not asked again, in the workflow's task order. Null
    /// when it feeds none (#126).
    /// </summary>
    internal static string? SentBackNote(Workflow snapshot, RunView view, TaskId task)
    {
        var fed = new HashSet<TaskId>();
        var next = new Queue<TaskId>([task]);
        while (next.TryDequeue(out var from))
            foreach (var (edge, kind) in snapshot.Connections)
                if (edge.From == from && kind == ConnectionKind.Dependency && fed.Add(edge.To)) next.Enqueue(edge.To);
        var reasons = snapshot.Tasks.Keys.Where(fed.Contains).Select(view.Tasks.GetValueOrDefault)
            .OfType<TaskView>().Where(each => each is { State: TaskState.SentBack, Gate.Reason: { } })
            .Select(each => each.Gate!.Reason!).Distinct().ToArray();
        return reasons.Length == 0 ? null : string.Join("\n\n", reasons);
    }

    /// <summary>
    /// What approving <paramref name="request"/> hands on, input by input: each input's report under its task's title, as the
    /// approval records it, and for a review, its verdict and every finding it raised, read from the review's attempt in
    /// this run or, for a result carried from an earlier run, in that run. Null when the journal cannot be read or holds no
    /// such request. It reads the journals and logs off the loop.
    /// </summary>
    public ImmutableArray<GateInput>? GateInputs(GateId request) =>
        Record() is { } record && record.Gates.TryGetValue(request, out var state) && record.Inputs.TryGetValue(state.Request.Inputs, out var inputs)
            ? GateForwarding.Inputs(record, inputs, result => ReviewLog(record, Address.Run, result)) : null;

    /// <summary>
    /// The log of <paramref name="review"/>'s review as this run shows it, which its node's inspector reads the findings from
    /// (#118): its newest attempt in this run, or, for a result the run carried from an earlier run, the attempt there that
    /// produced it. Null when the journal or the log cannot be read, or the run has neither. It reads the journals and the
    /// log off the loop.
    /// </summary>
    public AttemptRecord? ReviewLog(TaskId review) =>
        Record() is not { } record ? null
        : RunProjection.LatestAttempts(record).TryGetValue(review, out var newest) ? Log(record.Attempts[newest])
        : record.CurrentResults.GetValueOrDefault(review) is { Origin: ResultOrigin.Carried } carried ? ReviewLog(record, Address.Run, carried)
        : null;

    /// <summary>The log of the attempt that produced <paramref name="result"/> of run <paramref name="run"/>, or null.</summary>
    private AttemptRecord? ReviewLog(RunRecord record, RunId run, ResultRecord result) => result.Origin switch
    {
        ResultOrigin.Executed executed => Log(run, record.Attempts[executed.Attempt]),
        ResultOrigin.Carried carried when _store.Read(Address.Workflow, carried.Run) is RunRead.Loaded earlier =>
            ReviewLog(earlier.Record, carried.Run, earlier.Record.Results.Single(each => each.Id == carried.Result)),
        _ => null,
    };

    private async Task<GateReply> Answer(RunAddress address, GateResponse response, GateAnswer answer, OperationId command, CancellationToken wait)
    {
        GateReply? reply = null;
        var outcome = await Command(address, _ =>
        {
            reply = _store.AnswerGate(_permit!, command, response.Request, response.Inputs, answer) switch
            {
                RunDecision.Rejected { Reason: { Problem: RunProblem.StaleInput, Task: { } task } } =>
                    new GateReply.Stale(Read() is { } record && RunReducer.LiveGate(record, task) is { Decision: null } open ? open.Request : null),
                RunDecision.Rejected rejected => new GateReply.Refused(rejected.Reason),
                RunDecision.Existing existing => Receipt(existing.Record, response.Request, repeat: true),
                RunDecision.Created created => Receipt(created.Record, response.Request, repeat: false),
                RunDecision.Recorded recorded => Receipt(recorded.Record, response.Request, repeat: false),
                _ => throw new InvalidOperationException(),
            };
            return Accepted;
        }, wait).ConfigureAwait(false);
        return reply ?? outcome switch
        {
            RunCommand.Unavailable unavailable => new GateReply.Unavailable(unavailable.Message),
            RunCommand.Refused refused => new GateReply.Refused(refused.Reason),
            _ => throw new InvalidOperationException(),
        };
    }

    private static GateReply Receipt(RunRecord record, GateId request, bool repeat) =>
        new GateReply.Recorded(record.Receipts[record.Gates[request].Decision!.Operation], repeat);

    /// <summary>
    /// Records a request for every ready Approval node and every ready Merge node's own step, off the loop. A request takes
    /// no client slot, so it never waits for one, and the node shows as settling until its request is recorded. A Merge
    /// node's request hands on at once: its predecessors' code merged, with a conflict going to a Merge node's agent first.
    /// </summary>
    private void RequestGates(RunRecord record, RunView view)
    {
        foreach (var ready in view.Tasks.Values)
        {
            var task = ready.Task;
            var work = record.Revision.Snapshot.Tasks[task].Blueprint.Work;
            if (ready.State != TaskState.Ready || work is not (WorkSpec.Person or WorkSpec.Merge) || work is WorkSpec.Merge && ready.Attempt is not null)
                continue;
            _live[task] = new(LiveStage.Settling);
            // A blocked request shows through its recorded block. A refused one holds the node as a refused start does, so
            // not once the run is stopping. One whose inputs moved holds nothing, so the next decision reads them again.
            Background(async () =>
            {
                var prepared = await Scheduled().RequestGate(_permit!, RunOperations.Gate(Address.Run, task), task).ConfigureAwait(false);
                return prepared is GatePreparation.Requested requested && work is WorkSpec.Merge ? HandOn(requested.Request) : prepared;
            }, prepared =>
            {
                _live.Remove(task);
                if (prepared is GatePreparation.Rejected rejected)
                    HoldStart(task, new TaskHold.Refused(rejected.Reason, null, Transient(rejected.Reason.Problem)));
            }, error => Faulted(task, error));
        }
    }

    /// <summary>Hands on each Merge node's request that a crash or a refusal left open, off the loop.</summary>
    private void HandOnMerges(RunRecord record, RunView view)
    {
        foreach (var (task, state) in view.Tasks)
        {
            if (_live.ContainsKey(task) || _holds.ContainsKey(task) || record.Revision.Snapshot.Tasks[task].Blueprint.Work is not WorkSpec.Merge ||
                RunReducer.LiveGate(record, task) is not { Decision: null } open || state.State != TaskState.Settling)
                continue;
            _live[task] = new(LiveStage.Settling);
            Offload(() => HandOn(open.Request), handed =>
            {
                _live.Remove(task);
                if (handed is GatePreparation.Rejected rejected)
                    HoldStart(task, new TaskHold.Refused(rejected.Reason, null, Transient(rejected.Reason.Problem)));
            }, error => Faulted(task, error));
        }
    }

    /// <summary>Records a Merge node's result for its request: the merged code, reports, and artifacts the request composed.</summary>
    private GatePreparation HandOn(GateRequest request) =>
        _store.MergeGate(_permit!, OperationIds.Derive(RunOperations.Gate(Address.Run, request.Task), $"merged/{request.Id.Value:D}"), request.Id) switch
        {
            RunDecision.Rejected { Reason.Problem: RunProblem.StaleInput } => new GatePreparation.Moved(new(RunProblem.StaleInput)),
            RunDecision.Rejected rejected => new GatePreparation.Rejected(rejected.Reason),
            _ => new GatePreparation.Requested(request),
        };
}
