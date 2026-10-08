using System.Collections.Immutable;
using IDevelop.Workflows;
using AsyncTask = System.Threading.Tasks.Task;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    /// <summary>
    /// The conversation of <paramref name="task"/> as <paramref name="coordinator"/>'s run owns it (E3c.1). Its current attempt is
    /// the task's newest attempt in that run, and every command goes to the coordinator with the run's address, so a window
    /// that only reads the run gets <see cref="CommandOutcome.Unavailable"/>. The attempt list is the task's whole history.
    /// </summary>
    internal IConversationSession OpenConversation(WorkflowRunCoordinator coordinator, TaskId task) => new RunConversation(this, coordinator, task);

    private sealed class RunConversation : IConversationSession
    {
        private const string ClosedOwner = "The conversation owner is closed.";
        private readonly ProjectRuns _owner;
        private readonly WorkflowRunCoordinator _run;
        private readonly Lock _gate = new();
        private object? _shown;
        private int _disposed;

        public RunConversation(ProjectRuns owner, WorkflowRunCoordinator run, TaskId task)
        {
            _owner = owner;
            _run = run;
            Task = task;
            _shown = Shown(run.View);
            owner.ConversationChanged += OnChanged;
            run.Changed += OnRunChanged;
        }

        public TaskId Task { get; }

        public event Action<long>? Changed;

        private bool Unavailable => Volatile.Read(ref _disposed) != 0 || _owner._leaving is not null;

        private RunAddress Address => _run.Address;

        /// <summary>The task's newest attempt in the run, live while this window runs its turn.</summary>
        private (TaskView? State, CachedConversation? Read) Current(RunView view)
        {
            var state = view.Tasks.GetValueOrDefault(Task);
            return (state, state?.Attempt is { } attempt ? _owner.ReadRunHistory(Address.Workflow, Address.Run, Task, attempt) : null);
        }

        public ConversationSnapshot Snapshot
        {
            get
            {
                var view = _run.View;
                var (state, read) = Current(view);
                var latest = read?.History.Record;
                var unavailable = Unavailable ? ClosedOwner : !view.Controlled ? WorkflowRunCoordinator.ElsewhereMessage : view.Phase switch
                {
                    RunPhase.Approved => null,
                    RunPhase.StopRequested => WorkflowRunCoordinator.StoppingMessage,
                    _ => WorkflowRunCoordinator.EndedMessage,
                };
                var running = state?.State == TaskState.Running && latest is { Status: AttemptStatus.Running, BetweenTurns: false, Stopping: false };
                var resting = state?.State == TaskState.Waiting && latest is
                    { BetweenTurns: true, Status: AttemptStatus.WaitingForInput or AttemptStatus.Running or AttemptStatus.InReview };
                var send = unavailable ?? state?.State switch
                {
                    TaskState.Running when latest?.SessionId is null => "The client has not reported its session yet.",
                    TaskState.Running => null,
                    TaskState.Waiting when resting => null,
                    TaskState.Waiting => WorkflowRunCoordinator.NotWaitingMessage,
                    TaskState.Starting => WorkflowRunCoordinator.StartingMessage,
                    TaskState.Settling => WorkflowRunCoordinator.EndingMessage,
                    null or TaskState.Pending or TaskState.Ready => WorkflowRunCoordinator.NotStartedMessage,
                    TaskState.Blocked or TaskState.Refused or TaskState.Uncertain => WorkflowRunCoordinator.HeldMessage,
                    _ => WorkflowRunCoordinator.ClosedMessage,
                };
                var actions = new ConversationActions(
                    Availability(send is null, send, "Send a message."),
                    Availability(unavailable is null && running && latest?.SessionId is not null,
                        unavailable ?? "Stop and send requires a running agent turn.", "Stop this turn and send a message."),
                    Availability(unavailable is null && (running || resting),
                        unavailable ?? WorkflowRunCoordinator.NothingToCancelMessage, "Cancel this attempt."),
                    Availability(unavailable is null && resting && latest?.Status == AttemptStatus.WaitingForInput,
                        unavailable ?? WorkflowRunCoordinator.NotWaitingMessage, "Mark this task done."),
                    Availability(false, unavailable ?? "A workflow run owns this task's session.", ""));
                return new ConversationSnapshot(Interlocked.Read(ref _owner._revision), read?.Lines ?? 0,
                    latest is null ? null : new TurnKey(latest.Id, latest.Turns.Count), latest, Capabilities(latest), actions);
            }
        }

        private ClientCapabilities Capabilities(AttemptRecord? latest)
        {
            if (latest is not null)
            {
                return ClientPolicy.For(latest.Requested.Client, latest.ReadOnly, latest.Conversation, latest.Subject is not null, _owner._questions).Capabilities;
            }

            var definition = Definition();
            return definition?.Execution?.Client is { } client
                ? ClientPolicy.For(client, definition.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly }, definition.Conversation,
                    definition.Blueprint.Work is WorkSpec.Review, _owner._questions).Capabilities
                : new ClientCapabilities(false, false, "This task has no agent.");
        }

        private TaskDefinition? Definition() => _owner.TurnStore.Read(Address.Workflow, Address.Run) is RunRead.Loaded loaded
            ? loaded.Record.Revision.Snapshot.Tasks.GetValueOrDefault(Task) : null;

        private static ActionAvailability Availability(bool enabled, string? reason, string action) => new(enabled, enabled ? action : reason ?? "Unavailable.");

        public Task<ImmutableArray<AttemptSummary>> ListAttemptsAsync(CancellationToken ct) => ct.IsCancellationRequested
            ? AsyncTask.FromCanceled<ImmutableArray<AttemptSummary>>(ct) : AsyncTask.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var attempts = Unavailable ? ImmutableArray<AttemptSummary>.Empty : _owner.ListConversationAttempts(Task, ct);
            ct.ThrowIfCancellationRequested();
            return Unavailable ? ImmutableArray<AttemptSummary>.Empty : attempts;
        }, ct);

        public Task<HistoryResult> ReadPageAsync(AttemptId head, HistoryQuery query, int count, CancellationToken ct) => ct.IsCancellationRequested
            ? AsyncTask.FromCanceled<HistoryResult>(ct) : AsyncTask.Run<HistoryResult>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return new HistoryResult.Unavailable(ClosedOwner);
            }

            var histories = new List<AttemptHistory>();
            var seen = new HashSet<AttemptId>();
            AttemptId? link = head;
            while (link is { } id && seen.Add(id) && _owner.ReadConversationHistory(Task, id) is { } history)
            {
                ct.ThrowIfCancellationRequested();
                histories.Add(history);
                link = history.Record.Continues;
            }

            histories.Reverse();
            ct.ThrowIfCancellationRequested();
            return Unavailable ? new HistoryResult.Unavailable(ClosedOwner)
                : ConversationPager.Read($"{_owner._projectFolder}/{Task}/run/{Address.Workflow}/{Address.Run}", histories,
                    Interlocked.Read(ref _owner._revision), query, count);
        }, ct);

        public Task<RequestRecord?> ReadRequestAsync(RequestKey key, CancellationToken ct) => ct.IsCancellationRequested
            ? AsyncTask.FromCanceled<RequestRecord?>(ct) : AsyncTask.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var request = Unavailable ? null : _owner.ReadConversationHistory(Task, key.Turn.Attempt)?.Record.Requests.GetValueOrDefault(key);
            ct.ThrowIfCancellationRequested();
            return Unavailable ? null : request;
        }, ct);

        public Task<SendResult> SendAsync(TurnKey expected, string text, bool stopTurn, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Unavailable ? AsyncTask.FromResult<SendResult>(new SendResult.Refused(new SendProblem.ClosedOwner()))
                : _run.Send(Address, Task, expected, text, stopTurn, ct);
        }

        /// <summary>A run's questions are deferred into text at once, so the person answers them with a message.</summary>
        public Task<AnswerResult> AnswerAsync(RequestKey key, QuestionsReply reply, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return AsyncTask.FromResult(Unavailable ? new AnswerResult(AnswerOutcome.Unavailable, ClosedOwner)
                : !_run.Controlled ? new AnswerResult(AnswerOutcome.Unavailable, WorkflowRunCoordinator.ElsewhereMessage)
                : new AnswerResult(AnswerOutcome.Stale, "Answer a workflow run's question with a message."));
        }

        public Task<ConversationCommandResult> CancelAsync(TurnKey expected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Unavailable ? AsyncTask.FromResult(new ConversationCommandResult(CommandOutcome.Unavailable, ClosedOwner))
                : _run.Cancel(Address, Task, expected, ct);
        }

        public Task<ConversationCommandResult> MarkDoneAsync(TurnKey expected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Unavailable ? AsyncTask.FromResult(new ConversationCommandResult(CommandOutcome.Unavailable, ClosedOwner))
                : _run.MarkDone(Address, Task, expected, ct);
        }

        /// <summary>A turn taken in the client's own terminal would bypass the run, so a run-owned session stays in the run.</summary>
        public Task<TerminalResult> OpenInTerminalAsync(TurnKey expected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return AsyncTask.FromResult<TerminalResult>(new TerminalResult.Refused(new TerminalProblem.ClosedOwner()));
            }

            var name = _owner.TurnStore.Read(Address.Workflow, Address.Run) is RunRead.Loaded loaded
                ? loaded.Record.Revision.Snapshot.DisplayName : Address.Workflow.ToString();
            return AsyncTask.FromResult<TerminalResult>(new TerminalResult.Refused(new TerminalProblem.Blocked(new StartProblem.RunOwned(name))));
        }

        private void OnChanged(TaskId? task, long revision)
        {
            if (task is not null && task != Task)
            {
                return;
            }

            Raise(revision);
        }

        /// <summary>Raises <see cref="Changed"/> when the run's decision changed what this task's conversation shows.</summary>
        private void OnRunChanged(object? sender, EventArgs e)
        {
            var shown = Shown(_run.View);
            lock (_gate)
            {
                if (Equals(shown, _shown))
                {
                    return;
                }

                _shown = shown;
            }

            Raise(Interlocked.Increment(ref _owner._revision));
        }

        /// <summary>What of the run's view this task's conversation shows.</summary>
        private object Shown(RunView view)
        {
            var state = view.Tasks.GetValueOrDefault(Task);
            return (view.Phase, view.Controlled, state?.State, state?.Attempt, state?.Status);
        }

        private void Raise(long revision) => ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Changed?.Invoke(revision);
            }
        });

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _owner.ConversationChanged -= OnChanged;
            _run.Changed -= OnRunChanged;
        }
    }
}
