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

/// <summary>Windows and macOS, whose volumes ignore case by default, when the temporary folder's volume does.</summary>
internal sealed class CaseInsensitiveFactAttribute : FactAttribute
{
    public CaseInsensitiveFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Skip = "Windows and macOS only.";
        }
        else if (!TemporaryVolume.IgnoresCase)
        {
            Skip = "Case-insensitive temporary folders only.";
        }
    }
}

internal sealed class CaseSensitiveFactAttribute : FactAttribute
{
    public CaseSensitiveFactAttribute()
    {
        if (TemporaryVolume.IgnoresCase) Skip = "Case-sensitive temporary folders only.";
    }
}

/// <summary>Case sensitivity belongs to a volume, so the case facts probe the one that holds the temporary folder.</summary>
internal static class TemporaryVolume
{
    private static readonly Lazy<bool> Probe = new(() =>
    {
        var name = "idevelop-case-" + Guid.NewGuid().ToString("N");
        var probe = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name.ToUpperInvariant()));
        try
        {
            return Directory.Exists(Path.Combine(Path.GetTempPath(), name));
        }
        finally
        {
            probe.Delete();
        }
    });

    public static bool IgnoresCase => Probe.Value;
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
