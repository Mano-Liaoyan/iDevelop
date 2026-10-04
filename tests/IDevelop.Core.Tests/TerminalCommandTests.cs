using System.Diagnostics;
using System.Text;
using IDevelop.Execution;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests;

public sealed class TerminalCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_POSIX_shell_changes_into_the_folder_first_with_its_quote_escaped()
    {
        Assert.Equal(
            """cd '/home/me/Bob'\''s project' && claude --resume 5a1c01fc""",
            TerminalCommand.For("/home/me/Bob's project", "claude --resume 5a1c01fc", TerminalShell.Posix));
    }

    [Fact]
    public void PowerShell_changes_into_the_folder_first_with_each_kind_of_single_quote_doubled()
    {
        Assert.Equal(
            """Set-Location -LiteralPath 'C:\Users\me\Bob''s project'; claude --resume 5a1c01fc""",
            TerminalCommand.For(@"C:\Users\me\Bob's project", "claude --resume 5a1c01fc", TerminalShell.PowerShell));
        Assert.Equal(
            "Set-Location -LiteralPath 'C:\\Users\\me\\Bob\u2019\u2019s project'; pi --session 5a1c01fc",
            TerminalCommand.For("C:\\Users\\me\\Bob\u2019s project", "pi --session 5a1c01fc", TerminalShell.PowerShell));
    }

    [Fact]
    public void This_platforms_shell_runs_the_command_in_a_folder_whose_name_holds_a_quote_and_a_space()
    {
        var folder = _temp.Create("Bob's project");
        File.WriteAllText(Path.Combine(folder, "marker.txt"), "inside the project");
        var command = TerminalCommand.For(folder, OperatingSystem.IsWindows() ? "Get-Content marker.txt" : "cat marker.txt", TerminalCommand.Current);
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))])
            : new ProcessStartInfo("/bin/sh", ["-c", command]);
        start.RedirectStandardOutput = true;
        start.UseShellExecute = false;

        using var shell = Process.Start(start)!;
        var output = shell.StandardOutput.ReadToEnd();
        shell.WaitForExit();

        Assert.Equal((0, "inside the project"), (shell.ExitCode, output.Trim()));
    }
}
