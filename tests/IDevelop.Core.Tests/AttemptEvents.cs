using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

/// <summary>The events of a Codex attempt at the Build task, each the given number of seconds after <see cref="T0"/>.</summary>
internal static class AttemptEvents
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 4, 5, 0, 0, TimeSpan.Zero);
    public static readonly AttemptId First = new(Guid.Parse("019aa000-0000-7000-8000-000000000001"));
    public static readonly ExecutionSettings CodexHigh = new(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" };

    public static AttemptEvent.Launched LaunchedAt1s => new(T0.AddSeconds(1), 4242, T0.AddSeconds(1));

    public static AttemptEvent.Requested BuildRequested(AttemptId attempt) =>
        new(T0, attempt, TestTasks.Build, "Implement atomic save", CodexHigh, "# Implement atomic save\n", "codex", ["exec", "--json"]);

    public static AttemptEvent Said(int seconds, AgentEvent e) => new AttemptEvent.Agent(T0.AddSeconds(seconds), e);

    public static AttemptEvent.Exited Exit(int seconds, int code, string stderr = "") => new(T0.AddSeconds(seconds), code, stderr);

    public static AttemptEvent.MessageQueued Sent(int seconds, string text, bool stopsTurn = false) => new(T0.AddSeconds(seconds), text, stopsTurn);

    public static AttemptEvent.TurnRequested NextTurn(int seconds, string prompt) =>
        new(T0.AddSeconds(seconds), prompt, "codex", ["exec", "resume", "--json", "thread-1", "-"]);
}
