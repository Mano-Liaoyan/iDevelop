using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace IDevelop.Execution;

/// <summary>
/// A client command found on the search path. <see cref="IsBatchShim"/> is true for a .cmd or .bat file, which Windows
/// runs through cmd.exe. cmd.exe parses a batch file's command line again, so such a command only takes arguments
/// that cmd.exe cannot reinterpret.
/// </summary>
public sealed partial record ResolvedCommand(string Path, bool IsBatchShim)
{
    /// <summary>
    /// The PATH the command's process gets, which lists the folders the resolver searched. A script that starts node, and
    /// the tools an agent runs, then find what the app found, even when the app itself started with a minimal PATH. Null
    /// keeps the app's own PATH.
    /// </summary>
    public string? SearchPath { get; init; }

    /// <summary>The first argument a batch shim cannot pass safely, or null.</summary>
    internal string? UnsafeArgument(IEnumerable<string> arguments) =>
        IsBatchShim ? arguments.FirstOrDefault(argument => !BatchSafe().IsMatch(argument)) : null;

    [GeneratedRegex("^[A-Za-z0-9._/:=@+-]*$")]
    private static partial Regex BatchSafe();
}

/// <summary>
/// Finds client commands the way a shell would, in the folders of a search path. It never looks in the current folder.
/// Tests point it at a folder of fake-client shims, so every process test goes through real resolution.
/// </summary>
public sealed class CommandResolver
{
    private const string PathMarker = "__IDEVELOP_PATH__";

    private readonly Func<Task<ImmutableArray<string>>> _readSearchPath;
    private readonly ImmutableArray<string> _extensions;
    private Lazy<Task<ImmutableArray<string>>> _searchPath;

    internal CommandResolver(Func<Task<ImmutableArray<string>>> readSearchPath, ImmutableArray<string> extensions)
    {
        _readSearchPath = readSearchPath;
        _extensions = extensions;
        _searchPath = ReadFullyQualifiedFolders();
    }

    /// <summary>
    /// Reads the search path again and completes once it is known. Each refresh of the clients calls it, so a client
    /// installed after the app started is found, and no lookup blocks on the login shell.
    /// </summary>
    internal Task ReloadAsync()
    {
        _searchPath = ReadFullyQualifiedFolders();
        return _searchPath.Value;
    }

    // A relative folder would point into the current folder, so neither the search nor the command's PATH keeps one.
    private Lazy<Task<ImmutableArray<string>>> ReadFullyQualifiedFolders() =>
        new(async () => [.. (await _readSearchPath()).Where(Path.IsPathFullyQualified)]);

    /// <param name="extensions">Windows' PATHEXT entries, tried in order. Empty elsewhere, where a file needs an execute bit.</param>
    public static CommandResolver Create(IReadOnlyList<string> searchPath, IReadOnlyList<string> extensions)
    {
        ImmutableArray<string> folders = [.. searchPath];
        return new CommandResolver(() => Task.FromResult(folders), [.. extensions]);
    }

    /// <summary>
    /// PATH, with PATHEXT on Windows. An app inherits PATH from whatever started it, which can be older than the user's
    /// last install, so the current PATH is merged in too. On Windows that is the machine and user PATH in the registry.
    /// On macOS and Linux it is the login shell's PATH, because an app started from Finder or a desktop launcher gets a
    /// minimal one. That costs a shell start, so it happens at the first lookup and at each refresh.
    /// </summary>
    public static CommandResolver FromEnvironment()
    {
        ImmutableArray<string> path = [.. Split(Environment.GetEnvironmentVariable("PATH"))];
        if (!OperatingSystem.IsWindows())
        {
            return new CommandResolver(async () => MergeSearchPath(path, await LoginShellPathAsync()), []);
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } value ? value : ".COM;.EXE;.BAT;.CMD";
        return new CommandResolver(
            () => Task.FromResult(MergeSearchPath(path, RegistryPath())),
            [.. pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }

    /// <summary>The machine PATH and then the user PATH, as Windows builds a new process's PATH, with variables expanded.</summary>
    private static ImmutableArray<string> RegistryPath() =>
        [.. new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User }
            .SelectMany(target => Split(Environment.GetEnvironmentVariable("Path", target)))
            .Select(Environment.ExpandEnvironmentVariables)];

    public ResolvedCommand? Resolve(string command)
    {
        var searchPath = _searchPath.Value.GetAwaiter().GetResult();
        foreach (var folder in searchPath)
        {
            if (_extensions.IsEmpty)
            {
                var path = Path.Combine(folder, command);
                if (File.Exists(path) && (OperatingSystem.IsWindows() || IsExecutable(path)))
                {
                    return Found(path, isBatch: false);
                }

                continue;
            }

            foreach (var extension in _extensions)
            {
                var path = Path.Combine(folder, command + extension);
                if (File.Exists(path))
                {
                    var isBatch = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
                    return Found(path, isBatch);
                }
            }
        }

        return null;

        ResolvedCommand Found(string path, bool isBatch) => new(path, isBatch) { SearchPath = string.Join(Path.PathSeparator, searchPath) };
    }

    /// <summary>
    /// The login shell's order when it contains every folder of the app's PATH, as for an app started from Finder or a
    /// desktop launcher, so the user's own tools come before the system's copies. Otherwise, as for an app started from
    /// a terminal with an activated environment, the app's order, followed by the login shell's other folders.
    /// </summary>
    internal static ImmutableArray<string> MergeSearchPath(IReadOnlyList<string> app, IReadOnlyList<string> loginShell) =>
        app.All(loginShell.Contains) ? [.. loginShell.Distinct()] : [.. app.Concat(loginShell).Distinct()];

    /// <summary>The PATH a login shell prints after <see cref="PathMarker"/>. Shell start-up noise before it is ignored.</summary>
    internal static ImmutableArray<string> ParseLoginShellPath(string output)
    {
        var start = output.LastIndexOf(PathMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        var value = output[(start + PathMarker.Length)..];
        var end = value.IndexOf('\n');
        return [.. (end < 0 ? value : value[..end]).Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static IEnumerable<string> Split(string? path) =>
        (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [UnsupportedOSPlatform("windows")]
    private static bool IsExecutable(string path) =>
        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static async Task<ImmutableArray<string>> LoginShellPathAsync()
    {
        if (Environment.GetEnvironmentVariable("SHELL") is not { Length: > 0 } shell || !Path.IsPathFullyQualified(shell))
        {
            return [];
        }

        try
        {
            var probe = new Probe(["-i", "-l", "-c", $"printf \"\\n{PathMarker}%s\\n\" \"$PATH\""]) { Timeout = TimeSpan.FromSeconds(5) };
            var output = await Probes.RunAsync(new ResolvedCommand(shell, IsBatchShim: false), probe);
            return ParseLoginShellPath(output.Stdout);
        }
        catch (Exception)
        {
            // Whatever went wrong, the process PATH alone is what the app would have had anyway.
            return [];
        }
    }
}
