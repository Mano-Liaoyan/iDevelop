using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(IDevelop.Desktop.Tests.TestAppBuilder))]

namespace IDevelop.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
