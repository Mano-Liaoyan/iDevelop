using Avalonia;
using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

internal sealed class TempFolder : IDisposable
{
    // Each headless test's App remembers its theme in a folder of its own, so the test's files join it there and
    // leave with it. A test on another thread sees whichever App the headless thread runs, so it takes a new folder.
    private readonly string _root = Application.Current is App { PreferencesFile: { } file } app && app.CheckAccess()
        ? Path.GetDirectoryName(file)!
        : Path.Combine(Path.GetTempPath(), "idevelop-tests", Guid.NewGuid().ToString("N"));

    public string Create(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string Seed(params WorkflowEdit[] edits)
    {
        var folder = Create("seed");
        var document = WorkflowDocument.Open(folder);
        foreach (var edit in edits)
        {
            Assert.IsType<EditResult.Applied>(document.Apply(edit));
        }

        document.Save();
        return folder;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
