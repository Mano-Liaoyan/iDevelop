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
    private readonly Task _loop;
    private int _takes;

    public ScheduledLoop(GitRepository repository) => _loop = Task.Run(() => Run(repository));

    /// <summary>Completes once the loop first holds the lock.</summary>
    public Task Held => _held.Task;

    /// <summary>Completes once the loop holds the lock a second time.</summary>
    public Task Again => _again.Task;

    public int Takes => Volatile.Read(ref _takes);

    /// <summary>Lets the lock go, and returns once the loop took it again or was refused it.</summary>
    public void Turn()
    {
        if (_stop.IsCancellationRequested) return;
        _turn.Release();
        try { _settled.Wait(_stop.Token); }
        catch (OperationCanceledException) { }
    }

    private void Run(GitRepository repository)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var mutation = repository.TakeMutationLock(TimeSpan.FromSeconds(30), () =>
            {
                _settled.Release();
                return _stop.IsCancellationRequested;
            }, scheduled: true);
            if (mutation is null) return;
            if (Interlocked.Increment(ref _takes) == 1) _held.TrySetResult();
            else
            {
                _again.TrySetResult();
                _settled.Release();
            }
            try { _turn.Wait(_stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
