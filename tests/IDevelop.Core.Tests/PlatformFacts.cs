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

internal sealed class CaseSensitiveFactAttribute : FactAttribute
{
    public CaseSensitiveFactAttribute()
    {
        var name = "idevelop-case-" + Guid.NewGuid().ToString("N");
        var probe = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name.ToUpperInvariant()));
        try
        {
            if (Directory.Exists(Path.Combine(Path.GetTempPath(), name))) Skip = "Case-sensitive temporary folders only.";
        }
        finally
        {
            probe.Delete();
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

internal sealed class LinuxOrWindowsFactAttribute : FactAttribute
{
    public LinuxOrWindowsFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) Skip = "Linux and Windows only.";
    }
}

internal sealed class LinuxOrWindowsTheoryAttribute : TheoryAttribute
{
    public LinuxOrWindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) Skip = "Linux and Windows only.";
    }
}

internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only.";
    }
}

internal sealed class OtherPlatformFactAttribute : FactAttribute
{
    public OtherPlatformFactAttribute()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) Skip = "Platforms without file identity only.";
    }
}
