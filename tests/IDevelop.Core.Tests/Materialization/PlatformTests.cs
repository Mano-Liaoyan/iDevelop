using System.Diagnostics;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

public sealed class PlatformTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("input")]
    public async Task Publication_and_successor_checkout_obey_line_endings_and_binary_attributes(string mode)
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U), configureBase: git =>
        {
            git.Git("config", "core.autocrlf", mode);
            git.Write(".gitattributes", "*.bin binary\ncrlf.txt text eol=crlf\n");
            return git.Commit("attributes");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("lf.txt", "one\ntwo\n", ready.Checkout);
        f.Git.Write("crlf.txt", "one\ntwo\n", ready.Checkout);
        File.WriteAllBytes(Path.Combine(ready.Checkout, "data.bin"), [13, 10, 0, 13]);
        await f.Close(ready);
        var result = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt)).Result;
        var commit = Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex;
        Assert.Equal(new byte[] { 111, 110, 101, 10, 116, 119, 111, 10 }, Blob(f.Git, commit, "lf.txt"));
        Assert.Equal(new byte[] { 111, 110, 101, 10, 116, 119, 111, 10 }, Blob(f.Git, commit, "crlf.txt"));
        Assert.Equal(new byte[] { 13, 10, 0, 13 }, Blob(f.Git, commit, "data.bin"));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        var successor = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        Assert.Equal(mode == "true" ? new byte[] { 111, 110, 101, 13, 10, 116, 119, 111, 13, 10 }
            : new byte[] { 111, 110, 101, 10, 116, 119, 111, 10 }, File.ReadAllBytes(Path.Combine(successor.Checkout, "lf.txt")));
        Assert.Equal(new byte[] { 111, 110, 101, 13, 10, 116, 119, 111, 13, 10 }, File.ReadAllBytes(Path.Combine(successor.Checkout, "crlf.txt")));
        Assert.Equal(new byte[] { 13, 10, 0, 13 }, File.ReadAllBytes(Path.Combine(successor.Checkout, "data.bin")));
    }

    [Fact]
    public async Task GitTree_snapshot_from_a_clean_linked_checkout_reads_its_own_index()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("a.txt", "root changed\n");
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
        Assert.Equal("ed1c97a309dc85781c41c02b31bb9f115be3899d", GitTree.Snapshot(ready.Checkout));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3\n", f.Git.Run(ready.Checkout, "rev-parse", "HEAD").Text);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Repository_root_sdk_build_excludes_sources_in_nested_task_checkouts()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Write("p.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
            git.Write("Class.cs", "public class Root { }\n");
            git.Write("NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>\n");
            return git.Commit("sdk");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("Broken.cs", "public class Broken { this is a compile error }\n", ready.Checkout);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            ["build", "--nologo", "-p:TreatWarningsAsErrors=false"])
        {
            WorkingDirectory = f.Git.Folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.True(File.Exists(Path.Combine(f.Git.Folder, "bin/Debug/net10.0/p.dll")), output);
        Assert.Equal("public class Broken { this is a compile error }\n", File.ReadAllText(Path.Combine(ready.Checkout, "Broken.cs")));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("publish")]
    public async Task Moving_a_project_blocks_existing_task_ownership_without_repairing_registrations(string action)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var operation = f.Op();
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        if (action == "publish") await f.Close(ready);
        var moved = Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "s");
        f.ReleaseControl();
        Directory.Move(f.Git.Folder, moved);
        var before = f.Git.Run(moved, "worktree", "list", "--porcelain");
        Assert.Equal(0, before.ExitCode);
        var store = RunStore.Open(moved);
        using var permit = Assert.IsType<ControlTake.Owned>(store.TakeControl(W, f.RunId)).Permit;
        using var lease = Assert.IsType<LeaseTake.Taken>(permit.TakeTask(T)).Lease;
        var materializer = Execution.Materializer.Open(moved, store, null, new QuiescentBoundary(), new Clock(), f.Git.Environment);
        if (action == "prepare")
            Assert.Equal("UncertainOwnership", Assert.IsType<Preparation.Blocked>(await materializer.Prepare(lease, operation, new AttemptCause.Initial())).Block.Problem.ToString());
        else
            Assert.Equal("UncertainOwnership", Assert.IsType<Publication.Blocked>(materializer.Publish(lease, f.Op(), ready.Execution.Launch.Attempt)).Block.Problem.ToString());
        var after = f.Git.Run(moved, "worktree", "list", "--porcelain");
        Assert.Equal(0, after.ExitCode);
        Assert.Equal(before.Stdout, after.Stdout);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(moved, ".worktrees/93f23689/90d5b0a2/a.txt")));
    }

    [Fact]
    public async Task Preparation_initializes_the_recorded_submodule_and_salvage_preserves_dirty_module_files()
    {
        using var module = new GitFixture();
        module.Write("module.txt", "module\n");
        Assert.Equal("fb88360ef5a51929c241ce46428ee8571a45722c", module.Commit("module").Hex);
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Git("config", "--file", git.Environment["GIT_CONFIG_GLOBAL"], "protocol.file.allow", "always");
            git.Git("submodule", "add", "-q", module.Folder, "m");
            return git.Commit("submodule");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Assert.Equal("fb88360ef5a51929c241ce46428ee8571a45722c\n", f.Git.Run(Path.Combine(ready.Checkout, "m"), "rev-parse", "HEAD").Text);
        f.Git.Write("m/module.txt", "dirty module\n", ready.Checkout);
        f.Git.Write("m/new.txt", "module unfinished\n", ready.Checkout);
        var retained = Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("dirty module\n", File.ReadAllText(Path.Combine(ready.Checkout, "m/module.txt")));
        Assert.Equal("module unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "m/new.txt")));
        Assert.Equal("160000 commit fb88360ef5a51929c241ce46428ee8571a45722c\tm\n", f.Git.Git("ls-tree", retained.Commit.Hex, "m"));
    }

    private static byte[] Blob(GitFixture git, string commit, string path)
    {
        var result = git.Run(git.Folder, "cat-file", "blob", commit + ":" + path);
        Assert.Equal(0, result.ExitCode);
        return result.Stdout;
    }
}
