using IDevelop.Workflows;

namespace IDevelop.Projects;

/// <summary>
/// The open workflow of a project folder. A project keeps each workflow in
/// <c>.idp/workflows/&lt;workflow id&gt;.json</c>. This version opens one workflow per project.
/// </summary>
public sealed class WorkflowDocument
{
    /// <summary>Null while the file holds an older format than <see cref="Save"/> writes.</summary>
    private Workflow? _saved;

    private readonly Stack<Workflow> _undo = new();

    private readonly Stack<Workflow> _redo = new();

    /// <summary>The edit that made <see cref="Current"/>, or null after an undo, a redo, or a save, so the next edit starts its own step.</summary>
    private WorkflowEdit? _lastEdit;

    private WorkflowDocument(string projectFolder, string filePath, Workflow workflow, string? converted = null)
    {
        ProjectFolder = projectFolder;
        FilePath = filePath;
        Current = workflow;
        _saved = converted is null ? workflow : null;
        Converted = converted;
    }

    public string ProjectFolder { get; }

    public string FilePath { get; }

    public Workflow Current { get; private set; }

    /// <summary>Compares with the saved workflow, so undoing back to it reads as saved.</summary>
    public bool HasUnsavedChanges => !ReferenceEquals(Current, _saved);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>What opening converted from an older file format, for the user, or null. The first save writes the current format.</summary>
    public string? Converted { get; }

    /// <summary>Raised after <see cref="Current"/>, the saved state, <see cref="CanUndo"/>, or <see cref="CanRedo"/> changes.</summary>
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
                var parsed = WorkflowFile.Parse(File.ReadAllBytes(file), file);
                return new WorkflowDocument(projectFolder, file, parsed.Workflow, parsed.Converted);
            default:
                throw new ProjectException(
                    $"{workflowsFolder} holds {files.Length} workflow files. This version of iDevelop opens one workflow per project.");
        }
    }

    /// <summary>
    /// An edit that is rejected or has no effect leaves the document unchanged and raises nothing. Each other edit is one
    /// undo step and clears the redo steps, except that a title or field edit continuing the previous edit of the same text
    /// joins its step, so a run of typing undoes at once.
    /// </summary>
    public EditResult Apply(WorkflowEdit edit)
    {
        var result = Current.Apply(edit);
        if (result is EditResult.Applied applied && !ReferenceEquals(applied.Workflow, Current))
        {
            if (!EditsSameText(_lastEdit, edit))
            {
                _undo.Push(Current);
            }

            _redo.Clear();
            _lastEdit = edit;
            Current = applied.Workflow;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>Restores the workflow before the latest undo step. Does nothing when there is none.</summary>
    public void Undo() => Step(_undo, _redo);

    /// <summary>Applies the latest undone step again. Does nothing when there is none.</summary>
    public void Redo() => Step(_redo, _undo);

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
        _lastEdit = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Step(Stack<Workflow> from, Stack<Workflow> to)
    {
        if (from.TryPop(out var workflow))
        {
            to.Push(Current);
            Current = workflow;
            _lastEdit = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool EditsSameText(WorkflowEdit? previous, WorkflowEdit edit) => (previous, edit) switch
    {
        (WorkflowEdit.EditTitle p, WorkflowEdit.EditTitle e) => p.Task == e.Task,
        (WorkflowEdit.SetField p, WorkflowEdit.SetField e) => p.Task == e.Task && p.Key == e.Key,
        _ => false,
    };

    /// <summary>In ordinal order, so a message names the same file each time.</summary>
    private static string[] WorkflowFiles(string workflowsFolder) =>
        Directory.Exists(workflowsFolder) ? [.. Directory.EnumerateFiles(workflowsFolder, "*.json").Order(StringComparer.Ordinal)] : [];
}

/// <summary>The message is for the user.</summary>
public sealed class ProjectException(string message, Exception? inner = null) : Exception(message, inner);
