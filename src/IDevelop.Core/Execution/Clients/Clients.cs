using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>
/// Each client's row and names. <see cref="Get"/> is the one switch over the clients, and it is exhaustive, so a new client
/// fails the build until it has a row.
/// </summary>
public static class Clients
{
    public static ImmutableArray<ClientId> All { get; } = [ClientId.ClaudeCode, ClientId.Codex, ClientId.Pi, ClientId.Antigravity];

    public static string Name(ClientId id) => Get(id).Name;

    internal static string WireName(ClientId id) => Get(id).WireName;

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

    internal static ClientDefinition Get(ClientId id) => id switch
    {
        ClientId.ClaudeCode => ClaudeCode.Definition,
        ClientId.Codex => Codex.Definition,
        ClientId.Pi => Pi.Definition,
        ClientId.Antigravity => Antigravity.Definition,
    };
}
