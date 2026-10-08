using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// A person's commands to the run's tasks: messages, Mark done, and Cancel (E3c.1). Each one is decided on the loop after a
/// durable reread, like <see cref="Resume"/> and <see cref="Stop"/>, and names its run.
/// </summary>
internal sealed partial class WorkflowRunCoordinator
{
    internal const string StartingMessage = "The task's next turn is starting. Send your message again in a moment.";
    internal const string EndingMessage = "The turn is ending. Wait for it to finish.";
    internal const string StoppingMessage = "The workflow is stopping, so its tasks take no more messages.";
    internal const string EndedMessage = "The workflow run has ended, so its tasks take no more messages.";
    internal const string NotStartedMessage = "This task has not started in the run yet.";
    internal const string HeldMessage = "The run holds this task back. Resume the run to try its next step again.";
    internal const string ClosedMessage = "This task's attempt in the run has ended.";
    internal const string BusyMessage = "The task is busy. Try again in a moment.";
    internal const string NotWaitingMessage = "The task must finish its turn and wait for input.";
    internal const string ReplyPendingMessage = "A reply is waiting for its turn. Mark the task done after that turn.";
    internal const string NothingToCancelMessage = "There is no running or waiting attempt to cancel.";

    /// <summary>
    /// Sends the person's message to <paramref name="task"/>'s agent. A running turn queues it, and Stop and send ends that
    /// turn first. A resting attempt records it in its log at once, and the coordinator then starts the attempt's next turn
    /// with every queued message, when the run's client slot is free. The message is refused while a turn starts or settles,
    /// so the queued text never changes between a next turn's prompt and its claim.
    /// </summary>
    internal Task<SendResult> Send(RunAddress address, TaskId task, TurnKey expected, string text, bool stopTurn, CancellationToken wait = default) =>
        Converse(address, (record, complete) => SendCore(record, task, expected, text, stopTurn, complete), Refusal, wait);

    /// <summary>Ends <paramref name="task"/>'s attempt that waits for input as succeeded, and publishes or accepts its result.</summary>
    internal Task<ConversationCommandResult> MarkDone(RunAddress address, TaskId task, TurnKey expected, CancellationToken wait = default) =>
        Converse(address, (record, complete) => MarkDoneCore(record, task, expected, complete), CommandRefusal, wait);

    /// <summary>Cancels <paramref name="task"/>'s running turn, or closes its resting attempt as cancelled without sending its queued text.</summary>
    internal Task<ConversationCommandResult> Cancel(RunAddress address, TaskId task, TurnKey expected, CancellationToken wait = default) =>
        Converse(address, (record, complete) => CancelCore(record, task, expected, complete), CommandRefusal, wait);

    private static SendResult Refused(SendProblem problem) => new SendResult.Refused(problem);

    private static SendResult Refused(string reason) => Refused(new SendProblem.RunUnavailable(reason));

    private static ConversationCommandResult Applied(string detail) => new(CommandOutcome.Applied, detail);

    private static ConversationCommandResult Declined(string detail) => new(CommandOutcome.Refused, detail);

    private static SendResult Refusal(RunCommand command) => command switch
    {
        RunCommand.Unavailable unavailable => Refused(unavailable.Message),
        RunCommand.Refused refused => Refused(Describe(refused.Reason)),
        _ => throw new InvalidOperationException(),
    };

    private static ConversationCommandResult CommandRefusal(RunCommand command) => command switch
    {
        RunCommand.Unavailable unavailable => new(CommandOutcome.Unavailable, unavailable.Message),
        RunCommand.Refused refused => Declined(Describe(refused.Reason)),
        _ => throw new InvalidOperationException(),
    };

    private static string Describe(RunRejection reason) => reason.Problem switch
    {
        RunProblem.IdentityMismatch => "The command names another workflow run.",
        RunProblem.RunStopped => EndedMessage,
        RunProblem.StorageUnavailable => "iDevelop could not read the workflow run.",
        RunProblem.TaskBusy or RunProblem.JournalBusy => BusyMessage,
        var problem => $"The workflow run refused the command ({problem}).",
    };

    /// <summary>
    /// Runs <paramref name="body"/> on the loop with the journal reread, after the checks every command gets: a window that
    /// only reads the run is unavailable, and a command for another run is refused. The body completes the command, at once
    /// or from the outcome of its work.
    /// </summary>
    private Task<T> Converse<T>(RunAddress address, Action<RunRecord, Action<T>> body, Func<RunCommand, T> refusal, CancellationToken wait)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(T result) => done.TrySetResult(result);
        void Run()
        {
            if (_permit is null) Complete(refusal(new RunCommand.Unavailable(ElsewhereMessage)));
            else if (!Same(address)) Complete(refusal(new RunCommand.Refused(new(RunProblem.IdentityMismatch))));
            else if (Read() is not { } record) Complete(refusal(new RunCommand.Refused(new(RunProblem.StorageUnavailable))));
            else body(record, Complete);
        }
        if (!Post(() =>
            {
                try { Run(); }
                catch (Exception error) { done.TrySetException(error); }
            }))
            Complete(refusal(new RunCommand.Refused(new(RunProblem.RunStopped))));
        return done.Task.WaitAsync(wait);
    }

    /// <summary>Why the run takes no message for <paramref name="task"/> at all now, or null.</summary>
    private static SendProblem? Unconversable(RunRecord record, TaskId task) => record.Phase switch
    {
        RunPhase.Approved => record.Revision.Snapshot.Tasks.ContainsKey(task) ? null : new SendProblem.MissingTask(),
        RunPhase.StopRequested => new SendProblem.RunUnavailable(StoppingMessage),
        _ => new SendProblem.RunUnavailable(EndedMessage),
    };

    private static string Text(SendProblem problem) => problem switch
    {
        SendProblem.RunUnavailable unavailable => unavailable.Reason,
        SendProblem.MissingTask => "The task no longer exists.",
        _ => problem.ToString(),
    };

    /// <summary>A task's attempt that rests between turns, waiting for input or with queued text, and its log.</summary>
    private sealed record RestingAttempt(AttemptId Attempt, LaunchKey Last, string Folder, AttemptEvidence Evidence)
    {
        public AttemptRecord Log => Evidence.Record!;

        public TurnKey Turn => new(Attempt, Log.Turns.Count);

        public LaunchKey Next => new(Attempt, Last.Turn + 1);
    }

    private RestingAttempt? Resting(RunRecord record, TaskView? view)
    {
        if (view is not { State: TaskState.Waiting, Attempt: { } attempt } || record.Closures.ContainsKey(attempt) ||
            RunProjection.LastLaunch(record, attempt) is not { } last || !record.TurnClosures.ContainsKey(last))
            return null;
        var folder = _store.AttemptFolder(Address.Workflow, Address.Run, view.Task, attempt);
        var evidence = AttemptEvidence.Read(folder);
        return evidence is { Rejection: null, Record: { BetweenTurns: true, Status: AttemptStatus.WaitingForInput or AttemptStatus.Running } log } &&
            log.Turns.Count == last.Turn ? new(attempt, last, folder, evidence) : null;
    }

    /// <summary>Whether the person wrote to the attempt after its last turn closed, which only a reply to a waiting attempt does.</summary>
    private static bool Replied(RunRecord record, RestingAttempt resting) =>
        AttemptEvidence.Suffix(resting.Folder, record.TurnClosures[resting.Last], resting.Evidence.Checkpoint!) is { } suffix &&
        suffix.Any(e => e is AttemptEvent.MessageQueued);

    /// <summary>
    /// The prompt of a resting attempt's next turn, or null while it waits for the person. A turn that ended with queued
    /// text goes on with it. A turn that waits for input, a deferred question's included, goes on only once the person
    /// replied after it closed, and then its earlier queued text comes first.
    /// </summary>
    private static string? Continuation(RunRecord record, RestingAttempt resting)
    {
        var log = resting.Log;
        if (log.Queued.IsEmpty) return null;
        var goesOn = log.Status == AttemptStatus.Running || log.Status == AttemptStatus.WaitingForInput && Replied(record, resting);
        return goesOn ? string.Join("\n\n", log.Queued.Select(message => message.Text)) : null;
    }

    /// <summary>Starts the next turn of the first resting attempt, in task order, that has text for its agent.</summary>
    private bool Continue(RunRecord record, RunView view)
    {
        foreach (var state in view.Tasks.Values)
        {
            var task = state.Task;
            if (_live.ContainsKey(task) || Resting(record, state) is not { } resting ||
                Continuation(record, resting) is not { } prompt)
                continue;
            var next = resting.Next;
            _live[task] = new(LiveStage.Starting);
            _runs.Probe?.Invoke("coordinator.continue");
            Background(() => _runs.StartTurn(_permit!, new TurnIntent.Next(RunOperations.Turn(record, next), next, prompt)),
                start => Started(task, start), error => Faulted(task, error));
            return true;
        }
        return false;
    }

    private void SendCore(RunRecord record, TaskId task, TurnKey expected, string text, bool stopTurn, Action<SendResult> complete)
    {
        if (Unconversable(record, task) is { } unconversable)
        {
            complete(Refused(unconversable));
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            complete(Refused(new SendProblem.EmptyMessage()));
            return;
        }
        var definition = record.Revision.Snapshot.Tasks[task];
        if ((NodeWorks.For(definition.Blueprint.Work) as IConverses)?.Receive(text) is not MessageUse.Turn turn)
        {
            complete(Refused(new SendProblem.CannotStart(new StartProblem.NoConversation())));
            return;
        }
        switch (_live.GetValueOrDefault(task))
        {
            case { Stage: LiveStage.Running, Turn: { } running }:
                if (expected != new TurnKey(running.Address.Launch.Attempt, running.Address.Launch.Turn))
                {
                    complete(Refused(new SendProblem.StaleTarget()));
                    return;
                }
                Background(() => running.SendAsync(text, stopTurn), complete,
                    error => complete(Refused(new SendProblem.CannotStart(new StartProblem.CannotRecord(error.Message)))));
                return;
            case { Stage: LiveStage.Starting }:
                complete(Refused(StartingMessage));
                return;
            case not null:
                complete(Refused(new SendProblem.Ending(definition.Title)));
                return;
        }
        // A task this window holds back, for a refused or blocked step, projects as anything but waiting.
        var view = Project(record).Tasks.GetValueOrDefault(task);
        if (Resting(record, view) is not { } resting)
        {
            complete(Refused(Unrested(view)));
            return;
        }
        if (expected != resting.Turn)
        {
            complete(Refused(new SendProblem.StaleTarget()));
            return;
        }
        if (record.Preparations.ContainsKey(resting.Next))
        {
            complete(Refused(StartingMessage));
            return;
        }
        // The task stays out of dispatch until the message is written.
        _live[task] = new(LiveStage.Settling);
        Offload(() => Queue(task, resting, turn.Prompt), result =>
        {
            _live.Remove(task);
            complete(result);
        }, error =>
        {
            _live.Remove(task);
            complete(Refused(new SendProblem.CannotStart(new StartProblem.CannotRecord(error.Message))));
        });
    }

    /// <summary>Why a task that does not rest between turns takes no message now.</summary>
    private static string Unrested(TaskView? view) => view?.State switch
    {
        null or TaskState.Pending or TaskState.Ready => NotStartedMessage,
        TaskState.Starting => StartingMessage,
        TaskState.Running or TaskState.Settling => EndingMessage,
        TaskState.Waiting => NotWaitingMessage,
        TaskState.Blocked or TaskState.Refused or TaskState.Uncertain => HeldMessage,
        _ => ClosedMessage,
    };

    /// <summary>
    /// Writes the person's message to the resting attempt's log under the task's lease. Nothing else of this window touches
    /// the task meanwhile, and another window cannot command the run, so only a stop recorded since can stand in its way.
    /// </summary>
    private SendResult Queue(TaskId task, RestingAttempt resting, string text)
    {
        if (_permit!.TakeTask(task) is not LeaseTake.Taken taken) return Refused(BusyMessage);
        using (taken.Lease)
        using (var authority = taken.Lease.Use())
        {
            if (authority is null) return Refused(BusyMessage);
            _runs.Probe?.Invoke("coordinator.queue.before");
            if (Record() is not { } record) return Refused(Describe(new(RunProblem.StorageUnavailable)));
            if (Unconversable(record, task) is { } stopped) return Refused(stopped);
            using var append = AttemptLog.Open(resting.Folder);
            append.Append(new AttemptEvent.MessageQueued(_runs.TimeProvider.GetUtcNow(), text, false) { Id = Guid.CreateVersion7().ToString() });
            return new SendResult.Queued();
        }
    }

    private void MarkDoneCore(RunRecord record, TaskId task, TurnKey expected, Action<ConversationCommandResult> complete)
    {
        if (Unconversable(record, task) is { } unconversable)
        {
            complete(Declined(Text(unconversable)));
            return;
        }
        if (_live.ContainsKey(task) ||
            Resting(record, Project(record).Tasks.GetValueOrDefault(task)) is not { Log.Status: AttemptStatus.WaitingForInput } resting)
        {
            complete(Declined(NotWaitingMessage));
            return;
        }
        if (expected != resting.Turn)
        {
            complete(new(CommandOutcome.Stale, "The conversation has moved to another turn."));
            return;
        }
        if (Replied(record, resting))
        {
            complete(Declined(ReplyPendingMessage));
            return;
        }
        // The turn's own operation, so a publication that this cannot finish converges with the one Resume finishes.
        var operation = RunOperations.Turn(record, resting.Last);
        _live[task] = new(LiveStage.Settling);
        Background(async () =>
        {
            var closing = await _runs.CloseResting(_permit!, OperationIds.Derive(operation, "conversation/mark-done"), resting.Attempt,
                new RestingEnd.MarkDone()).ConfigureAwait(false);
            switch (closing)
            {
                case RestingClose.Closed closed:
                    if (Finish(closed.Attempt.Lease, task, resting.Attempt, operation) is { } refusal)
                    {
                        // The closure is recorded. Its publication is finished later, under a lease of its own.
                        closed.Attempt.Lease.Dispose();
                        return Declined(Describe(refusal));
                    }
                    return closed.Attempt.Release() is Release.Held held ? Declined(Describe(held.Reason)) : Applied("The task was marked done.");
                case RestingClose.Blocked blocked:
                    return Declined(blocked.Block.Detail);
                case RestingClose.Refused refused:
                    return Declined(Describe(refused.Reason));
                default:
                    throw new InvalidOperationException();
            }
        }, result =>
        {
            _live.Remove(task);
            complete(result);
        }, error =>
        {
            _live.Remove(task);
            complete(Declined(error.Message));
        });
    }

    private void CancelCore(RunRecord record, TaskId task, TurnKey expected, Action<ConversationCommandResult> complete)
    {
        if (Unconversable(record, task) is { } unconversable)
        {
            complete(Declined(Text(unconversable)));
            return;
        }
        switch (_live.GetValueOrDefault(task))
        {
            case { Stage: LiveStage.Running, Turn: { } running } live:
                if (expected != new TurnKey(running.Address.Launch.Attempt, running.Address.Launch.Turn))
                {
                    complete(new(CommandOutcome.Stale, "The conversation has moved to another turn."));
                    return;
                }
                _live[task] = live with { Cancelled = true };
                Background(() => running.CancelAsync(), result => complete(result is SendResult.Refused
                    ? Declined(NothingToCancelMessage) : Applied("The cancellation was recorded.")), error => complete(Declined(error.Message)));
                return;
            case not null:
                complete(Declined(EndingMessage));
                return;
        }
        if (Resting(record, Project(record).Tasks.GetValueOrDefault(task)) is not { } resting)
        {
            complete(Declined(NothingToCancelMessage));
            return;
        }
        if (expected != resting.Turn)
        {
            complete(new(CommandOutcome.Stale, "The conversation has moved to another turn."));
            return;
        }
        var operation = OperationIds.Derive(RunOperations.Turn(record, resting.Last), "conversation/cancel");
        _live[task] = new(LiveStage.Settling);
        Background(async () =>
        {
            var closing = await _runs.CloseResting(_permit!, operation, resting.Attempt, new RestingEnd.Cancel()).ConfigureAwait(false);
            return closing switch
            {
                RestingClose.Closed closed => closed.Attempt.Release() is Release.Held held ? Declined(Describe(held.Reason)) : Applied("The cancellation was recorded."),
                RestingClose.Blocked blocked => Declined(blocked.Block.Detail),
                RestingClose.Refused refused => Declined(Describe(refused.Reason)),
                _ => throw new InvalidOperationException(),
            };
        }, result =>
        {
            _live.Remove(task);
            complete(result);
        }, error =>
        {
            _live.Remove(task);
            complete(Declined(error.Message));
        });
    }
}
