using Avalonia.Headless.XUnit;

namespace IDevelop.Desktop.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void The_main_window_opens_titled_iDevelop()
    {
        var window = new MainWindow();

        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal("iDevelop", window.Title);
    }
}
