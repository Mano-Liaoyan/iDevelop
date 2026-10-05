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
        private readonly Channel<AttemptEvent> _events = Channel.CreateUnbounded<AttemptEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _abandon = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private LaunchPlan _plan;
        private Turn _turn;
        private bool _messageWaiting;
        private ConversationMode? _conversation;
        private bool _closed;

        /// <param name="order">Counts this window's launches, so <see cref="Active"/> keeps the order the runs started in.</param>
        public ActiveRun(ProjectRuns owner, long order, LaunchPlan plan, ChildProcess process, AttemptLog log, RunLock held, AttemptRecord record)
        {
            _owner = owner;
            Order = order;
            _plan = plan;
            _turn = new Turn(process);
            _log = log;
            _held = held;
            Record = record;
        }

        public long Order { get; }

        public AttemptRecord Record { get; private set; }

        /// <summary>Completes once the lock is released.</summary>
        public Task Completion => _finished.Task;

        public void Start() => _ = Task.Run(RunAsync);

        /// <summary>Cancel or leave: no message is accepted after it, and the turn's process tree stops while its client runs.</summary>
        public void Stop(AttemptEvent request)
        {
            lock (_gate)
            {
                _closed = true;
                _events.Writer.TryWrite(request);
                if (_turn.ClientRuns)
                {
                    _turn.Process.StopTree();
                }
            }
        }

        /// <param name="conversation">The task's mode now, which the next turn takes.</param>
        public SendResult Send(string text, bool stopTurn, ConversationMode conversation)
        {
            lock (_gate)
            {
                if (Problem() is { } problem)
                {
                    return new SendResult.Refused(problem);
                }

                _conversation = conversation;
                // Once the client exited, the turn keeps its own outcome.
                var stops = stopTurn && _turn.ClientRuns;
                _events.Writer.TryWrite(new AttemptEvent.MessageQueued(DateTimeOffset.UtcNow, text, stops));
                _messageWaiting = true;
                if (stops)
                {
                    _turn.Process.StopTree();
                }

                return new SendResult.Queued();
            }
        }

        public SendProblem? SendProblem()
        {
            lock (_gate)
            {
                return Problem();
            }
        }

        /// <summary>Stops the drain at its next event, without waiting for the client's exit. The attempt stays on record as running.</summary>
        public void Abandon() => _abandon.Cancel();

        /// <summary>Called under the gate.</summary>
        private SendProblem? Problem() => (_closed, Record.SessionId) switch
        {
            (true, _) => new SendProblem.Ending(Record.TaskTitle),
            (_, null) => new SendProblem.NoSessionYet(Record.Requested.Client),
            _ => null,
        };

        private async Task RunAsync()
        {
            try
            {
                Read(_turn, _plan);
                await DrainAsync();
            }
            finally
            {
                _finished.TrySetResult();
            }
        }

        private void Read(Turn turn, LaunchPlan plan)
        {
            var stderrTail = new Tail();
            _ = turn.Process.WriteStdinAsync(plan.Launch.Stdin, close: true);
            turn.Process.ReadStdout(line => Interpret(turn, plan, line));
            turn.Process.ReadStderr(line =>
            {
                lock (_gate)
                {
                    if (turn.Open)
                    {
                        _log.AppendStderr(line);
                        stderrTail.Add(line);
                    }
                }
            });
            _ = WatchExitAsync(turn, stderrTail);
        }

        private async Task DrainAsync()
        {
            var exited = false;
            try
            {
                await foreach (var e in _events.Reader.ReadAllAsync(_abandon.Token))
                {
                    // Leaving gave up while this drain was busy. Events already queued stay off the log too.
                    _abandon.Token.ThrowIfCancellationRequested();
                    Append(e);
                    exited |= e is AttemptEvent.Exited;
                    _owner.Publish(Record);
                    if (Record.Status != AttemptStatus.Running)
                    {
                        break;
                    }

                    if (e is AttemptEvent.Exited)
                    {
                        if (!NextTurn())
                        {
                            break;
                        }

                        exited = false;
                        _owner.Publish(Record);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Record = AttemptReducer.Abandon(Record, CannotWriteLog(e), DateTimeOffset.UtcNow);
            }
            finally
            {
                lock (_gate)
                {
                    _closed = true;
                    _turn.Open = false;
                    _events.Writer.TryComplete();
                }

                // Whatever ended the drain early, including a Changed handler that threw, no client runs on unobserved.
                // After its exit, whatever the client started for the user, such as a dev server, keeps running.
                if (exited)
                {
                    _turn.Process.LeaveDescendantsRunning();
                }
                else
                {
                    _turn.Process.StopTree();
                }

                _turn.Process.Dispose();
                _log.Dispose();
                _held.Dispose();
                _owner.Finish(this);
            }
        }

        /// <summary>
        /// After a turn ended with a message waiting, starts the next turn with every waiting message. Every event still
        /// queued came from the person, because the ended turn writes nothing more, so they are folded first. False when
        /// the attempt ended instead: a cancel or leave came first, or the client did not start. Unless the next turn
        /// started, the run closes to messages before the gate opens, because no turn is left to take them.
        /// </summary>
        private bool NextTurn()
        {
            _turn.Process.LeaveDescendantsRunning();
            _turn.Process.Dispose();
            lock (_gate)
            {
                var started = false;
                try
                {
                    while (Record.Status == AttemptStatus.Running && _events.Reader.TryRead(out var e))
                    {
                        Append(e);
                    }

                    started = Record is { Status: AttemptStatus.Running, SessionId: { } session } && Launch(session);
                    return started;
                }
                finally
                {
                    _closed |= !started;
                }
            }
        }

        /// <summary>Called under the gate. False when the client did not start, which ended the attempt.</summary>
        private bool Launch(string session)
        {
            var plan = _plan.Resuming(session, string.Join("\n\n", Record.Queued));
            Append(new AttemptEvent.TurnRequested(DateTimeOffset.UtcNow, plan.Request.Prompt, plan.Command.Path, plan.Launch.Arguments)
            {
                Conversation = _conversation,
            });
            var (record, process) = _owner.LaunchTurn(plan, Record, _log);
            Record = record;
            _messageWaiting = false;
            if (process is null)
            {
                return false;
            }

            _plan = plan;
            _turn = new Turn(process);
            Read(_turn, plan);
            return true;
        }

        private void Append(AttemptEvent e)
        {
            _log.Append(e);
            Record = AttemptReducer.Apply(Record, e);
        }

        private void Interpret(Turn turn, LaunchPlan plan, string line)
        {
            var events = string.IsNullOrWhiteSpace(line) ? [] : Parse(plan, line);
            lock (_gate)
            {
                if (!turn.Open)
                {
                    return;
                }

                _log.AppendOutput(line);
                foreach (var e in events)
                {
                    _events.Writer.TryWrite(new AttemptEvent.Agent(DateTimeOffset.UtcNow, e));
                }
            }
        }

        private static ImmutableArray<AgentEvent> Parse(LaunchPlan plan, string line)
        {
            try
            {
                return plan.Client.Interpret(line);
            }
            catch (JsonException)
            {
                return [new AgentEvent.Notice($"iDevelop could not read a line that {Clients.Name(plan.Settings.Client)} printed.")];
            }
        }

        private async Task WatchExitAsync(Turn turn, Tail stderrTail)
        {
            var exitQueued = false;
            try
            {
                var code = await turn.Process.WaitForExitAsync();
                // The turn ends without output that comes later.
                await turn.Process.WaitForOutputAsync();
                lock (_gate)
                {
                    turn.Open = false;
                    _closed |= !_messageWaiting;
                    exitQueued = _events.Writer.TryWrite(new AttemptEvent.Exited(DateTimeOffset.UtcNow, code, stderrTail.Text));
                }
            }
            finally
            {
                // Without an exit the drain would wait forever, so it ends, and its end stops the client.
                if (!exitQueued)
                {
                    _events.Writer.TryComplete();
                }
            }
        }

        /// <summary>One client process. <see cref="Open"/> turns false under the run's gate when the process exits.</summary>
        private sealed class Turn(ChildProcess process)
        {
            public ChildProcess Process { get; } = process;

            public bool Open { get; set; } = true;

            /// <summary>
            /// Whether a stop should end the turn's process tree. <see cref="Open"/> stays true for up to 5 seconds after
            /// the client exits, while a process it left running, such as a dev server, holds its output. That process
            /// is the person's, as after a command in a terminal, so nothing stops it.
            /// </summary>
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
