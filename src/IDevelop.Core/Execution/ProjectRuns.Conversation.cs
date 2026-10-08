using System.Collections.Immutable;
using IDevelop.Workflows;
using AsyncTask = System.Threading.Tasks.Task;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    public IConversationSession OpenConversation(TaskId task) => new ConversationSession(this, task);

    private sealed class ConversationSession : IConversationSession
    {
        private readonly ProjectRuns _owner;
        private int _disposed;

        public ConversationSession(ProjectRuns owner, TaskId task)
        {
            _owner = owner;
            Task = task;
            owner.ConversationChanged += OnChanged;
        }

        public TaskId Task { get; }

        public event Action<long>? Changed;

        private bool Unavailable => Volatile.Read(ref _disposed) != 0 || _owner._leaving is not null;

        public ConversationSnapshot Snapshot
        {
            get
            {
                lock (_owner._gate)
                {
                    var published = _owner._published.GetValueOrDefault(Task);
                    var record = published?.Record;
                    var task = _owner.Resolve(Task);
                    var client = task?.Execution?.Client ?? record?.Requested.Client;
                    var capabilities = client is { } id
                        ? ClientPolicy.For(id, record?.ReadOnly ?? task?.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly },
                            task?.Conversation ?? record?.Conversation ?? ConversationMode.Autonomous,
                            record?.Subject is not null || task?.Blueprint.Work is WorkSpec.Review, _owner._questions).Capabilities
                        : new ClientCapabilities(false, false, "This task has no agent.");
                    var unavailable = Unavailable ? "The conversation owner is closed." : null;
                    var sendReason = unavailable ?? (task is null ? "The task no longer exists." : Describe(_owner.CheckSend(task)));
                    var active = _owner._active.GetValueOrDefault(Task);
                    var running = active is not null && record is { Status: AttemptStatus.Running, Stopping: false };
                    var waiting = active is null && record is { Status: AttemptStatus.WaitingForInput };
                    var actions = new ConversationActions(
                        Availability(sendReason is null, sendReason, "Send a message."),
                        Availability(sendReason is null && running && record?.Subject is null,
                            sendReason ?? "Stop and send requires a running agent turn.", "Stop this turn and send a message."),
                        Availability(unavailable is null && (active is not null || record?.Status is AttemptStatus.WaitingForInput or AttemptStatus.InReview),
                            unavailable ?? "There is no owned run to cancel.", "Cancel this attempt."),
                        Availability(unavailable is null && task is not null && waiting,
                            unavailable ?? (task is null ? "The task no longer exists." : "The task must finish teardown and wait for input."), "Mark this task done."),
                        Availability(unavailable is null && waiting && record?.SessionId is not null,
                            unavailable ?? "The task must finish teardown and wait with a session.", "Open this session in a terminal."));
                    return new ConversationSnapshot(Interlocked.Read(ref _owner._revision), published?.LogRevision ?? 0,
                        record is null ? null : new TurnKey(record.Id, record.Turns.Count), record, capabilities, actions);
                }
            }
        }

        private static ActionAvailability Availability(bool enabled, string? reason, string action) => new(enabled, enabled ? action : reason ?? "Unavailable.");

        private static string? Describe(SendProblem? problem) => problem switch
        {
            null => null,
            SendProblem.ClosedOwner => "The project is closed.",
            SendProblem.MissingTask => "The task no longer exists.",
            SendProblem.StaleTarget => "The conversation has moved to another turn.",
            SendProblem.EmptyMessage => "Write a message first.",
            SendProblem.NeverRan => "Run this task first.",
            SendProblem.NoSession => "The client reported no session to resume.",
            SendProblem.NoSessionYet => "The client has not reported its session yet.",
            SendProblem.ClientChanged => "The task now uses another client. Start a new attempt first.",
            SendProblem.Ending => "The turn is ending. Wait for teardown to finish.",
            SendProblem.NotReviewing => "The review is no longer running.",
            SendProblem.RunUnavailable unavailable => unavailable.Reason,
            SendProblem.CannotStart cannot => StartReason(cannot.Problem),
            _ => throw new InvalidOperationException("Unknown send problem."),
        };

        private static string StartReason(StartProblem problem) => problem switch
        {
            StartProblem.CannotRecord record => record.Reason,
            StartProblem.ClientMissing missing => missing.Reason,
            StartProblem.ClientUnready unready => unready.Reason,
            StartProblem.ModelUnready model => model.Reason,
            StartProblem.AlreadyRunning => "Another run owns this task.",
            StartProblem.RunInAnotherWindow => "Another window owns this task.",
            StartProblem.RunOwned owned => $"A run of the \"{owned.Workflow}\" workflow owns this task.",
            StartProblem.UnderReview review => $"The task is under review by {review.Review}.",
            StartProblem.NoConversation => "This node has no agent conversation.",
            StartProblem.NoAgent => "Choose an agent before continuing.",
            StartProblem.NoModel => "Choose a model before continuing.",
            StartProblem.ClientChecking => "The client is still being checked.",
            StartProblem.ModelNotOffered => "The configured model is unavailable.",
            StartProblem.ReasoningNotOffered => "The configured reasoning level is unavailable.",
            StartProblem.NoReadOnlyMode => "This client cannot run a read-only task.",
            _ => "The task cannot continue with its current configuration and ownership.",
        };

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
                return new HistoryResult.Unavailable("The conversation owner is closed.");
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
            if (Unavailable)
            {
                return new HistoryResult.Unavailable("The conversation owner is closed.");
            }

            return ConversationPager.Read($"{_owner._projectFolder}/{Task}", histories,
                Interlocked.Read(ref _owner._revision), query, count);
        }, ct);

        public Task<RequestRecord?> ReadRequestAsync(RequestKey key, CancellationToken ct) => ct.IsCancellationRequested
            ? AsyncTask.FromCanceled<RequestRecord?>(ct) : AsyncTask.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (Unavailable)
            {
                return null;
            }

            lock (_owner._gate)
            {
                if (_owner._active.GetValueOrDefault(Task) is { } active && active.AttemptId == key.Turn.Attempt)
                {
                    return active.ReadRequest(key);
                }
            }

            var request = _owner.ReadConversationHistory(Task, key.Turn.Attempt)?.Record.Requests.GetValueOrDefault(key);
            ct.ThrowIfCancellationRequested();
            return Unavailable ? null : request;
        }, ct);

        public Task<SendResult> SendAsync(TurnKey expected, string text, bool stopTurn, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TaskDefinition task;
            lock (_owner._gate)
            {
                if (Unavailable)
                {
                    return Refused(new SendProblem.ClosedOwner());
                }

                if (_owner.Resolve(Task) is not { } current)
                {
                    return Refused(new SendProblem.MissingTask());
                }

                if (_owner.Current(Task) != expected)
                {
                    return Refused(new SendProblem.StaleTarget());
                }

                task = current;
            }

            return _owner.SendAsync(task, text, stopTurn, ct, expected);
        }

        private static Task<SendResult> Refused(SendProblem problem) => AsyncTask.FromResult<SendResult>(new SendResult.Refused(problem));

        public Task<AnswerResult> AnswerAsync(RequestKey key, QuestionsReply reply, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_owner._gate)
            {
                if (Unavailable)
                {
                    return AsyncTask.FromResult(new AnswerResult(AnswerOutcome.Unavailable, "The conversation owner is closed."));
                }

                return _owner._active.TryGetValue(Task, out var run) ? run.AnswerAsync(key, reply, ct)
                    : AsyncTask.FromResult(new AnswerResult(AnswerOutcome.Stale, "The question's turn has ended."));
            }
        }

        public async Task<ConversationCommandResult> CancelAsync(TurnKey expected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_owner._gate)
            {
                if (Check(expected) is { } refused)
                {
                    return refused;
                }

                if (!_owner._active.ContainsKey(Task) && _owner.Latest.GetValueOrDefault(Task)?.Status is not (AttemptStatus.WaitingForInput or AttemptStatus.InReview))
                {
                    return new ConversationCommandResult(CommandOutcome.Refused, "There is no owned attempt to cancel.");
                }
            }

            var result = await _owner.CancelCoreAsync(Task, expected, ct);
            return new ConversationCommandResult(result.Applied ? CommandOutcome.Applied : result.Stale ? CommandOutcome.Stale : CommandOutcome.Refused,
                result.Applied ? "The cancellation was recorded." : result.Problem is { } problem ? StartReason(problem) : "The attempt is no longer available.");
        }

        public Task<ConversationCommandResult> MarkDoneAsync(TurnKey expected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_owner._gate)
            {
                if (Check(expected) is { } refused)
                {
                    return AsyncTask.FromResult(refused);
                }

                if (_owner.Resolve(Task) is null)
                {
                    return AsyncTask.FromResult(new ConversationCommandResult(CommandOutcome.Unavailable, "The task no longer exists."));
                }

                if (_owner._active.ContainsKey(Task) || _owner.Latest.GetValueOrDefault(Task)?.Status != AttemptStatus.WaitingForInput)
                {
                    return AsyncTask.FromResult(new ConversationCommandResult(CommandOutcome.Refused, "The task must finish teardown and wait for input."));
                }
            }

                    var done = _owner.MarkDoneCore(Task, expected);
            return AsyncTask.FromResult(new ConversationCommandResult(done.Applied ? CommandOutcome.Applied : done.Problem is null ? CommandOutcome.Stale : CommandOutcome.Refused,
                            done.Applied ? "The task was marked done." : done.Problem is { } problem ? StartReason(problem) : "The attempt changed."));
        }

        public Task<TerminalResult> OpenInTerminalAsync(TurnKey expected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_owner._gate)
            {
                if (Unavailable)
                {
                    return AsyncTask.FromResult<TerminalResult>(new TerminalResult.Refused(new TerminalProblem.ClosedOwner()));
                }

                if (_owner.Current(Task) != expected)
                {
                    return AsyncTask.FromResult<TerminalResult>(new TerminalResult.Refused(new TerminalProblem.StaleTarget()));
                }
            }

            return AsyncTask.FromResult(_owner.OpenInTerminal(Task, expected));
        }

        private ConversationCommandResult? Check(TurnKey expected) => Unavailable
            ? new ConversationCommandResult(CommandOutcome.Unavailable, "The conversation owner is closed.")
            : _owner.Current(Task) != expected ? new ConversationCommandResult(CommandOutcome.Stale, "The conversation has moved to another turn.") : null;

        private void OnChanged(TaskId? task, long revision)
        {
            if (task is not null && task != Task)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    Changed?.Invoke(revision);
                }
            });
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _owner.ConversationChanged -= OnChanged;
        }
    }
}
