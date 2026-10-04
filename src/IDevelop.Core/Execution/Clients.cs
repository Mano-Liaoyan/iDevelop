using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Each client's names. Every switch here is exhaustive, so a new client fails the build until it has them.</summary>
public static class Clients
{
    public static ImmutableArray<ClientId> All { get; } = [ClientId.ClaudeCode, ClientId.Codex, ClientId.Pi, ClientId.Antigravity];

    public static string Name(ClientId id) => id switch
    {
        ClientId.ClaudeCode => "Claude Code",
        ClientId.Codex => "Codex",
        ClientId.Pi => "Pi",
        ClientId.Antigravity => "Antigravity CLI",
    };

    /// <summary>The name the workflow file and the attempt log store.</summary>
    internal static string WireName(ClientId id) => id switch
    {
        ClientId.ClaudeCode => "claude-code",
        ClientId.Codex => "codex",
        ClientId.Pi => "pi",
        ClientId.Antigravity => "antigravity",
    };

    internal static ClientId? ParseWireName(string name)
    {
        foreach (var id in All)
        {
            if (WireName(id) == name)
            {
                return id;
            }
        }

        return null;
    }
}
