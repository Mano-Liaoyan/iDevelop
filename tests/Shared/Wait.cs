using System.Text.Json;

namespace IDevelop.TestSupport;

internal static class Wait
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits until <paramref name="done"/> holds, for what nothing announces. A process can still hold or write the file
    /// that the condition reads, so an IO or JSON error counts as "not yet".
    /// </summary>
    public static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            try
            {
                if (done()) return;
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
            }

            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The condition did not become true in time.");
            await Task.Delay(20);
        }
    }
}
