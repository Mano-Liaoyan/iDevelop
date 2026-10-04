using System.Runtime.Versioning;
using IDevelop.Execution;
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

    // A relative folder in the child's PATH would make it look in its current folder, which for a run is the project.
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
