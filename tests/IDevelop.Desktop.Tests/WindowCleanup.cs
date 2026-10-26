using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Xunit.Sdk;

[assembly: IDevelop.Desktop.Tests.WindowCleanup]

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Ends every window a headless test left open once the test is over. The headless platform keeps a shown window and
/// its rendered frames until the window closes, so each test that opened one held tens of megabytes, and the whole
/// suite grew to about 18 GB. Disposing the window's platform side ends it without <see cref="Window.OnClosing"/>, as
/// the main window would otherwise ask about unsaved changes or active runs. A main window first closes its projects, as
/// quitting does, so its runs stop and it lets go of each project's <c>window.lock</c>. Windows deletes no open file, so a
/// lock that stayed open failed the test's cleanup of its folder.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class WindowCleanupAttribute : BeforeAfterTestAttribute
{
    private static readonly HashSet<Window> Open = [];

    static WindowCleanupAttribute()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => { lock (Open) Open.Add(window); });
        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => { lock (Open) Open.Remove(window); });
    }

    // A headless test runs on the headless thread, which owns its windows. A plain test runs elsewhere and leaves them.
    public override void After(MethodInfo methodUnderTest)
    {
        Window[] windows;
        lock (Open)
        {
            windows = [.. Open.Where(window => window.CheckAccess())];
            Open.ExceptWith(windows);
        }

        foreach (var window in windows)
        {
            if (window is MainWindow main)
            {
                Leave(main);
            }

            window.PlatformImpl?.Dispose();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Shell.Forget();
        }
    }

    private static void Leave(MainWindow window)
    {
        var leaving = window.ViewModel.Leave();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!leaving.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
