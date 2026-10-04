using System.Collections.Immutable;
using System.Text;

namespace IDevelop.Execution;

/// <summary>A short command a client answers without starting an agent: a model list or a sign-in check.</summary>
internal sealed record Probe(ImmutableArray<string> Arguments)
{
    public string? Stdin { get; init; }

    /// <summary>A probe that keeps running after it answers gets the end of its input at the first line this accepts.</summary>
    public Func<string, bool>? DoneWhen { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary><see cref="ExitCode"/> is null when the probe was stopped, at its timeout or after it answered and did not exit.</summary>
internal sealed record ProbeOutput(int? ExitCode, string Stdout, string Stderr, bool TimedOut);

internal static class Probes
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs a probe in the temporary folder, so it never runs inside a project. On timeout it stops the tree and returns
    /// what it read with TimedOut set. A probe with DoneWhen gets the end of its input at its answer, and is stopped only
    /// if it has not exited 5 seconds later.
    /// </summary>
    /// <exception cref="LaunchException">The command did not start.</exception>
    public static async Task<ProbeOutput> RunAsync(ResolvedCommand command, Probe probe)
    {
        using var child = ChildProcess.Start(command, probe.Arguments, Path.GetTempPath());
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pi's RPC mode may stop at the end of its input before it answers, so a probe that waits for an answer keeps stdin open.
        var input = child.WriteStdinAsync(probe.Stdin ?? "", close: probe.DoneWhen is null);
        child.ReadStdout(line =>
        {
            lock (stdout)
            {
                stdout.Append(line).Append('\n');
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
            await input;
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
