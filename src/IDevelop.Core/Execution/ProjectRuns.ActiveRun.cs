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
    /// one channel. The channel puts every event in one order: stdout lines, the person's messages, stop requests, and
    /// each turn's exit. One drain appends each event to the log, folds it, and publishes the record, in that order. A stop
    /// request enqueued before the kill therefore always precedes the exit the kill causes. A turn's output ends at its
    /// exit, so nothing a turn left running writes into the next one.
    /// A message is accepted only while a turn can still take it. A cancel or leave, the exit of a turn with no message
    /// waiting, and a next turn that does not start each close the run to messages under the gate that accepts them, so
    /// every accepted message precedes that point or reaches the next turn. Whatever else ends the drain closes the run
    /// first.
    /// The lock is released only after the last event is on disk, or after the drain stops and the log is closed, so
    /// nothing appends after another instance could take over. The run disposes each turn's process before it starts the
    /// next turn or releases the lock, so a late stop does nothing. While a turn's client runs, Cancel, Stop and send, and
    /// leaving stop everything it started, and so does a crash on Windows. What the client leaves running when it exits on
    /// its own keeps running, as after a command in a terminal, even while it still holds the turn's output.
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
        private LaunchPlan _plan;
        private Turn _turn;
        private bool _messageWaiting;
        private ConversationMode? _conversation;
        private bool _closed;

        public ActiveRun(ProjectRuns owner, long order, LaunchPlan plan, ChildProcess process, AttemptLog log, RunLock held, AttemptRecord record)
        {
            _owner = owner;
            Order = order;
            _plan = plan;
            _turn = new Turn(process, plan.Client.Protocol(plan.Request));
            _log = log;
            _held = held;
            Record = record;
            _revision = log.LineCount;
        }

        public long Order { get; }
        public AttemptRecord Record { get; private set; }
        public Task Completion => _finished.Task;
        public (long Revision, long LogRevision, ImmutableDictionary<string, LiveMessageBuffer> Buffers) Live
        {
            get { lock (_gate) return (_revision, _log.LineCount, _buffers); }
        }

        public void Start() => _ = Task.Run(RunAsync);

        public Task StopAsync(AttemptEvent request)
        {
            lock (_gate)
            {
                if (_closed && ! _turn.Open) return Task.CompletedTask;
                _closed = true;
                var done = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _events.Writer.TryWrite(new Input.Person(request, done, null)) ? done.Task : Task.CompletedTask;
            }
        }

        public SendResult Send(string text, bool stopTurn, ConversationMode conversation)
        {
            Task<SendResult> accepted;
            lock (_gate)
            {
                if (Problem() is { } problem) return new SendResult.Refused(problem);
                var done = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var message = new AttemptEvent.MessageQueued(DateTimeOffset.UtcNow, text, stopTurn) { Id = Guid.CreateVersion7().ToString() };
                if (!_events.Writer.TryWrite(new Input.Person(message, done, conversation))) return new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle));
                _messageWaiting = true;
                accepted = done.Task;
            }
            return accepted.GetAwaiter().GetResult();
        }

        public SendResult Guide(string text)
        {
            Task<SendResult> accepted;
            lock (_gate)
            {
                if (_closed) return new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle));
                var done = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_events.Writer.TryWrite(new Input.Person(new AttemptEvent.GuidanceAdded(DateTimeOffset.UtcNow, text), done, null)))
                    return new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle));
                accepted = done.Task;
            }
            return accepted.GetAwaiter().GetResult();
        }

        public SendProblem? GuideProblem() { lock (_gate) return _closed ? new SendProblem.Ending(Record.TaskTitle) : null; }
        public SendProblem? SendProblem() { lock (_gate) return Problem(); }
        public void Abandon() => _abandon.Cancel();
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
                                try { Output(_turn.Protocol.Read(line.Text)); }
                                catch (JsonException) { Append(new AttemptEvent.Agent(DateTimeOffset.UtcNow, new AgentEvent.Notice($"iDevelop could not read a line that {Clients.Name(_plan.Settings.Client)} printed."))); }
                                catch (ProtocolException error) { Fail(error.Message); }
                            }
                            break;
                        case Input.Person person:
                            Accept(person);
                            break;
                        case Input.WriteDone write when write.Turn == _turn:
                            _turn.PendingWrites--;
                            if (write.RequestId is { } request)
                            {
                                Append(new AttemptEvent.RequestClosed(DateTimeOffset.UtcNow, request, write.Success ? RequestCloseReason.PolicyDenied : RequestCloseReason.DeliveryUnknown));
                            }
                            if (!write.Success && _turn.ClientRuns) Fail("iDevelop could not write to the client's input pipe.");
                            if (_turn.PendingWrites == 0 && _turn.Exit is { } heldExit) _events.Writer.TryWrite(heldExit);
                            break;
                        case Input.Deadline deadline when deadline.Turn == _turn && !_turn.Process.HasExited:
                            if (!_turn.Stopping && Record.Verdict is AgentEvent.Succeeded) Append(new AttemptEvent.ShutdownForced(DateTimeOffset.UtcNow));
                            _turn.Process.StopTree();
                            CloseInput();
                            break;
                        case Input.Exit exit when exit.Turn == _turn:
                            if (_turn.PendingWrites > 0) { _turn.Exit = exit; break; }
                            _turn.Exit = null;
                            foreach (var (id, buffer) in _buffers.OrderBy(pair => pair.Value.Order))
                                Append(new AttemptEvent.Agent(DateTimeOffset.UtcNow, new AgentEvent.Message(buffer.Text) { Id = id, Partial = true }) { Order = buffer.Order });
                            lock (_gate) _buffers = ImmutableDictionary<string, LiveMessageBuffer>.Empty;
                            if (_turn.Failure is { } failure) Append(new AttemptEvent.Agent(DateTimeOffset.UtcNow, new AgentEvent.Failed(failure)));
                            Append(new AttemptEvent.Exited(DateTimeOffset.UtcNow, exit.Code, exit.Stderr) { Tree = GitTree.Snapshot(_owner._projectFolder) });
                            exited = true;
                            _owner.Publish(Record);
                            if (Record.Status != AttemptStatus.Running || !NextTurn()) return;
                            exited = false;
                            break;
                        case Input.Line or Input.WriteDone or Input.Deadline or Input.Exit:
                            break;
                        default:
                            throw new InvalidOperationException("Unknown run input.");
                    }
                    _owner.Publish(Record);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Record = AttemptReducer.Abandon(Record, CannotWriteLog(error), DateTimeOffset.UtcNow);
            }
            finally
            {
                lock (_gate)
                {
                    _closed = true;
                    _turn.Open = false;
                    _events.Writer.TryComplete();
                    while (_events.Reader.TryRead(out var pending))
                        if (pending is Input.Person person) person.Done.TrySetResult(new SendResult.Refused(new SendProblem.Ending(Record.TaskTitle)));
                }
                if (exited) _turn.Process.LeaveDescendantsRunning();
                else _turn.Process.StopTree();
                await DisposeTurnAsync();
                _log.Dispose();
                _held.Dispose();
                try { _owner.Finish(this); }
                finally { _finished.TrySetResult(); }
            }
        }

        private void Accept(Input.Person person)
        {
            try
            {
                var accepted = person.Event is AttemptEvent.MessageQueued message
                    ? message with { StopsTurn = message.StopsTurn && _turn.ClientRuns }
                    : person.Event;
                Append(accepted);
                if (person.Conversation is { } conversation) _conversation = conversation;
                person.Done.TrySetResult(accepted is AttemptEvent.GuidanceAdded ? new SendResult.Guided() : new SendResult.Queued());
                if (accepted is AttemptEvent.CancelRequested or AttemptEvent.InterruptRequested or AttemptEvent.MessageQueued { StopsTurn: true }) StopTurn();
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
                var at = DateTimeOffset.UtcNow;
                switch (e)
                {
                    case AgentEvent.MessageDelta delta:
                        lock (_gate)
                        {
                            var buffer = _buffers.GetValueOrDefault(delta.MessageId) ?? new LiveMessageBuffer("", _log.LineCount, at);
                            _buffers = _buffers.SetItem(delta.MessageId, buffer with { Text = buffer.Text + delta.Text });
                            _revision++;
                        }
                        break;
                    case AgentEvent.Message { Id: { } id } message:
                        LiveMessageBuffer? prior;
                        lock (_gate) prior = _buffers.GetValueOrDefault(id);
                        if (!message.Partial) Append(new AttemptEvent.Agent(at, message) { Order = prior?.Order });
                        lock (_gate)
                        {
                            _buffers = message.Partial
                                ? _buffers.SetItem(id, (prior ?? new LiveMessageBuffer("", _log.LineCount, at)) with { Text = message.Text })
                                : _buffers.Remove(id);
                            _revision++;
                        }
                        break;
                    case AgentEvent.QuestionAsked question:
                        Append(new AttemptEvent.QuestionRecorded(at, question.RequestId, question.Questions, new QuestionState.Closed(RequestCloseReason.PolicyDenied, null)));
                        Output(_turn.Protocol.Decline(question.RequestId));
                        break;
                    case AgentEvent.PermissionRequested permission:
                        Append(new AttemptEvent.Agent(at, permission));
                        var decline = _turn.Protocol.Decline(permission.RequestId);
                        foreach (var frame in decline.Writes) Write(frame, close: false, permission.RequestId);
                        break;
                    case AgentEvent.RequestClosed closed:
                        // The decline's write completion owns a permission's delivery outcome.
                        var key = new RequestKey(new TurnKey(Record.Id, Record.Turns.Count), closed.RequestId);
                        if (Record.Requests.GetValueOrDefault(key) is RequestRecord.Permission) break;
                        Append(new AttemptEvent.RequestClosed(at, closed.RequestId, RequestCloseReason.Resolved));
                        break;
                    default:
                        Append(new AttemptEvent.Agent(at, e));
                        break;
                }
            }
            foreach (var frame in output.Writes) Write(frame, close: false, null);
            if (output.CloseInput)
            {
                if (_turn.Stopping && _turn.ClientRuns) _turn.Process.StopTree();
                CloseInput();
                if (_turn.Protocol is not OneShotProtocol) Deadline();
            }
        }

        private void StopTurn()
        {
            if (!_turn.ClientRuns || _turn.Stopping) return;
            _turn.Stopping = true;
            Deadline();
            if (_turn.Protocol.Interrupt() is { } interrupt) Output(interrupt);
            else { _turn.Process.StopTree(); CloseInput(); }
        }

        private void Fail(string detail)
        {
            _turn.Failure ??= detail;
            Append(new AttemptEvent.Agent(DateTimeOffset.UtcNow, new AgentEvent.Failed(detail)));
            _turn.Process.StopTree();
            CloseInput();
        }

        private void Deadline()
        {
            if (_turn.StopBy is not null) return;
            _turn.StopBy = DateTimeOffset.UtcNow + _owner.ShutdownTime;
            var turn = _turn;
            var lifetime = turn.Lifetime.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_owner.ShutdownTime, lifetime);
                    _events.Writer.TryWrite(new Input.Deadline(turn));
                }
                catch (OperationCanceledException) { }
            });
        }

        private void CloseInput()
        {
            if (_turn.InputClosed) return;
            _turn.InputClosed = true;
            Write("", close: true, null);
        }

        private void Write(string frame, bool close, string? requestId)
        {
            _turn.PendingWrites++;
            _turn.Writes.Writer.TryWrite(new WriteWork(frame, close, requestId));
        }

        private void Read(Turn turn)
        {
            var stderr = new Tail();
            turn.Process.ReadStdout(line =>
            {
                lock (_gate) { if (turn.Open) _events.Writer.TryWrite(new Input.Line(turn, line)); }
            });
            turn.Process.ReadStderr(line =>
            {
                lock (_gate) { if (turn.Open) { _log.AppendStderr(line); stderr.Add(line); } }
            });
            turn.Writer = Task.Run(async () =>
            {
                await foreach (var work in turn.Writes.Reader.ReadAllAsync())
                {
                    var text = work.Frame.Length > 0 && turn.Protocol is not OneShotProtocol ? work.Frame + "\n" : work.Frame;
                    var success = await turn.Process.WriteInputAsync(text, work.Close, _owner.ShutdownTime, turn.Lifetime.Token);
                    _events.Writer.TryWrite(new Input.WriteDone(turn, work.RequestId, success));
                }
            });
            _ = WatchExitAsync(turn, stderr);
        }

        private async Task WatchExitAsync(Turn turn, Tail stderr)
        {
            try
            {
                var code = await turn.Process.WaitForExitAsync();
                var remaining = turn.StopBy is { } stopBy ? TimeSpan.FromTicks(Math.Max(0, (stopBy - DateTimeOffset.UtcNow).Ticks)) : (TimeSpan?)null;
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

        private bool NextTurn()
        {
            _turn.Process.LeaveDescendantsRunning();
            DisposeTurnAsync().GetAwaiter().GetResult();
            var tree = GitTree.Snapshot(_owner._projectFolder);
            lock (_gate)
            {
                var started = false;
                try
                {
                    while (_events.Reader.TryRead(out var input))
                        if (input is Input.Person person) Accept(person);
                    started = Record is { Status: AttemptStatus.Running, SessionId: { } session } && Launch(session, tree);
                    return started;
                }
                finally { _closed |= !started; }
            }
        }

        private bool Launch(string session, string? tree)
        {
            var plan = _plan.Resuming(session, string.Join("\n\n", Record.Queued.Select(message => message.Text)));
            if (_conversation is { } mode) plan = plan with { Request = plan.Request with { Policy = ClientPolicy.For(plan.Settings.Client, plan.Request.ReadOnly, mode, Record.Subject is not null, new HostQuestions.Disabled()) } };
            Append(new AttemptEvent.TurnRequested(DateTimeOffset.UtcNow, plan.Request.Prompt, plan.Command.Path, plan.Launch.Arguments)
            {
                Conversation = _conversation, Tree = tree, Consumed = [.. Record.Queued.Select(message => message.Id)],
            });
            var (record, process) = _owner.LaunchTurn(plan, Record, _log);
            Record = record;
            _messageWaiting = false;
            if (process is null) return false;
            _plan = plan;
            _turn = new Turn(process, plan.Client.Protocol(plan.Request));
            Read(_turn);
            Output(_turn.Protocol.Start());
            return true;
        }

        private async Task DisposeTurnAsync()
        {
            if (_turn.Disposed) return;
            _turn.Disposed = true;
            _turn.Lifetime.Cancel();
            _turn.Writes.Writer.TryComplete();
            await _turn.Writer;
            _turn.Lifetime.Dispose();
            _turn.Process.Dispose();
        }

        private void Append(AttemptEvent e)
        {
            _log.Append(e);
            lock (_gate)
            {
                Record = AttemptReducer.Apply(Record, e);
                _revision++;
            }
        }

        private abstract record Input
        {
            private Input() { }
            public sealed record Line(Turn Turn, string Text) : Input;
            public sealed record Person(AttemptEvent Event, TaskCompletionSource<SendResult> Done, ConversationMode? Conversation) : Input;
            public sealed record Exit(Turn Turn, int Code, string Stderr) : Input;
            public sealed record WriteDone(Turn Turn, string? RequestId, bool Success) : Input;
            public sealed record Deadline(Turn Turn) : Input;
        }
        private sealed record WriteWork(string Frame, bool Close, string? RequestId);
        private sealed class Turn(ChildProcess process, TurnProtocol protocol)
        {
            public ChildProcess Process { get; } = process;
            public TurnProtocol Protocol { get; } = protocol;
            public CancellationTokenSource Lifetime { get; } = new();
            public Channel<WriteWork> Writes { get; } = Channel.CreateUnbounded<WriteWork>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            public Task Writer { get; set; } = Task.CompletedTask;
            public bool Open { get; set; } = true;
            public bool Stopping { get; set; }
            public string? Failure { get; set; }
            public bool InputClosed { get; set; }
            public bool Disposed { get; set; }
            public int PendingWrites { get; set; }
            public Input.Exit? Exit { get; set; }
            public DateTimeOffset? StopBy { get; set; }
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
