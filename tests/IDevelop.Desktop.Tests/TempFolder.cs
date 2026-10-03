using IDevelop.Projects;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

internal sealed class TempFolder : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "idevelop-tests", Guid.NewGuid().ToString("N"));

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
