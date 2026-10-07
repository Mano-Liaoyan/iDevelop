namespace IDevelop.TestSupport;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    public void Advance(TimeSpan elapsed, bool fireTimers = true)
    {
        List<(TimerCallback Callback, object? State)> due = [];
        lock (_gate)
        {
            _now += elapsed;
            if (fireTimers)
            {
                foreach (var timer in _timers.ToArray())
                {
                    if (timer.At is { } at && at <= _now)
                    {
                        due.Add((timer.Callback, timer.State));
                        timer.At = timer.Period == Timeout.InfiniteTimeSpan ? null : _now + timer.Period;
                    }
                }
            }
        }
        foreach (var (callback, state) in due) callback(state);
    }
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? At { get; set; }
        public TimeSpan Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Period = period;
                At = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }
        }
        public void Dispose() { lock (owner._gate) { At = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
