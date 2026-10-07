using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using IDevelop.Execution;

namespace IDevelop.TestSupport;

internal enum FakeClientInstallMode { Shim, Direct }

/// <summary>
/// Installs fake clients in a folder of their own. A shim, the default, is "claude.cmd" on Windows, which runs through
/// cmd.exe like an npm shim, and an executable "claude" script elsewhere; it runs IDevelop.FakeAgent with its rules. Direct
/// installs the fake's own apphost as "claude.exe" or "claude", like a native client, so no shim holds the client's pipes;
/// the fake then reads its rules from beside itself. <see cref="Resolver"/> searches only that folder, so a real client
/// is never found.
/// </summary>
internal sealed class FakeClients : IDisposable
{
    private static readonly string Agent = Path.Combine(AppContext.BaseDirectory, "IDevelop.FakeAgent.dll");

    // The runtime lives in <dotnet root>/shared/Microsoft.NETCore.App/<version>/.
    private static readonly string Dotnet = Path.GetFullPath(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    private readonly FakeClientInstallMode _mode;
    private readonly Dictionary<string, string?> _environment = [];

    public FakeClients(string folder, FakeClientInstallMode mode = FakeClientInstallMode.Shim)
    {
        Folder = Directory.CreateDirectory(folder).FullName;
        _mode = mode;
        if (mode == FakeClientInstallMode.Direct)
        {
            var root = Path.GetDirectoryName(Dotnet)!;
            foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant() })
            {
                _environment[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, root);
            }
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _environment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
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

    private static FakeRule Adapt(FakeRule rule)
    {
        var exec = rule.When.IndexOf("exec");
        return exec < 0 ? rule : rule with
        {
            When = [.. rule.When.Take(exec), "app-server"],
            ThreadMethod = rule.When.Contains("resume") ? "thread/resume" : rule.ThreadMethod,
            ThreadId = rule.When.Contains("resume") && rule.Has.Length > 0 ? rule.Has[0] : rule.ThreadId,
            Has = [],
        };
    }

    public string Install(string command, params FakeRule[] rules)
    {
        var executable = command + (OperatingSystem.IsWindows() && _mode == FakeClientInstallMode.Direct ? ".exe" : "");
        var rulesFile = Path.Combine(Folder, $"{executable}.rules.json");
        var json = new JsonObject
        {
            ["rules"] = new JsonArray([.. rules.Select(Adapt).Select(rule => new JsonObject
            {
                ["when"] = new JsonArray([.. rule.When.Select(part => JsonValue.Create(part))]),
                ["has"] = new JsonArray([.. rule.Has.Select(part => JsonValue.Create(part))]),
                ["thread"] = rule.ThreadMethod,
                ["threadId"] = rule.ThreadId,
                ["beforeTurnResponse"] = new JsonArray([.. rule.BeforeTurnResponseSteps.Select(step => step.DeepClone())]),
                ["steps"] = new JsonArray([.. rule.Steps.Select(step => step.DeepClone())]),
            })]),
        };
        File.WriteAllText(rulesFile, json.ToJsonString());

        if (_mode == FakeClientInstallMode.Direct)
        {
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var name = "IDevelop.FakeAgent" + suffix;
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(Folder, name), overwrite: true);
            }

            var apphost = Path.Combine(Folder, executable);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "IDevelop.FakeAgent" + (OperatingSystem.IsWindows() ? ".exe" : "")), apphost, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(apphost, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return apphost;
        }

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
/// One rule of the fake agent: the steps it runs for client arguments that start with <see cref="When"/>, after any
/// leading "-c key=value" pairs, and contain <see cref="Has"/> without gaps.
/// </summary>
internal sealed record FakeRule(ImmutableArray<string> When, ImmutableArray<JsonNode> Steps)
{
    public ImmutableArray<string> Has { get; init; } = [];

    public string? ThreadMethod { get; init; }

    public string? ThreadId { get; init; }

    public ImmutableArray<JsonNode> BeforeTurnResponseSteps { get; init; } = [];

    public FakeRule BeforeTurnResponse(FakeRule steps) => this with { BeforeTurnResponseSteps = steps.Steps };

    public FakeRule Thread(string method, string? id = null) => this with { ThreadMethod = method, ThreadId = id };

    public static FakeRule On(params string[] argumentPrefix) => new([.. argumentPrefix], []);

    public FakeRule With(params string[] arguments) => this with { Has = [.. arguments] };

    public FakeRule RecordArguments(string file) => Step("recordArguments", file);

    public FakeRule RecordWorkingDirectory(string file) => Step("recordWorkingDirectory", file);

    public FakeRule CaptureStdin(string file) => Step("captureStdin", file);

    public FakeRule CapturePrompt(string file) => Step("capturePrompt", file);

    public FakeRule RecordFrames(string file) => Step("recordFrames", file);

    public FakeRule ReadLine(string file) => Step("readLine", file);

    public FakeRule WaitForLine(string pattern) => Step("waitForLine", pattern);

    public FakeRule EchoId(string line) => Step("echoId", line);

    public FakeRule CloseStdin() => Step("closeStdin", true);

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

    /// <summary>Writes <paramref name="text"/> to <paramref name="file"/>, relative to the client's current folder.</summary>
    public FakeRule Write(string file, string text) => Step("write", new JsonArray(JsonValue.Create(file), JsonValue.Create(text)));

    /// <summary>Runs the steps of <c>&lt;n&gt;.json</c> in <paramref name="folder"/> on the n-th call, after copying stdin to <c>&lt;n&gt;.stdin</c>.</summary>
    public FakeRule Scripted(string folder) => Step("scripted", folder);

    /// <summary>The steps as the JSON array that a scripted turn's file holds.</summary>
    public string StepsJson() => new JsonArray([.. Steps.Select(step => step.DeepClone())]).ToJsonString();

    private FakeRule Step(string name, JsonNode value) => this with { Steps = Steps.Add(new JsonObject { [name] = value }) };
}
