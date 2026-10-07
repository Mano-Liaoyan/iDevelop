using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    /// <summary>
    /// One attempt while it runs. Its turns run one after another, each one client process, under one log, one lock, and
    /// one channel. One drain owns parsing, folding, answers, expiry, Send, Stop, and exit. It appends and flushes each
    /// person's command before acknowledging it, so the intent always precedes the exit it causes.
    /// Stopping uses the protocol interrupt when available, then the process tree, within ShutdownTime. A turn's output
    /// ends at its exit, so nothing a turn left running writes into the next one. What the client leaves running when it
    /// exits on its own keeps running, as after a command in a terminal, even while it still holds the turn's output.
    /// The run disposes each turn's process before it starts the next turn or releases the lock, so a late stop does
    /// nothing. The log closes and the lock is released last, so nothing appends after another instance could take over.
    /// </summary>
    private sealed class ActiveRun
    {
        private readonly ProjectRuns _owner;
        private readonly AttemptLog _log;
        private readonly RunLock _held;
        private readonly Channel<Input> _events = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _abandon = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private ImmutableDictionary<string, LiveMessageBuffer> _buffers = ImmutableDictionary<string, LiveMessageBuffer>.Empty;
        private long _revision;
        private long _publishedRevision;
        private AttemptRecord _publishedRecord;
        private int _presentationSequence;
        private LaunchPlan _plan;
        private Turn _turn;
        private bool _messageWaiting;
        private ConversationMode? _conversation;
        private bool _closed;
        private AttemptEvent.InterruptRequested? _leave;
        private readonly List<string> _questionOrder = [];
        private readonly HashSet<Input.Answer> _deliveries = [];

        public ActiveRun(ProjectRuns owner, long order, LaunchPlan plan, ChildProcess process, AttemptLog log, RunLock held, AttemptRecord record)
        {
            _owner = owner;
            Order = order;
            _plan = plan;
            _turn = new Turn(process, plan.Client.Protocol(plan.Request));
            _log = log;
            _held = held;
            Record = record;
            TaskId = record.Task;
            Title = record.TaskTitle;
            AttemptId = record.Id;
            _revision = log.LineCount;
            _publishedRevision = _revision;
            _publishedRecord = record;
        }

        public long Order { get; }

        private AttemptRecord Record { get; set; }

        public TaskId TaskId { get; }

        public string Title { get; }

        public AttemptId AttemptId { get; }

        public Task Completion => _finished.Task;

        public (long Revision, long LogRevision, ImmutableDictionary<string, LiveMessageBuffer> Buffers) Live
        {
            get
            {
                lock (_gate)
                {
                    return (_revision, _log.LineCount, _buffers);
                }
            }
        }

        public void Start() => _ = Task.Run(RunAsync);

        public Task<SendResult> StopAsync(AttemptEvent request, TurnKey? expected = null, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var done = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _events.Writer.TryWrite(new Input.Person(request, done, null, expected, ct)) ? done.Task : Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.StaleTarget()));
            }
        }

        public Task<SendResult> SendAsync(TaskDefinition task, string text, bool stopTurn, CancellationToken ct, TurnKey? expected)
        {
            lock (_gate)
            {
                if (Problem() is { } problem)
                {
                    return Task.FromResult<SendResult>(new SendResult.Refused(problem));
                }

                var done = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var message = new AttemptEvent.MessageQueued(_owner.TimeProvider.GetUtcNow(), text, stopTurn) { Id = Guid.CreateVersion7().ToString() };
                if (!_events.Writer.TryWrite(new Input.Person(message, done, task, expected, ct)))
                {
                    return Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle)));
                }

                _messageWaiting = true;
                return done.Task;
            }
        }

        public Task<SendResult> GuideAsync(TaskDefinition task, string text, CancellationToken ct, TurnKey? expected)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle)));
                }

                var done = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_events.Writer.TryWrite(new Input.Person(new AttemptEvent.GuidanceAdded(_owner.TimeProvider.GetUtcNow(), text), done, task, expected, ct)))
                {
                    return Task.FromResult<SendResult>(new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle)));
                }

                return done.Task;
            }
        }

        public Task<AnswerResult> AnswerAsync(RequestKey key, QuestionsReply reply, CancellationToken ct)
        {
            lock (_gate)
            {
                var done = new TaskCompletionSource<AnswerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_events.Writer.TryWrite(new Input.Answer(key, reply, done, ct)))
                {
                    done.TrySetResult(new AnswerResult(AnswerOutcome.Stale, "The turn has ended."));
                }

                return done.Task;
            }
        }

        public AttemptHistory? ReadHistory(AttemptRecord published)
        {
            ImmutableDictionary<string, LiveMessageBuffer> buffers;
            lock (_gate)
            {
                buffers = _buffers;
            }

            return AttemptLog.ReadHistory(_owner._attempts, TaskId, AttemptId, buffers) is { } history ? history with { Record = published } : null;
        }

        public RequestRecord? ReadRequest(RequestKey key)
        {
            lock (_gate)
            {
                return Record.Requests.GetValueOrDefault(key);
            }
        }

        public SendProblem? GuideProblem()
        {
            lock (_gate)
            {
                return _closed ? new SendProblem.Ending(Record.TaskTitle) : null;
            }
        }

        public SendProblem? SendProblem()
        {
            lock (_gate)
            {
                return Problem();
            }
        }

        public void Abandon(AttemptEvent.InterruptRequested leave)
        {
            lock (_gate)
            {
                _leave = leave;
                _abandon.Cancel();
            }
        }

        private SendProblem? Problem() => (_closed, Record.SessionId) switch
        {
            (true, _) => new SendProblem.Ending(Record.TaskTitle),
            (_, null) => new SendProblem.NoSessionYet(Record.Requested.Client),
            _ => null,
        };

        private async Task RunAsync()
        {
            var exited = false;
            try
            {
                Read(_turn);
                Output(_turn.Protocol.Start());
                await foreach (var next in _events.Reader.ReadAllAsync(_abandon.Token))
                {
                    _abandon.Token.ThrowIfCancellationRequested();
                    switch (next)
                    {
                        case Input.Line line when line.Turn == _turn:
                            _log.AppendOutput(line.Text);
                            if (!string.IsNullOrWhiteSpace(line.Text))
                            {
                                try
                                {
                                    Output(_turn.Protocol.Read(line.Text));
                                }
                                catch (JsonException)
                                {
                                    Append(new AttemptEvent.Agent(_owner.TimeProvider.GetUtcNow(), new AgentEvent.Notice($"iDevelop could not read a line that {Clients.Name(_plan.Settings.Client)} printed.")));
                                }
                                catch (ProtocolException error)
                                {
                                    Fail(error.Message);
                                }
                            }

                            break;
                        case Input.Person person:
                            Accept(person);
                            break;
                        case Input.Answer answer:
                            AcceptAnswer(answer);
                            break;
                        case Input.QuestionTick tick when tick.Turn == _turn:
                            ExpireQuestions();
                            break;
                        case Input.WriteDone write when write.Turn == _turn:
                            _turn.PendingWrites--;
                            if (write.RequestId is { } request && write.Delivery is null)
                            {
                                Append(new AttemptEvent.RequestClosed(_owner.TimeProvider.GetUtcNow(), request, write.Success ? RequestCloseReason.PolicyDenied : RequestCloseReason.DeliveryUnknown));
                            }

                            if (write.Delivery is { } reply)
                            {
                                _deliveries.Remove(reply);
                                if (!write.Success)
                                {
                                    Append(new AttemptEvent.RequestClosed(_owner.TimeProvider.GetUtcNow(), reply.Key.Id, RequestCloseReason.DeliveryUnknown));
                                }

                                Publish();
                                reply.Done.TrySetResult(new AnswerResult(write.Success ? AnswerOutcome.Recorded : AnswerOutcome.DeliveryUnknown,
                                    write.Success ? "The answer was recorded." : "The answer was recorded, but delivery could not be confirmed."));
                            }

                            if (!write.Success && _turn.ClientRuns && !_turn.Stopping)
                            {
                                Fail("iDevelop could not write to the client's input pipe.");
                            }

                            if (_turn.PendingWrites == 0 && _turn.Exit is { } heldExit)
                            {
                                _events.Writer.TryWrite(heldExit);
                            }

                            break;
                        case Input.Deadline deadline when deadline.Turn == _turn && !_turn.Process.HasExited:
                            if (!_turn.Stopping && Record.Verdict is AgentEvent.Succeeded)
                            {
                                Append(new AttemptEvent.ShutdownForced(_owner.TimeProvider.GetUtcNow()));
                            }

                            _turn.Process.StopTree();
                            CloseInput();
                            break;
                        case Input.Exit exit when exit.Turn == _turn:
                            if (_turn.PendingWrites > 0)
                            {
                                _turn.Exit = exit;
                                break;
                            }

                            _turn.Exit = null;
                            foreach (var (id, buffer) in _buffers.OrderBy(pair => pair.Value.Order).ThenBy(pair => pair.Value.PresentationSequence))
                            {
                                Append(new AttemptEvent.Agent(_owner.TimeProvider.GetUtcNow(), new AgentEvent.Message(buffer.Text) { Id = id, Partial = true }) { Order = buffer.Order, PresentationSequence = buffer.PresentationSequence });
                            }

                            lock (_gate)
                            {
                                _buffers = ImmutableDictionary<string, LiveMessageBuffer>.Empty;
                            }

                            if (_turn.Failure is { } failure && Record.Verdict != new AgentEvent.Failed(failure))
                            {
                                Append(new AttemptEvent.Agent(_owner.TimeProvider.GetUtcNow(), new AgentEvent.Failed(failure)));
                            }

                            Append(new AttemptEvent.Exited(_owner.TimeProvider.GetUtcNow(), exit.Code, exit.Stderr) { Tree = GitTree.Snapshot(_owner._projectFolder) });
                            exited = true;
                            Publish();
                            if (Record.Status != AttemptStatus.Running || !await NextTurnAsync())
                            {
                                return;
                            }

                            exited = false;
                            break;
                        case Input.Line or Input.WriteDone or Input.Deadline or Input.QuestionTick or Input.Exit:
                            break;
                        default:
                            throw new InvalidOperationException("Unknown run input.");
                    }

                    Publish();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Record = AttemptReducer.Abandon(Record, CannotWriteLog(error), _owner.TimeProvider.GetUtcNow());
            }
            finally
            {
                lock (_gate)
                {
                    _closed = true;
                    _turn.Open = false;
                    _events.Writer.TryComplete();
                    while (_events.Reader.TryRead(out var pending))
                    {
                        if (pending is Input.Person person)
                        {
                            person.Done.TrySetResult(new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle)));
                        }
                        else if (pending is Input.Answer answer)
                        {
                            answer.Done.TrySetResult(new AnswerResult(AnswerOutcome.Stale, "The turn has ended."));
                        }
                        else if (pending is Input.WriteDone { Delivery: { } delivery })
                        {
                            delivery.Done.TrySetResult(new AnswerResult(AnswerOutcome.DeliveryUnknown, "Answer delivery could not be confirmed."));
                        }
                    }
                }

                foreach (var delivery in _deliveries)
                {
                    delivery.Done.TrySetResult(new AnswerResult(AnswerOutcome.DeliveryUnknown, "Answer delivery could not be confirmed."));
                }

                if (exited)
                {
                    _turn.Process.LeaveDescendantsRunning();
                }
                else
                {
                    _turn.Process.StopTree();
                }

                await DisposeTurnAsync();
                await _owner.BeforeRelease();
                try
                {
                    if (_leave is { } leave && Record is { Status: AttemptStatus.Running, InterruptReason: null })
                    {
                        Append(leave);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Record = AttemptReducer.Abandon(Record, CannotWriteLog(error), _owner.TimeProvider.GetUtcNow());
                }

                _log.Dispose();
                _held.Dispose();
                try
                {
                    _owner.Finish(this, Record);
                }
                finally
                {
                    _finished.TrySetResult();
                }
            }
        }

        private void Accept(Input.Person person)
        {
            try
            {
                if (person.Ct.IsCancellationRequested)
                {
                    person.Done.TrySetCanceled(person.Ct);
                    return;
                }

                if (person.Expected is { } expected && expected != new TurnKey(Record.Id, Record.Turns.Count))
                {
                    person.Done.TrySetResult(new SendResult.Refused(new SendProblem.StaleTarget()));
                    return;
                }

                if (person.Task is { } task)
                {
                    lock (_owner._gate)
                    {
                        if (_owner.SendTarget(task, person.Expected) is { } target)
                        {
                            person.Done.TrySetResult(new SendResult.Refused(target));
                            return;
                        }

                        _conversation = (_owner.Resolve(task.Id) ?? task).Conversation;
                    }

                    if (Record.Stopping)
                    {
                        person.Done.TrySetResult(new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle)));
                        return;
                    }
                }

                var accepted = person.Event is AttemptEvent.MessageQueued message
                    ? message with { StopsTurn = message.StopsTurn && _turn.ClientRuns }
                    : person.Event;
                Append(accepted);
                if (accepted is AttemptEvent.CancelRequested or AttemptEvent.InterruptRequested)
                {
                    lock (_gate)
                    {
                        _closed = true;
                    }
                }

                switch (accepted)
                {
                    case AttemptEvent.CancelRequested:
                        StopTurn(RequestCloseReason.Cancelled);
                        break;
                    case AttemptEvent.InterruptRequested:
                        StopTurn(RequestCloseReason.Interrupted);
                        break;
                    case AttemptEvent.MessageQueued { StopsTurn: true }:
                        StopTurn(RequestCloseReason.Stopped);
                        break;
                }

                Publish();
                person.Done.TrySetResult(accepted is AttemptEvent.GuidanceAdded ? new SendResult.Guided() : new SendResult.Queued());
            }
            catch
            {
                person.Done.TrySetResult(new SendResult.Refused(new SendProblem.CannotStart(new StartProblem.CannotRecord("iDevelop could not write this attempt's log."))));
                throw;
            }
        }

        private void Output(ProtocolOutput output)
        {
            foreach (var e in output.Events)
            {
                var at = _owner.TimeProvider.GetUtcNow();
                switch (e)
                {
                    case AgentEvent.MessageDelta delta:
                        lock (_gate)
                        {
                            var buffer = _buffers.GetValueOrDefault(delta.MessageId) ?? new LiveMessageBuffer("", _log.LineCount, at) { PresentationSequence = ++_presentationSequence };
                            var next = buffer with { Text = buffer.Text + delta.Text };
                            if (_buffers.GetValueOrDefault(delta.MessageId) != next)
                            {
                                _buffers = _buffers.SetItem(delta.MessageId, next);
                                _revision++;
                            }
                        }

                        break;
                    case AgentEvent.Message { Id: { } id } message:
                        LiveMessageBuffer? prior;
                        lock (_gate)
                        {
                            prior = _buffers.GetValueOrDefault(id);
                        }

                        if (!message.Partial)
                        {
                            Append(new AttemptEvent.Agent(at, message) { Order = prior?.Order, PresentationSequence = prior?.PresentationSequence });
                        }

                        lock (_gate)
                        {
                            if (message.Partial)
                            {
                                var next = (prior ?? new LiveMessageBuffer("", _log.LineCount, at) { PresentationSequence = ++_presentationSequence }) with { Text = message.Text };
                                if (prior != next)
                                {
                                    _buffers = _buffers.SetItem(id, next);
                                    _revision++;
                                }
                            }
                            else if (prior is not null)
                            {
                                _buffers = _buffers.Remove(id);
                                _revision++;
                            }
                        }

                        break;
                    case AgentEvent.QuestionAsked question:
                        OpenQuestion(question, at);
                        break;
                    case AgentEvent.PermissionRequested permission:
                        Append(new AttemptEvent.Agent(at, permission));
                        var decline = _turn.Protocol.Decline(permission.RequestId);
                        foreach (var frame in decline.Writes)
                        {
                            Write(frame, close: false, permission.RequestId);
                        }

                        break;
                    case AgentEvent.RequestClosed closed:
                        // The decline's write completion owns a permission's delivery outcome.
                        var key = new RequestKey(new TurnKey(Record.Id, Record.Turns.Count), closed.RequestId);
                        if (Record.Requests.GetValueOrDefault(key) is RequestRecord.Permission)
                        {
                            break;
                        }

                        Append(new AttemptEvent.RequestClosed(at, closed.RequestId, RequestCloseReason.Resolved));
                        break;
                    default:
                        Append(new AttemptEvent.Agent(at, e));
                        break;
                }
            }

            foreach (var frame in output.Writes)
            {
                Write(frame, close: false, null, oneShotPrompt: output.IsOneShotPrompt);
            }

            if (output.CloseInput)
            {
                if (_turn.Stopping && _turn.ClientRuns)
                {
                    _turn.Process.StopTree();
                }

                CloseInput();
                if (_turn.Protocol is not OneShotProtocol)
                {
                    Deadline(Record.Verdict is AgentEvent.Succeeded ? _owner.TimeProvider.GetUtcNow() + _owner.SuccessExitTime : null);
                }
            }
        }

        private void OpenQuestion(AgentEvent.QuestionAsked question, DateTimeOffset at)
        {
            if (_turn.Stopping || _plan.Request.PolicyFor(_plan.Settings.Client).Questions == QuestionHandling.Decline)
            {
                var reason = _turn.StopReason is null or RequestCloseReason.Deferred ? RequestCloseReason.PolicyDenied : _turn.StopReason.Value;
                Append(new AttemptEvent.QuestionRecorded(at, question.RequestId, question.Questions, new QuestionState.Closed(reason, null)));
                Output(_turn.Protocol.Decline(question.RequestId));
                return;
            }

            _questionOrder.Add(question.RequestId);
            if (_owner._questions is HostQuestions.DeferImmediately)
            {
                Append(new AttemptEvent.QuestionRecorded(at, question.RequestId, question.Questions, new QuestionState.Closed(RequestCloseReason.Deferred, null)));
                Defer([question.RequestId], Fallback(question.Questions), null);
                return;
            }

            var bounded = (HostQuestions.Bounded)_owner._questions;
            var deadline = OpenQuestions().Select(request => ((QuestionState.Open)request.State).Deadline).FirstOrDefault()
                ?? new RequestDeadline(at + bounded.AnswerTime, at + bounded.AnswerTime + bounded.ShutdownTime);
            Append(new AttemptEvent.QuestionRecorded(at, question.RequestId, question.Questions, new QuestionState.Open(deadline)));
            _turn.QuestionTimer?.Dispose();
            var turn = _turn;
            turn.QuestionTimer = _owner.TimeProvider.CreateTimer(_ => _events.Writer.TryWrite(new Input.QuestionTick(turn)), null,
                Remaining(deadline.AnswerBy), Timeout.InfiniteTimeSpan);
            ExpireQuestions();
        }

        private TimeSpan Remaining(DateTimeOffset at) => TimeSpan.FromTicks(Math.Max(0, (at - _owner.TimeProvider.GetUtcNow()).Ticks));

        private RequestRecord.Question[] OpenQuestions() => [.. _questionOrder
            .Select(id => Record.Requests.GetValueOrDefault(new RequestKey(new TurnKey(Record.Id, Record.Turns.Count), id)))
            .OfType<RequestRecord.Question>().Where(request => request.State is QuestionState.Open)];

        private static string Fallback(IEnumerable<AskedQuestion> questions) => string.Join("\n\n", questions
            .Select(question => string.Join("\n", new[] { question.Text }.Concat(question.Options.Select(option => option.Label)))));

        private void ExpireQuestions()
        {
            var requests = OpenQuestions();
            if (requests.Length == 0 || _turn.Stopping || _owner.TimeProvider.GetUtcNow() < ((QuestionState.Open)requests[0].State).Deadline.AnswerBy)
            {
                return;
            }

            Defer([.. requests.Select(request => request.Key.Id)], Fallback(requests.SelectMany(request => request.Questions)),
                            ((QuestionState.Open)requests[0].State).Deadline.StopBy);
        }

        private void Defer(ImmutableArray<string> ids, string fallback, DateTimeOffset? stopBy)
        {
            Append(new AttemptEvent.RequestDeferred(_owner.TimeProvider.GetUtcNow(), ids, fallback));
            lock (_gate)
            {
                _closed = true;
            }

            StopTurn(RequestCloseReason.Deferred, stopBy);
        }

        private void AcceptAnswer(Input.Answer answer)
        {
            if (answer.Ct.IsCancellationRequested)
            {
                answer.Done.TrySetCanceled(answer.Ct);
                return;
            }

            lock (_owner._gate)
            {
                if (_owner._leaving is not null)
                {
                    answer.Done.TrySetResult(new AnswerResult(AnswerOutcome.Unavailable, "The project is closed."));
                    return;
                }
            }

            ExpireQuestions();
            if (answer.Key.Turn != new TurnKey(Record.Id, Record.Turns.Count)
                || Record.Requests.GetValueOrDefault(answer.Key) is not RequestRecord.Question question)
            {
                answer.Done.TrySetResult(new AnswerResult(AnswerOutcome.Stale, "The question is no longer current."));
                return;
            }

            var recorded = question.State switch
            {
                QuestionState.AnswerRecorded state => state.Reply,
                QuestionState.Closed state => state.RecordedReply,
                QuestionState.Open => null,
                _ => throw new InvalidOperationException("Unknown question state."),
            };
            if (recorded is not null && EqualReply(recorded, answer.Reply))
            {
                answer.Done.TrySetResult(new AnswerResult(AnswerOutcome.AlreadyRecorded, "This answer was already recorded."));
                return;
            }

            if (question.State is not QuestionState.Open || _turn.Stopping || !_turn.ClientRuns)
            {
                answer.Done.TrySetResult(new AnswerResult(AnswerOutcome.Stale, "The question is closed."));
                return;
            }

            if (!ValidReply(question, answer.Reply))
            {
                answer.Done.TrySetResult(new AnswerResult(AnswerOutcome.Invalid, "The answer must match the question, options, and selection rules."));
                return;
            }

            try
            {
                Append(new AttemptEvent.RequestAnswered(_owner.TimeProvider.GetUtcNow(), answer.Key.Id, answer.Reply));
                _deliveries.Add(answer);
                var output = _turn.Protocol.Answer(answer.Key.Id, answer.Reply);
                foreach (var frame in output.Writes)
                {
                    Write(frame, false, answer.Key.Id, answer);
                }
            }
            catch
            {
                answer.Done.TrySetResult(new AnswerResult(AnswerOutcome.DeliveryUnknown, "Answer delivery could not be confirmed."));
                throw;
            }
        }

        private static bool EqualReply(QuestionsReply left, QuestionsReply right) => !right.Answers.IsDefault && left.Answers.Length == right.Answers.Length
            && left.Answers.Zip(right.Answers).All(pair => pair.First.QuestionId == pair.Second.QuestionId
                && pair.First.Text == pair.Second.Text && !pair.Second.OptionIds.IsDefault && pair.First.OptionIds.SequenceEqual(pair.Second.OptionIds));

        private static bool ValidReply(RequestRecord.Question request, QuestionsReply reply) => !reply.Answers.IsDefault
            && reply.Answers.Length == request.Questions.Length && reply.Answers.Select(answer => answer.QuestionId).Distinct().Count() == reply.Answers.Length
            && reply.Answers.All(answer => request.Questions.FirstOrDefault(question => question.Id == answer.QuestionId) is { } question
                && !answer.OptionIds.IsDefault && answer.OptionIds.Distinct().Count() == answer.OptionIds.Length
                && (question.MultiSelect || answer.OptionIds.Length <= 1)
                && answer.OptionIds.All(id => question.Options.Any(option => option.Id == id))
                && (question.AllowsOther || string.IsNullOrEmpty(answer.Text)));

        private bool BeginStopping(RequestCloseReason reason)
        {
            var stopping = _turn.Stopping;
            _turn.StopReason = Record.InterruptReason is not null ? RequestCloseReason.Interrupted
                : Record.CancelRequested ? RequestCloseReason.Cancelled : _turn.StopReason ?? reason;
            _turn.QuestionTimer?.Dispose();
            foreach (var question in OpenQuestions())
            {
                Append(new AttemptEvent.RequestClosed(_owner.TimeProvider.GetUtcNow(), question.Key.Id, _turn.StopReason.Value));
            }

            return !stopping;
        }

        private void StopTurn(RequestCloseReason reason, DateTimeOffset? stopBy = null)
        {
            if (!BeginStopping(reason) || !_turn.ClientRuns)
            {
                return;
            }

            _turn.PromptCancellation.Cancel();
            Deadline(stopBy);
            if (_turn.Protocol.Interrupt() is { } interrupt)
            {
                Output(interrupt);
            }
            else
            {
                _turn.Process.StopTree();
                CloseInput();
            }
        }

        private void Fail(string detail)
        {
            BeginStopping(RequestCloseReason.TurnEnded);
            _turn.Failure ??= detail;
            Append(new AttemptEvent.Agent(_owner.TimeProvider.GetUtcNow(), new AgentEvent.Failed(detail)));
            _turn.Process.StopTree();
            CloseInput();
        }

        private void Deadline(DateTimeOffset? stopBy = null)
        {
            var deadline = stopBy ?? _owner.TimeProvider.GetUtcNow() + _owner.ShutdownTime;
            if (_turn.StopBy is { } current && current <= deadline)
            {
                return;
            }

            _turn.StopTimer?.Dispose();
            _turn.StopBy = deadline;
            var turn = _turn;
            turn.StopTimer = _owner.TimeProvider.CreateTimer(_ => _events.Writer.TryWrite(new Input.Deadline(turn)), null,
                Remaining(_turn.StopBy.Value), Timeout.InfiniteTimeSpan);
        }

        private void CloseInput()
        {
            if (_turn.InputClosed)
            {
                return;
            }

            _turn.InputClosed = true;
            Write("", close: true, null);
        }

        private void Write(string frame, bool close, string? requestId, Input.Answer? answer = null, bool oneShotPrompt = false)
        {
            _turn.PendingWrites++;
            _turn.Writes.Writer.TryWrite(new WriteWork(frame, close, requestId, answer, oneShotPrompt));
        }

        private void Read(Turn turn)
        {
            var stderr = new Tail();
            turn.Process.ReadStdout(line =>
            {
                lock (_gate)
                {
                    if (turn.Open)
                    {
                        _events.Writer.TryWrite(new Input.Line(turn, line));
                    }
                }
            });
            turn.Process.ReadStderr(line =>
            {
                lock (_gate)
                {
                    if (turn.Open)
                    {
                        _log.AppendStderr(line);
                        stderr.Add(line);
                    }
                }
            });
            turn.Writer = Task.Run(async () =>
            {
                await foreach (var work in turn.Writes.Reader.ReadAllAsync())
                {
                    var text = work.Frame.Length > 0 && turn.Protocol is not OneShotProtocol ? work.Frame + "\n" : work.Frame;
                    using var promptLifetime = work.OneShotPrompt
                        ? CancellationTokenSource.CreateLinkedTokenSource(turn.Lifetime.Token, turn.PromptCancellation.Token) : null;
                    var success = await turn.Process.WriteInputAsync(text, work.Close,
                        work.OneShotPrompt ? Timeout.InfiniteTimeSpan : _owner.ShutdownTime, promptLifetime?.Token ?? turn.Lifetime.Token);
                    _events.Writer.TryWrite(new Input.WriteDone(turn, work.RequestId, success, work.Answer));
                }
            });
            _ = WatchExitAsync(turn, stderr);
        }

        private async Task WatchExitAsync(Turn turn, Tail stderr)
        {
            try
            {
                var code = await turn.Process.WaitForExitAsync();
                turn.PromptCancellation.Cancel();
                var remaining = turn.Stopping && turn.StopBy is { } stopBy ? TimeSpan.FromTicks(Math.Max(0, (stopBy - _owner.TimeProvider.GetUtcNow()).Ticks)) : (TimeSpan?)null;
                await turn.Process.WaitForOutputAsync(remaining);
                lock (_gate)
                {
                    turn.Open = false;
                    _closed |= !_messageWaiting;
                    _events.Writer.TryWrite(new Input.Exit(turn, code, stderr.Text));
                }
            }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
        }

        private async Task<bool> NextTurnAsync()
        {
            _turn.Process.LeaveDescendantsRunning();
            await DisposeTurnAsync();
            var tree = GitTree.Snapshot(_owner._projectFolder);
            var started = false;
            try
            {
                while (_events.Reader.TryRead(out var input))
                {
                    if (input is Input.Person person)
                    {
                        Accept(person);
                    }
                    else if (input is Input.Answer answer)
                    {
                        AcceptAnswer(answer);
                    }
                }

                started = Record is { Status: AttemptStatus.Running, SessionId: { } session } && Launch(session, tree);
                return started;
            }
            finally
            {
                lock (_gate)
                {
                    _closed |= !started;
                }
            }
        }

        private bool Launch(string session, string? tree)
        {
            var plan = _plan.Resuming(session, string.Join("\n\n", Record.Queued.Select(message => message.Text)));
            if (_conversation is { } mode)
            {
                plan = plan with
                {
                    Request = plan.Request with
                    {
                        Policy = ClientPolicy.For(plan.Settings.Client, plan.Request.ReadOnly, mode, Record.Subject is not null, _owner._questions)
                    }
                };
            }

            Append(new AttemptEvent.TurnRequested(_owner.TimeProvider.GetUtcNow(), plan.Request.Prompt, plan.Command.Path, plan.Launch.Arguments)
            {
                Conversation = _conversation,
                Tree = tree,
                Consumed = [.. Record.Queued.Select(message => message.Id)],
            });
            var (record, process) = _owner.LaunchTurn(plan, Record, _log);
            Record = record;
            _messageWaiting = false;
            if (process is null)
            {
                return false;
            }

            _plan = plan;
            _turn = new Turn(process, plan.Client.Protocol(plan.Request));
            Read(_turn);
            Output(_turn.Protocol.Start());
            return true;
        }

        private async Task DisposeTurnAsync()
        {
            if (_turn.Disposed)
            {
                return;
            }

            _turn.Disposed = true;
            _turn.QuestionTimer?.Dispose();
            _turn.StopTimer?.Dispose();
            _turn.Lifetime.Cancel();
            _turn.Writes.Writer.TryComplete();
            await _turn.Writer;
            _turn.Lifetime.Dispose();
            _turn.PromptCancellation.Dispose();
            _turn.Process.Dispose();
        }

        private void Publish()
        {
            var live = Live;
            if (ReferenceEquals(Record, _publishedRecord) && live.Revision == _publishedRevision)
            {
                return;
            }

            _publishedRecord = Record;
            _publishedRevision = live.Revision;
            _owner.Publish(Record, live.LogRevision);
        }

        private void Append(AttemptEvent e)
        {
            lock (_gate)
            {
                _log.Append(e);
                Record = AttemptReducer.Apply(Record, e);
                _revision++;
            }
        }

        private abstract record Input
        {
            private Input() { }

            public sealed record Line(Turn Turn, string Text) : Input;

            public sealed record Person(AttemptEvent Event, TaskCompletionSource<SendResult> Done, TaskDefinition? Task, TurnKey? Expected, CancellationToken Ct) : Input;

            public sealed record Exit(Turn Turn, int Code, string Stderr) : Input;

            public sealed record Answer(RequestKey Key, QuestionsReply Reply, TaskCompletionSource<AnswerResult> Done, CancellationToken Ct) : Input;

            public sealed record QuestionTick(Turn Turn) : Input;

            public sealed record WriteDone(Turn Turn, string? RequestId, bool Success, Answer? Delivery) : Input;

            public sealed record Deadline(Turn Turn) : Input;
        }

        private sealed record WriteWork(string Frame, bool Close, string? RequestId, Input.Answer? Answer, bool OneShotPrompt);

        private sealed class Turn(ChildProcess process, TurnProtocol protocol)
        {
            public ChildProcess Process { get; } = process;

            public TurnProtocol Protocol { get; } = protocol;

            public CancellationTokenSource Lifetime { get; } = new();

            public CancellationTokenSource PromptCancellation { get; } = new();

            public Channel<WriteWork> Writes { get; } = Channel.CreateUnbounded<WriteWork>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            public Task Writer { get; set; } = Task.CompletedTask;

            public bool Open { get; set; } = true;

            public RequestCloseReason? StopReason { get; set; }

            public bool Stopping => StopReason is not null;

            public string? Failure { get; set; }

            public bool InputClosed { get; set; }

            public bool Disposed { get; set; }

            public int PendingWrites { get; set; }

            public Input.Exit? Exit { get; set; }

            public DateTimeOffset? StopBy { get; set; }

            public ITimer? StopTimer { get; set; }

            public ITimer? QuestionTimer { get; set; }

            public bool ClientRuns => Open && !Process.HasExited;
        }
    }

    /// <summary>The last few kilobytes of the client's stderr, for the exit event.</summary>
    private sealed class Tail
    {
        private const int Limit = 4096;

        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public void Add(string line)
        {
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > Limit)
                {
                    _text.Remove(0, _text.Length - Limit);
                }
            }
        }
    }
}
