using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests.Git;

internal sealed class GitFixture : IDisposable
{
    private readonly TempFolder _temp = new();

    public GitFixture(bool initialize = true)
    {
        Folder = _temp.Create("r");
        var config = Path.Combine(_temp.Create("config"), "empty");
        File.WriteAllBytes(config, []);
        Environment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_AUTHOR_NAME"] = "E2", ["GIT_AUTHOR_EMAIL"] = "e2@example.test", ["GIT_AUTHOR_DATE"] = "2026-10-07T00:00:00Z",
            ["GIT_COMMITTER_NAME"] = "E2", ["GIT_COMMITTER_EMAIL"] = "e2@example.test", ["GIT_COMMITTER_DATE"] = "2026-10-07T00:00:00Z",
            ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C", ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        if (initialize)
        {
            Git("init", "-q", "--object-format=sha1", "-b", "main");
            Git("config", "core.autocrlf", "false");
        }
    }

    public string Folder { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    public GitRepository Open(string? folder = null) => Assert.IsType<RepositoryOpen.Opened>(GitRepository.Open(folder ?? Folder, Environment)).Repository;
    public string PathOf(string relative, string? checkout = null) => Path.Combine(checkout ?? Folder, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string contents, string? checkout = null)
    {
        var path = PathOf(relative, checkout);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public CommitId Diamond()
    {
        Write("root.txt", "root\n");
        Assert.Equal("7c64b20d5be53b5c1a291863ef191aa28f6f4d51", Commit("root").Hex);
        Write("plan.txt", "approved\n");
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", Commit("plan").Hex);
        Write("a.txt", "A\n");
        var a = Commit("a");
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", a.Hex);
        return a;
    }

    public CommitId Commit(string message)
    {
        Git("add", "--all");
        Git("-c", "commit.gpgSign=false", "commit", "-q", "-m", message);
        return new(Git("rev-parse", "HEAD").Trim());
    }

    public string Git(params string[] arguments)
    {
        var result = Run(Folder, arguments);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return result.Text;
    }

    public GitResult Run(string checkout, params string[] arguments) => Run(checkout, Environment, arguments);

    public GitResult Run(string checkout, IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = checkout, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var (key, value) in environment) process.StartInfo.Environment[key] = value;
        process.Start();
        using var bytes = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Fixture Git did not exit.");
        }
        stdout.GetAwaiter().GetResult();
        return new(process.ExitCode, bytes.ToArray(), stderr.GetAwaiter().GetResult());
    }

    public static T Read<T>(GitRead<T> result) => Assert.IsType<GitRead<T>.Read>(result).Value;
    public void Dispose() => _temp.Dispose();
}
