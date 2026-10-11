using IDevelop.Execution;

namespace IDevelop.Core.Tests.Git;

/// <summary>
/// A run's scheduled work that holds the repository's mutation lock in a tight loop. Each time a waiting step calls
/// <see cref="Turn"/> after a try the lock refused, the loop lets the lock go and takes it again at once, as scheduled work
/// does. <see cref="Turn"/> returns only once the loop holds the lock again or has itself been refused it, so the step's
/// next try always meets the loop's new take, never the moment between letting go and taking again.
/// </summary>
internal sealed class ScheduledLoop : IAsyncDisposable
{
    private readonly SemaphoreSlim _turn = new(0);
    private readonly SemaphoreSlim _settled = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _again = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _holding = new();
    private readonly Lock _gate = new();
    private readonly Task _loop;
    private int _takes;
    // Set by Turn and cleared by the loop as it settles, so each turn settles once, not once per refused try.
    private int _turning;

    public ScheduledLoop(GitRepository repository) => _loop = Task.Run(() => Run(repository));

    /// <summary>Completes once the loop first holds the lock.</summary>
    public Task Held => _held.Task;

    /// <summary>Completes once the loop holds the lock a second time.</summary>
    public Task Again => _again.Task;

    public int Takes => Volatile.Read(ref _takes);

    /// <summary>
    /// Lets the lock go, and returns once the loop took it again or was refused it. Turns of several waiting steps take place
    /// one after another.
    /// </summary>
    public void Turn()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            Volatile.Write(ref _turning, 1);
            _turn.Release();
            try { _settled.Wait(_stop.Token); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Returns once the loop holds the lock, at once while it does.</summary>
    public void WaitHeld()
    {
        try { _holding.Wait(_stop.Token); }
        catch (OperationCanceledException) { }
    }

    private void Settle()
    {
        if (Interlocked.Exchange(ref _turning, 0) == 1) _settled.Release();
    }

    private void Run(GitRepository repository)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var mutation = repository.TakeMutationLock(TimeSpan.FromSeconds(30), () =>
            {
                Settle();
                return _stop.IsCancellationRequested;
            }, scheduled: true);
            if (mutation is null) return;
            _holding.Set();
            if (Interlocked.Increment(ref _takes) == 1) _held.TrySetResult();
            else _again.TrySetResult();
            Settle();
            try { _turn.Wait(_stop.Token); }
            catch (OperationCanceledException) { return; }
            finally { _holding.Reset(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
