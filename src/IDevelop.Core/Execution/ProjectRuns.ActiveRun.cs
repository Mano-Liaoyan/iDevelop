using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    /// <summary>
    /// One run. A channel puts every event in one order: stdout lines, stop requests, and the exit. One drain appends
    /// each event to the log, folds it, and publishes the record, in that order. A stop request enqueued before the kill
    /// therefore always precedes the exit the kill causes. The lock is released only after the last event is on disk,
    /// or after the drain stops and the log is closed, so nothing appends after another instance could take over.
    /// The run disposes its process just before it releases the lock, so a late stop does nothing. While the client runs,
    /// Cancel and leaving stop everything it started, and so does a crash on Windows. What it leaves running when it exits
    /// on its own keeps running, as after a command in a terminal.
    /// </summary>
    private sealed class ActiveRun(ProjectRuns owner, LaunchPlan plan, ChildProcess process, AttemptLog log, RunLock held, AttemptRecord record)
    {
        private readonly Channel<AttemptEvent> _events = Channel.CreateUnbounded<AttemptEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _abandon = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AttemptRecord Record { get; private set; } = record;

        /// <summary>Completes once the lock is released.</summary>
        public Task Completion => _finished.Task;

        public void Start() => _ = Task.Run(RunAsync);

        public void Stop(AttemptEvent request)
        {
            Request(request);
            process.StopTree();
        }

        /// <summary>Stops the drain at its next event, without waiting for the client's exit. The attempt stays on record as running.</summary>
        public void Abandon() => _abandon.Cancel();

        private void Request(AttemptEvent e) => _events.Writer.TryWrite(e);

        private async Task RunAsync()
        {
            try
            {
                var stderrTail = new Tail();
                _ = process.WriteStdinAsync(plan.Launch.Stdin, close: true);
                process.ReadStdout(Interpret);
                process.ReadStderr(line =>
                {
                    log.AppendStderr(line);
                    stderrTail.Add(line);
                });
                _ = WatchExitAsync(stderrTail);
                await DrainAsync();
            }
            finally
            {
                _finished.TrySetResult();
            }
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
                    log.Append(e);
                    exited |= e is AttemptEvent.Exited;
                    Record = AttemptReducer.Apply(Record, e);
                    owner.Publish(Record);
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
                // Whatever ended the drain early, including a Changed handler that threw, no client runs on unobserved.
                // After its exit, whatever the client started for the user, such as a dev server, keeps running.
                if (exited)
                {
                    process.LeaveDescendantsRunning();
                }
                else
                {
                    process.StopTree();
                }

                process.Dispose();
                log.Dispose();
                held.Dispose();
                owner.Finish(this);
            }
        }

        private void Interpret(string line)
        {
            log.AppendOutput(line);
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            ImmutableArray<AgentEvent> events;
            try
            {
                events = plan.Client.Interpret(line);
            }
            catch (JsonException)
            {
                events = [new AgentEvent.Notice($"iDevelop could not read a line that {Clients.Name(plan.Settings.Client)} printed.")];
            }

            foreach (var e in events)
            {
                Request(new AttemptEvent.Agent(DateTimeOffset.UtcNow, e));
            }
        }

        private async Task WatchExitAsync(Tail stderrTail)
        {
            try
            {
                var code = await process.WaitForExitAsync();
                // The attempt settles without output that comes later, which the closed log ignores.
                await process.WaitForOutputAsync();
                Request(new AttemptEvent.Exited(DateTimeOffset.UtcNow, code, stderrTail.Text));
            }
            finally
            {
                _events.Writer.TryComplete();
            }
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
