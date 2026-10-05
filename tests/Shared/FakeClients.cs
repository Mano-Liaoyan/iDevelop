using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using IDevelop.Execution;

namespace IDevelop.TestSupport;

/// <summary>
/// Installs fake-client shims in a folder of their own: "claude.cmd" on Windows, which runs through cmd.exe like an npm
/// shim, and an executable "claude" script elsewhere. Each shim runs IDevelop.FakeAgent with its rules and passes the
/// client arguments through. <see cref="Resolver"/> searches only that folder, so a real client is never found.
/// </summary>
internal sealed class FakeClients
{
    private static readonly string Agent = Path.Combine(AppContext.BaseDirectory, "IDevelop.FakeAgent.dll");

    // The runtime lives in <dotnet root>/shared/Microsoft.NETCore.App/<version>/.
    private static readonly string Dotnet = Path.GetFullPath(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    public FakeClients(string folder)
    {
        Folder = Directory.CreateDirectory(folder).FullName;
    }

    public string Folder { get; }

    public CommandResolver Resolver => CommandResolver.Create([Folder], OperatingSystem.IsWindows() ? [".COM", ".EXE", ".BAT", ".CMD"] : []);

    /// <summary>A client directory that searches only this folder, after its first refresh.</summary>
    public async Task<ClientDirectory> DiscoverAsync()
    {
        var clients = new ClientDirectory(Resolver);
        // The headless tests block on this on the UI thread, so its end must not need that thread.
        await clients.RefreshAsync().ConfigureAwait(false);
        return clients;
    }

    /// <summary>Writes the shim for <paramref name="command"/>, or replaces it.</summary>
    public string Install(string command, params FakeRule[] rules)
    {
        var rulesFile = Path.Combine(Folder, $"{command}.rules.json");
        var json = new JsonObject
        {
            ["rules"] = new JsonArray([.. rules.Select(rule => new JsonObject
            {
                ["when"] = new JsonArray([.. rule.When.Select(part => JsonValue.Create(part))]),
                ["has"] = new JsonArray([.. rule.Has.Select(part => JsonValue.Create(part))]),
                ["steps"] = new JsonArray([.. rule.Steps.Select(step => step.DeepClone())]),
            })]),
        };
        File.WriteAllText(rulesFile, json.ToJsonString());

        if (OperatingSystem.IsWindows())
        {
            var shim = Path.Combine(Folder, $"{command}.cmd");
            File.WriteAllText(shim, $"@\"{Dotnet}\" \"{Agent}\" --rules \"{rulesFile}\" -- %*\r\n");
            return shim;
        }
        else
        {
            var shim = Path.Combine(Folder, command);
            File.WriteAllText(shim, $"#!/bin/sh\nexec \"{Dotnet}\" \"{Agent}\" --rules \"{rulesFile}\" -- \"$@\"\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return shim;
        }
    }
}

/// <summary>
/// One rule of the fake agent: the steps it runs for client arguments that start with <see cref="When"/> and contain
/// <see cref="Has"/> without gaps.
/// </summary>
internal sealed record FakeRule(ImmutableArray<string> When, ImmutableArray<JsonNode> Steps)
{
    public ImmutableArray<string> Has { get; init; } = [];

    public static FakeRule On(params string[] argumentPrefix) => new([.. argumentPrefix], []);

    public FakeRule With(params string[] arguments) => this with { Has = [.. arguments] };

    public FakeRule RecordArguments(string file) => Step("recordArguments", file);

    public FakeRule RecordWorkingDirectory(string file) => Step("recordWorkingDirectory", file);

    public FakeRule CaptureStdin(string file) => Step("captureStdin", file);

    public FakeRule WaitForStdinEnd() => Step("waitForStdinEnd", true);

    public FakeRule Print(string line) => Step("print", line);

    public FakeRule Print(IEnumerable<string> lines) => lines.Aggregate(this, (rule, line) => rule.Print(line));

    public FakeRule Stderr(string line) => Step("stderr", line);

    public FakeRule Replay(string file) => Step("replay", file);

    public FakeRule Sleep(int milliseconds) => Step("sleep", milliseconds);

    public FakeRule LockFile(string file) => Step("lockFile", file);

    public FakeRule WaitForFile(string file) => Step("waitForFile", file);

    public FakeRule SpawnSleepingChild(string pidFile) => Step("spawnSleepingChild", pidFile);

    public FakeRule SpawnThroughCmd(string pidFile) => Step("spawnThroughCmd", pidFile);

    public FakeRule Hang() => Step("hang", true);

    public FakeRule Exit(int code) => Step("exit", code);

    private FakeRule Step(string name, JsonNode value) => this with { Steps = Steps.Add(new JsonObject { [name] = value }) };
}
