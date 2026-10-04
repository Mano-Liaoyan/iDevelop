using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace IDevelop.Execution;

/// <summary>
/// A client command found on the search path. <see cref="IsBatchShim"/> is true for a .cmd or .bat file, which Windows
/// runs through cmd.exe. cmd.exe parses a batch file's command line again, so such a command only takes arguments
/// that cmd.exe cannot reinterpret.
/// </summary>
public sealed partial record ResolvedCommand(string Path, bool IsBatchShim)
{
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

    private readonly Lazy<ImmutableArray<string>> _searchPath;
    private readonly ImmutableArray<string> _extensions;

    private CommandResolver(Func<ImmutableArray<string>> searchPath, ImmutableArray<string> extensions)
    {
        _searchPath = new Lazy<ImmutableArray<string>>(searchPath);
        _extensions = extensions;
    }

    /// <param name="extensions">Windows' PATHEXT entries, tried in order. Empty elsewhere, where a file needs an execute bit.</param>
    public static CommandResolver Create(IReadOnlyList<string> searchPath, IReadOnlyList<string> extensions)
    {
        ImmutableArray<string> folders = [.. searchPath];
        return new CommandResolver(() => folders, [.. extensions]);
    }

    /// <summary>
    /// PATH, with PATHEXT on Windows. On macOS and Linux it also appends the login shell's PATH, because an app started
    /// from Finder or a desktop launcher gets a minimal PATH. That costs a shell start, so it happens at the first lookup.
    /// </summary>
    public static CommandResolver FromEnvironment()
    {
        ImmutableArray<string> path = [.. Split(Environment.GetEnvironmentVariable("PATH"))];
        if (!OperatingSystem.IsWindows())
        {
            return new CommandResolver(() => [.. path.Concat(LoginShellPath()).Distinct()], []);
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } value ? value : ".COM;.EXE;.BAT;.CMD";
        return new CommandResolver(() => path, [.. pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }

    public ResolvedCommand? Resolve(string command)
    {
        foreach (var folder in _searchPath.Value.Where(Path.IsPathFullyQualified))
        {
            if (_extensions.IsEmpty)
            {
                var path = Path.Combine(folder, command);
                if (File.Exists(path) && (OperatingSystem.IsWindows() || IsExecutable(path)))
                {
                    return new ResolvedCommand(path, IsBatchShim: false);
                }

                continue;
            }

            foreach (var extension in _extensions)
            {
                var path = Path.Combine(folder, command + extension);
                if (File.Exists(path))
                {
                    var isBatch = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
                    return new ResolvedCommand(path, isBatch);
                }
            }
        }

        return null;
    }

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

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool IsExecutable(string path) =>
        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static ImmutableArray<string> LoginShellPath()
    {
        if (Environment.GetEnvironmentVariable("SHELL") is not { Length: > 0 } shell || !Path.IsPathFullyQualified(shell))
        {
            return [];
        }

        try
        {
            var probe = new Probe(["-i", "-l", "-c", $"printf \"\\n{PathMarker}%s\\n\" \"$PATH\""]) { Timeout = TimeSpan.FromSeconds(5) };
            var output = Probes.RunAsync(new ResolvedCommand(shell, IsBatchShim: false), probe, CancellationToken.None).GetAwaiter().GetResult();
            return ParseLoginShellPath(output.Stdout);
        }
        catch (LaunchException)
        {
            return [];
        }
    }
}
