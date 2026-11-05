using Avalonia.Threading;

namespace IDevelop.Desktop.Mvvm;

/// <summary>
/// Runs work on the UI thread once per batch of requests. Any thread may request. A request that arrives while the work
/// waits joins that run, and a request that arrives after the run starts posts a new run.
/// </summary>
internal sealed class UiBatch(Action work)
{
    private int _posted;

    public void Request()
    {
        if (Interlocked.Exchange(ref _posted, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _posted, 0);
            work();
        });
    }
}
