using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace IDevelop.Desktop.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void The_main_window_opens_titled_iDevelop()
    {
        var window = new MainWindow();

        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal("iDevelop", window.Title);
    }

    [AvaloniaFact]
    public void Saving_beside_another_workflow_file_shows_why_and_keeps_the_unsaved_changes()
    {
        var folder = _temp.Create("plan");
        var shell = Shell.Open(folder);
        shell.Click(shell.Find<Button>("AddTask"));
        var other = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, ".idevelop", "workflows")).FullName, "other.json");
        File.WriteAllText(other, "{}");

        shell.Press(Key.S, RawInputModifiers.Control);

        Assert.Equal($"Not saved. {other} is another workflow file, and this version of iDevelop keeps one workflow per project.", shell.Status);
        Assert.Equal("plan* - iDevelop", shell.Window.Title);
        Assert.True(shell.ShowsUnsavedChanges);
        Assert.Equal(["other.json"], Directory.EnumerateFiles(Path.GetDirectoryName(other)!).Select(Path.GetFileName));
    }
}
