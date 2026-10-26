using System.Runtime.Versioning;
using IDevelop.Clients;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests;

public sealed class CommandResolverTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Each_folder_is_searched_in_order_with_each_extension_in_order()
    {
        var first = _temp.Create("first");
        var second = _temp.Create("second");
        File.WriteAllText(Path.Combine(first, "pi"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(first, "pi.cmd"), "@echo off\r\n");
        File.WriteAllText(Path.Combine(second, "pi.exe"), "");

        Assert.Equal(
            new ResolvedCommand(Path.Combine(first, "pi.cmd"), IsBatchShim: true) { SearchPath = $"{first}{Path.PathSeparator}{second}" },
            CommandResolver.Create([first, second], [".exe", ".cmd"]).Resolve("pi"));
        Assert.Equal(
            new ResolvedCommand(Path.Combine(second, "pi.exe"), IsBatchShim: false) { SearchPath = $"{second}{Path.PathSeparator}{first}" },
            CommandResolver.Create([second, first], [".exe", ".cmd"]).Resolve("pi"));
        Assert.Null(CommandResolver.Create([first, second], [".exe", ".cmd"]).Resolve("codex"));
    }

    // An app started from an IDE or an older terminal keeps the PATH it started with, so a client installed since then is
    // found only when a refresh reads the user's PATH again.
    [Fact]
    public async Task A_refresh_finds_a_client_whose_folder_joined_the_path_after_the_app_started()
    {
        var old = _temp.Create("old");
        var installed = _temp.Create("installed");
        await File.WriteAllTextAsync(Path.Combine(installed, "pi.cmd"), "@echo off\r\n");
        List<string> userPath = [old];
        var resolver = new CommandResolver(() => Task.FromResult<System.Collections.Immutable.ImmutableArray<string>>([.. userPath]), [".cmd"]);
        await resolver.ReloadAsync();
        Assert.Null(resolver.Resolve("pi"));

        userPath.Add(installed);
        await resolver.ReloadAsync();

        Assert.Equal(
            new ResolvedCommand(Path.Combine(installed, "pi.cmd"), IsBatchShim: true) { SearchPath = $"{old}{Path.PathSeparator}{installed}" },
            resolver.Resolve("pi"));
    }

    // A relative folder in the child's PATH would make it look in its current folder, which for a turn is the agent's working folder in the project.
    [Fact]
    public void The_path_a_command_gets_holds_only_the_fully_qualified_folders_it_was_searched_in()
    {
        var folder = _temp.Create("bin");
        File.WriteAllText(Path.Combine(folder, "pi.exe"), "");

        Assert.Equal(
            new ResolvedCommand(Path.Combine(folder, "pi.exe"), IsBatchShim: false) { SearchPath = folder },
            CommandResolver.Create([".", "node_modules/.bin", folder], [".exe"]).Resolve("pi"));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Without_extensions_a_file_needs_an_execute_bit()
    {
        var folder = _temp.Create("bin");
        var agy = Path.Combine(folder, "agy");
        File.WriteAllText(agy, "#!/bin/sh\n");
        var resolver = CommandResolver.Create([folder], []);
        Assert.Null(resolver.Resolve("agy"));

        File.SetUnixFileMode(agy, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Equal(new ResolvedCommand(agy, IsBatchShim: false) { SearchPath = folder }, resolver.Resolve("agy"));
    }

    [Fact]
    public void A_batch_shim_refuses_any_argument_cmd_could_reinterpret()
    {
        var shim = new ResolvedCommand(@"C:\tools\pi.cmd", IsBatchShim: true);

        Assert.Null(shim.UnsafeArgument(["-p", "--model", "openrouter/vendor/model-1.5:free", "--print=", "model_reasoning_effort=high"]));
        Assert.Equal("a&b", shim.UnsafeArgument(["--model", "a&b", "x|y"]));
        Assert.Equal("100%", shim.UnsafeArgument(["100%"]));
        Assert.Equal("two words", shim.UnsafeArgument(["two words"]));
        Assert.Null(new ResolvedCommand("/usr/bin/pi", IsBatchShim: false).UnsafeArgument(["a&b"]));
    }

    // npm's cmd-shim since 2019, as npm writes pi.cmd on Windows.
    private const string NpmShim =
        "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\r\n" +
        "IF EXIST \"%dp0%\\node.exe\" (\r\n  SET \"_prog=%dp0%\\node.exe\"\r\n) ELSE (\r\n  SET \"_prog=node\"\r\n  SET PATHEXT=%PATHEXT:;.JS;=;%\r\n)\r\n\r\n" +
        "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@earendil-works\\pi-coding-agent\\dist\\cli.js\" %*\r\n";

    // The shim npm wrote before 2019.
    private const string OldNpmShim =
        "@IF EXIST \"%~dp0\\node.exe\" (\r\n  \"%~dp0\\node.exe\"  --no-warnings \"%~dp0\\node_modules\\pi\\cli.js\" %*\r\n) ELSE (\r\n" +
        "  @SETLOCAL\r\n  @SET PATHEXT=%PATHEXT:;.JS;=;%\r\n  node  --no-warnings \"%~dp0\\node_modules\\pi\\cli.js\" %*\r\n)\r\n";

    [Fact]
    public void An_npm_shim_sends_an_argument_cmd_would_misread_to_node_directly_and_every_other_launch_through_the_shim()
    {
        var npm = _temp.Create("npm");
        File.WriteAllText(Path.Combine(npm, "pi.cmd"), NpmShim);
        File.WriteAllText(Path.Combine(npm, "node.exe"), "");
        var script = Script(npm, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
        var pi = CommandResolver.Create([npm], [".exe", ".cmd"]).Resolve("pi")!;

        // A path under a Windows user name with a space, as Pi's --skill takes it.
        var skill = "C:/Users/John Smith/.pi/agent/skills/deploy/SKILL.md";
        Assert.True(pi.IsBatchShim);
        Assert.Null(pi.UnsafeArgument(["--skill", skill]));
        Assert.Equal(Expected(Path.Combine(npm, "node.exe"), script, "-p", "--skill", skill), Launch(pi, ["-p", "--skill", skill]));
        Assert.Equal(Expected(pi.Path, "-p", "--skill", "C:/Users/John/SKILL.md"), Launch(pi, ["-p", "--skill", "C:/Users/John/SKILL.md"]));
    }

    [Fact]
    public void An_npm_shim_without_its_own_node_runs_the_node_on_the_search_path_and_its_flags()
    {
        var npm = _temp.Create("npm");
        var nodejs = _temp.Create("nodejs");
        File.WriteAllText(Path.Combine(npm, "pi.cmd"), OldNpmShim);
        File.WriteAllText(Path.Combine(nodejs, "node.cmd"), "@echo off\r\n");
        File.WriteAllText(Path.Combine(nodejs, "node.exe"), "");
        var script = Script(npm, "node_modules", "pi", "cli.js");

        var pi = CommandResolver.Create([npm, nodejs], [".exe", ".cmd"]).Resolve("pi")!;

        Assert.Equal(Expected(Path.Combine(nodejs, "node.exe"), "--no-warnings", script, "two words"), Launch(pi, ["two words"]));
    }

    [Fact]
    public void A_batch_file_iDevelop_cannot_follow_still_refuses_an_argument_cmd_would_misread()
    {
        var npm = _temp.Create("npm");
        File.WriteAllText(Path.Combine(npm, "pi.cmd"), NpmShim);
        File.WriteAllText(Path.Combine(npm, "codex.cmd"), "@echo off\r\n\"%~dp0\\codex.exe\" %*\r\n");
        Script(npm, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
        var resolver = CommandResolver.Create([npm], [".exe", ".cmd"]);

        // No node beside the shim or on the search path, a batch file that is not an npm shim, and an npm shim whose script
        // is gone.
        foreach (var command in new[] { "pi", "codex" })
        {
            var shim = resolver.Resolve(command)!;
            Assert.Equal("two words", shim.UnsafeArgument(["two words"]));
            Assert.Null(shim.Launch(["two words"]));
        }

        File.WriteAllText(Path.Combine(npm, "node.exe"), "");
        File.Delete(Path.Combine(npm, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js"));
        Assert.Equal("two words", resolver.Resolve("pi")!.UnsafeArgument(["two words"]));
        Assert.Equal("100%", new ResolvedCommand(@"C:\tools\pi.cmd", IsBatchShim: true).UnsafeArgument(["100%"]));
    }

    private static string Script(string folder, params string[] parts)
    {
        var script = Path.Combine([folder, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");
        return script;
    }

    /// <summary>The program and its arguments, one per line, which compare by text.</summary>
    private static (string, string) Launch(ResolvedCommand command, string[] arguments) =>
        command.Launch(arguments) is { } launch ? (launch.File, string.Join("\n", launch.Arguments)) : throw new Xunit.Sdk.XunitException("No launch.");

    private static (string, string) Expected(string file, params string[] arguments) => (file, string.Join("\n", arguments));

    [Fact]
    public void The_login_shell_path_is_read_after_its_marker_and_start_up_noise_is_ignored()
    {
        var output = "bash: no job control in this shell\nWelcome back\n\n__IDEVELOP_PATH__/opt/homebrew/bin:/usr/local/bin::/usr/bin\n";

        Assert.Equal(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"], CommandResolver.ParseLoginShellPath(output).ToArray());
        Assert.Empty(CommandResolver.ParseLoginShellPath("zsh: command not found: printf\n"));
    }

    // An app started from Finder or a desktop launcher gets a minimal PATH, which the login shell's PATH contains. The
    // user's own tools then come before the system's copies, as in their terminal.
    [Fact]
    public void A_path_the_login_shell_contains_takes_the_login_shells_order()
    {
        Assert.Equal(
            ["/opt/homebrew/bin", "/Users/me/.local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"],
            CommandResolver.MergeSearchPath(
                ["/usr/bin", "/bin", "/usr/sbin", "/sbin"],
                ["/opt/homebrew/bin", "/Users/me/.local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]).ToArray());
    }

    // An app started from a terminal with an activated virtual environment keeps that environment first.
    [Fact]
    public void A_path_with_its_own_folders_keeps_its_order_and_gains_the_login_shells_other_folders()
    {
        Assert.Equal(
            ["/work/app/.venv/bin", "/usr/bin", "/bin", "/opt/homebrew/bin"],
            CommandResolver.MergeSearchPath(["/work/app/.venv/bin", "/usr/bin", "/bin"], ["/opt/homebrew/bin", "/usr/bin", "/bin"]).ToArray());
        Assert.Equal(["/usr/bin", "/bin"], CommandResolver.MergeSearchPath(["/usr/bin", "/bin"], []).ToArray());
    }
}
