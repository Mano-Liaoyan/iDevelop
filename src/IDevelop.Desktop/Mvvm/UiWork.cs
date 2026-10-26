namespace IDevelop.Desktop.Mvvm;

/// <summary>
/// Starts work that a command or an event handler does not wait for. A failure goes to the status line, so it does not
/// stop the app and it is not lost.
/// </summary>
internal static class UiWork
{
    /// <param name="work">The work to start.</param>
    /// <param name="report">Shows a failure to the person.</param>
    public static void Start(Func<Task> work, Action<string> report) => _ = Watch(work, report);

    private static async Task Watch(Func<Task> work, Action<string> report)
    {
        try
        {
            await work();
        }
        catch (Exception error)
        {
            report($"Something went wrong: {error.Message}");
        }
    }
}
