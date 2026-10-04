using System.Collections.Immutable;

namespace IDevelop.Execution;

internal static class ReasoningLevels
{
    /// <summary>Weakest to strongest, so pickers list levels in one order whatever order a client prints them in.</summary>
    private static readonly ImmutableArray<string> Order = ["off", "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public static bool IsLevel(string name) => Order.Contains(name);

    /// <summary>Known levels weakest first, then any others in the order given.</summary>
    public static ImmutableArray<string> Sort(IEnumerable<string> levels) =>
        [.. levels.Distinct().OrderBy(level => Order.IndexOf(level) is var rank and >= 0 ? rank : Order.Length)];

    /// <summary>For a client that names no default: "medium" when offered, else the nearest level, the stronger on a tie.</summary>
    public static string? Default(ImmutableArray<string> levels)
    {
        var medium = Order.IndexOf("medium");
        return levels
            .Where(IsLevel)
            .OrderBy(level => Math.Abs(Order.IndexOf(level) - medium))
            .ThenByDescending(level => Order.IndexOf(level))
            .FirstOrDefault();
    }
}
