using Avalonia;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

internal static class AppTempFolder
{
    // Each headless test's App remembers its theme in a folder of its own, so the test's files join it there and
    // leave with it. A test on another thread sees whichever App the headless thread runs, so it takes a new folder.
    public static TempFolder New() =>
        new(Application.Current is App { PreferencesFile: { } file } app && app.CheckAccess() ? Path.GetDirectoryName(file) : null);

    public static string Seed(this TempFolder temp, params WorkflowEdit[] edits)
    {
        var folder = temp.Create("seed");
        var document = WorkflowDocument.Open(folder);
        foreach (var edit in edits)
        {
            Assert.IsType<EditResult.Applied>(document.Apply(edit));
        }

        document.Save();
        return folder;
    }
}
