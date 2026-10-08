using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.TestSupport;

internal static class LaunchMarkers
{
    public static int Runs(string folder, ClientId client) => Directory.EnumerateFiles(folder, "*.json").Count(file =>
    {
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(file))!;
        return arguments.Contains(client switch
        {
            ClientId.Codex => "app-server",
            ClientId.ClaudeCode or ClientId.Pi => "-p",
            ClientId.Antigravity => "--print=",
        });
    });
}
