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
            new ResolvedCommand(Path.Combine(first, "pi.cmd"), IsBatchShim: true),
            CommandResolver.Create([first, second], [".exe", ".cmd"]).Resolve("pi"));
        Assert.Equal(
            new ResolvedCommand(Path.Combine(second, "pi.exe"), IsBatchShim: false),
            CommandResolver.Create([second, first], [".exe", ".cmd"]).Resolve("pi"));
        Assert.Null(CommandResolver.Create([first, second], [".exe", ".cmd"]).Resolve("codex"));
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

        Assert.Equal(new ResolvedCommand(agy, IsBatchShim: false), resolver.Resolve("agy"));
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
}
