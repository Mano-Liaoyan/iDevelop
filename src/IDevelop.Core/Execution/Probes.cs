using System.Collections.Immutable;
using System.Text;

namespace IDevelop.Execution;

/// <summary>A short command a client answers without starting an agent: a model list, a sign-in check, or a skill list.</summary>
internal sealed record Probe(ImmutableArray<string> Arguments)
{
    public string? Stdin { get; init; }

    /// <summary>A probe that keeps running after it answers gets the end of its input at the first line this accepts.</summary>
    public Func<string, bool>? DoneWhen { get; init; }

    /// <summary>A probe that talks: what to write after a line it reads, or null. Its input stays open until it answers.</summary>
    public Func<string, string?>? Reply { get; init; }

    /// <summary>
    /// The folder the probe runs in. Null, the default, is the temporary folder, so a model list or a sign-in check never
    /// runs inside a project. A skill list runs in the project, whose own skills and settings it reads.
    /// </summary>
    public string? Folder { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary><see cref="ExitCode"/> is null when the probe was stopped, at its timeout or after it answered and did not exit.</summary>
internal sealed record ProbeOutput(int? ExitCode, string Stdout, string Stderr, bool TimedOut);

internal static class Probes
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs a probe in its folder, the temporary folder unless it names one. On timeout it stops the tree and returns what
    /// it read with TimedOut set. A probe with DoneWhen gets the end of its input at its answer, and is stopped only if it
    /// has not exited 5 seconds later.
    /// </summary>
    /// <exception cref="LaunchException">The command did not start.</exception>
    public static async Task<ProbeOutput> RunAsync(ResolvedCommand command, Probe probe)
    {
        using var child = ChildProcess.Start(command, probe.Arguments, probe.Folder ?? Path.GetTempPath(), ProcessLifetime.Standalone);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pi's RPC mode may stop at the end of its input before it answers, so a probe that waits for an answer keeps stdin open.
        var writes = new object();
        var input = child.WriteStdinAsync(probe.Stdin ?? "", close: probe.DoneWhen is null);
        child.ReadStdout(line =>
        {
            lock (stdout)
            {
                stdout.Append(line).Append('\n');
            }

            if (probe.Reply?.Invoke(line) is { } reply)
            {
                lock (writes)
                {
                    input = WriteAfter(input, child, reply);
                }
            }

            if (probe.DoneWhen?.Invoke(line) == true)
            {
                answered.TrySetResult();
            }
        });
        child.ReadStderr(line =>
        {
            lock (stderr)
            {
                stderr.Append(line).Append('\n');
            }
        });

        using var limit = new CancellationTokenSource(probe.Timeout);
        var exit = child.WaitForExitAsync();
        var first = await Task.WhenAny(exit, answered.Task, Task.Delay(Timeout.Infinite, limit.Token));
        var ended = first == exit;
        if (first == answered.Task)
        {
            // Pi's RPC documentation asks for this orderly shutdown. Stopping Pi's tree instead made the next pi command
            // take about 30 seconds to exit, which timed out its sign-in checks.
            Task written;
            lock (writes)
            {
                written = input;
            }

            await written;
            await child.WriteStdinAsync("", close: true);
            ended = await Task.WhenAny(exit, Task.Delay(ShutdownGrace)) == exit;
        }

        if (!ended)
        {
            child.StopTree();
        }

        await child.WaitForOutputAsync();
        int? exitCode = ended ? await exit : null;
        return new ProbeOutput(exitCode, Snapshot(stdout), Snapshot(stderr), TimedOut: first != exit && first != answered.Task);
    }

    private static async Task WriteAfter(Task before, ChildProcess child, string text)
    {
        await before;
        await child.WriteStdinAsync(text, close: false);
    }

    /// <summary>Why a probe did not answer as expected, in the words of its last output line.</summary>
    public static string Failure(string command, ProbeOutput output)
    {
        var said = TextLines.LastLine(output.Stderr) ?? TextLines.LastLine(output.Stdout);
        var ended = output.ExitCode is { } code ? $"{command} exited with code {code}" : $"{command} was stopped";
        return said is null ? $"{ended}." : $"{ended}: {said}";
    }

    private static string Snapshot(StringBuilder text)
    {
        lock (text)
        {
            return text.ToString();
        }
    }
}
