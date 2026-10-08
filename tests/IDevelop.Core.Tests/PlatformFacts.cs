namespace IDevelop.TestSupport;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only.";
        }
    }
}

/// <summary>Git for Windows' bash, which Pi runs its commands in on Windows.</summary>
internal sealed class GitBashFactAttribute : FactAttribute
{
    public static readonly string Bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");

    public GitBashFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Bash))
        {
            Skip = $"Git Bash is not installed at {Bash}.";
        }
    }
}

internal sealed class CaseInsensitiveFactAttribute : FactAttribute
{
    public CaseInsensitiveFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Skip = "Windows and macOS only.";
        }
    }
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Linux and macOS only.";
        }
    }
}

internal sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Linux and macOS only.";
        }
    }
}
