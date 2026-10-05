namespace IDevelop.Execution;

/// <summary>The shell a person pastes a command into. PowerShell is the default terminal on Windows.</summary>
internal enum TerminalShell { Posix, PowerShell }

/// <summary>
/// A client's terminal command that first changes into the project folder. From another folder, Claude Code and
/// Antigravity CLI found the session but worked in that folder, and Pi printed nothing.
/// </summary>
internal static class TerminalCommand
{
    public static TerminalShell Current => OperatingSystem.IsWindows() ? TerminalShell.PowerShell : TerminalShell.Posix;

    public static string For(string folder, string command, TerminalShell shell) => shell switch
    {
        TerminalShell.Posix => $"cd {PosixQuoted(folder)} && {command}",
        TerminalShell.PowerShell => $"Set-Location -LiteralPath {PowerShellQuoted(folder)}; {command}",
    };

    /// <summary>A POSIX shell reads everything between single quotes as itself, so only a single quote needs care.</summary>
    private static string PosixQuoted(string text) => $"'{text.Replace("'", @"'\''")}'";

    /// <summary>PowerShell also takes the typographic single quotes as quotes, and reads each one doubled as itself.</summary>
    private static string PowerShellQuoted(string text) =>
        $"'{string.Concat(text.Select(c => c is '\'' or '‘' or '’' or '‚' or '‛' ? $"{c}{c}" : $"{c}"))}'";
}
