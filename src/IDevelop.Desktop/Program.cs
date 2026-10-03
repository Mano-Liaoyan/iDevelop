using Avalonia;

namespace IDevelop.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure(() => new App { PreferencesFile = Path.Combine(Environment.GetFolderPath(PreferencesRoot), "iDevelop", "settings.json") })
        .UsePlatformDetect()
        .LogToTrace();

    // ApplicationData is ~/.config on macOS, and LocalApplicationData is its native ~/Library/Application Support.
    private static Environment.SpecialFolder PreferencesRoot =>
        OperatingSystem.IsMacOS() ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData;
}
