using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static class ClientRegistry
{
    /// <summary>Weakest to strongest, so pickers list levels in one order whatever order a client prints them in.</summary>
    private static readonly ImmutableArray<string> LevelOrder = ["off", "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public static ClientDefinition Get(ClientId id) => id switch
    {
        ClientId.ClaudeCode => ClaudeCode.Definition,
        ClientId.Codex => Codex.Definition,
        ClientId.Pi => Pi.Definition,
        ClientId.Antigravity => Antigravity.Definition,
    };

    public static bool IsLevel(string name) => LevelOrder.Contains(name);

    /// <summary>Known levels weakest first, then any others in the order given.</summary>
    public static ImmutableArray<string> SortLevels(IEnumerable<string> levels) =>
        [.. levels.Distinct().OrderBy(level => LevelOrder.IndexOf(level) is var rank and >= 0 ? rank : LevelOrder.Length)];

    /// <summary>For a client that names no default: "medium" when offered, else the nearest level, the stronger on a tie.</summary>
    public static string? DefaultLevel(ImmutableArray<string> levels)
    {
        var medium = LevelOrder.IndexOf("medium");
        return levels
            .Where(IsLevel)
            .OrderBy(level => Math.Abs(LevelOrder.IndexOf(level) - medium))
            .ThenByDescending(level => LevelOrder.IndexOf(level))
            .FirstOrDefault();
    }

    /// <summary>Why a probe did not answer as expected, in the words of its last output line.</summary>
    public static string ProbeFailure(string command, ProbeOutput output)
    {
        var said = LastLine(output.Stderr) ?? LastLine(output.Stdout);
        var ended = output.ExitCode is { } code ? $"{command} exited with code {code}" : $"{command} was stopped";
        return said is null ? $"{ended}." : $"{ended}: {said}";
    }

    public static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0);

    private static string? LastLine(string text) => Lines(text).Select(line => line.Trim()).LastOrDefault();
}
