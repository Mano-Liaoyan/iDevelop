namespace IDevelop.TestSupport;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Only Windows runs batch shims through cmd.exe.";
        }
    }
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Only Linux and macOS need an execute bit.";
        }
    }
}
