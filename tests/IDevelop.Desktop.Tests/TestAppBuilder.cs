using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(IDevelop.Desktop.Tests.TestAppBuilder))]

namespace IDevelop.Desktop.Tests;

public static class TestAppBuilder
{
    // Skia rather than headless drawing, so connector and connection hit tests use real geometry.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
