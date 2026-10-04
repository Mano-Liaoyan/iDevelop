using IDevelop.Workflows;

namespace IDevelop.Projects;

/// <summary>
/// The open workflow of a project folder. A project keeps each workflow in
/// <c>.idp/workflows/&lt;workflow id&gt;.json</c>. This version opens one workflow per project.
/// </summary>
public sealed class WorkflowDocument
{
    private Workflow _saved;

    private WorkflowDocument(string projectFolder, string filePath, Workflow workflow)
    {
        ProjectFolder = projectFolder;
        FilePath = filePath;
        Current = workflow;
        _saved = workflow;
    }

    public string ProjectFolder { get; }

    public string FilePath { get; }

    public Workflow Current { get; private set; }

    public bool HasUnsavedChanges => !ReferenceEquals(Current, _saved);

    /// <summary>Raised after <see cref="Current"/> or the saved state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Opens any existing folder. A folder without a workflow file opens as an empty workflow
    /// and stays untouched until the first <see cref="Save"/>.
    /// </summary>
    /// <exception cref="ProjectException">The folder is missing, holds several workflows, or its workflow file is invalid.</exception>
    public static WorkflowDocument Open(string folder)
    {
        var projectFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(projectFolder))
        {
            throw new ProjectException($"The folder {projectFolder} does not exist.");
        }

        var workflowsFolder = DataFolder.Workflows(projectFolder);
        var files = WorkflowFiles(workflowsFolder);
        switch (files)
        {
            case []:
                var empty = Workflow.Empty(WorkflowId.New());
                return new WorkflowDocument(projectFolder, Path.Combine(workflowsFolder, $"{empty.Id}.json"), empty);
            case [var file]:
                return new WorkflowDocument(projectFolder, file, WorkflowFile.Parse(File.ReadAllBytes(file), file));
            default:
                throw new ProjectException(
                    $"{workflowsFolder} holds {files.Length} workflow files. This version of iDevelop opens one workflow per project.");
        }
    }

    /// <summary>An edit that is rejected or has no effect leaves the document unchanged and raises nothing.</summary>
    public EditResult Apply(WorkflowEdit edit)
    {
        var result = Current.Apply(edit);
        if (result is EditResult.Applied applied && !ReferenceEquals(applied.Workflow, Current))
        {
            Current = applied.Workflow;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>
    /// Writes <see cref="Current"/> atomically. On an I/O error the previous file stays intact,
    /// <see cref="HasUnsavedChanges"/> stays true, and the exception propagates.
    /// </summary>
    /// <exception cref="ProjectException">Another workflow file sits beside this one, so saving would leave a project that no longer opens.</exception>
    public void Save()
    {
        var snapshot = Current;
        var workflowsFolder = Path.GetDirectoryName(FilePath)!;
        if (WorkflowFiles(workflowsFolder).FirstOrDefault(file => file != FilePath) is { } other)
        {
            throw new ProjectException($"Not saved. {other} is another workflow file, and this version of iDevelop keeps one workflow per project.");
        }

        Directory.CreateDirectory(workflowsFolder);
        DataFolder.EnsureGitIgnore(ProjectFolder);
        AtomicFile.Replace(FilePath, WorkflowFile.Serialize(snapshot));
        _saved = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>In ordinal order, so a message names the same file each time.</summary>
    private static string[] WorkflowFiles(string workflowsFolder) =>
        Directory.Exists(workflowsFolder) ? [.. Directory.EnumerateFiles(workflowsFolder, "*.json").Order(StringComparer.Ordinal)] : [];
}

/// <summary>The message is for the user.</summary>
public sealed class ProjectException(string message, Exception? inner = null) : Exception(message, inner);
