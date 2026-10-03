using System.Runtime.Versioning;
using IDevelop.Projects;
using IDevelop.Workflows;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

public sealed class WorkflowDocumentTests : IDisposable
{
    private const string SampleFileName = "019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01.json";
    private const string A = "019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11";
    private const string B = "019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22";

    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "sample-project");
    private static readonly string SampleFile = Path.Combine(Sample, ".idp", "workflows", SampleFileName);
    private static readonly TaskId Design = new(Guid.Parse(A));
    private static readonly TaskId Build = new(Guid.Parse(B));
    private static readonly TaskId Review = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_sample_file_parses_and_serializes_back_to_identical_bytes()
    {
        var golden = File.ReadAllBytes(SampleFile);

        var workflow = WorkflowFile.Parse(golden, SampleFile);

        Assert.Equal(golden, WorkflowFile.Serialize(workflow));
    }

    [Fact]
    public void Opening_the_sample_restores_its_text_kinds_and_positions()
    {
        var document = WorkflowDocument.Open(_temp.CopyOf(Sample));

        var workflow = document.Current;
        Assert.Equal("Design the workflow file format", workflow.Tasks[Design].Title);
        Assert.Equal(
            "Propose a JSON layout for tasks, connections, and canvas positions.\n" +
            "Keep multi-line text readable in a plain text editor.\n" +
            "\n" +
            "Store no view state other than node positions.",
            workflow.Tasks[Design].Instructions);
        Assert.Equal(
            "A three-task sample opens in a text editor without escaped line breaks.\n" +
            "Chinese text such as 审查说明 stays readable.",
            workflow.Tasks[Design].AcceptanceCriteria);
        Assert.Equal("", workflow.Tasks[Review].Instructions);
        Assert.Equal(new CanvasPoint(720, 247.5), workflow.Positions[Review]);
        Assert.Equal(ConnectionKind.Context, workflow.Connections[new ConnectionKey(Design, Review)]);
        Assert.Equal(ConnectionKind.Review, workflow.Connections[new ConnectionKey(Build, Review)]);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void Saving_the_opened_sample_rewrites_identical_bytes()
    {
        var document = WorkflowDocument.Open(_temp.CopyOf(Sample));

        document.Save();

        Assert.Equal(File.ReadAllBytes(SampleFile), File.ReadAllBytes(document.FilePath));
    }

    [Fact]
    public void A_folder_without_a_data_folder_opens_empty_and_writes_nothing()
    {
        var folder = _temp.Create("repository");
        File.WriteAllText(Path.Combine(folder, "README.md"), "# Existing repository\n");

        var document = WorkflowDocument.Open(folder);

        Assert.Empty(document.Current.Tasks);
        Assert.False(document.HasUnsavedChanges);
        Assert.Equal(Path.Combine(folder, ".idp", "workflows", $"{document.Current.Id}.json"), document.FilePath);
        Assert.Equal(new[] { "README.md" }, Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void The_first_save_creates_the_data_folder_and_the_task_reopens()
    {
        var folder = _temp.Create("repository");
        var document = WorkflowDocument.Open(folder);
        document.Apply(new CreateTask(new TaskDefinition(Design) { Title = "Plan release" }, new CanvasPoint(40, 60)));
        Assert.True(document.HasUnsavedChanges);

        document.Save();

        Assert.False(document.HasUnsavedChanges);
        Assert.Equal("*.tmp\n", File.ReadAllText(Path.Combine(folder, ".idp", ".gitignore")));
        Assert.Equal(
            new[] { $"{document.Current.Id}.json" },
            Directory.EnumerateFiles(Path.Combine(folder, ".idp", "workflows")).Select(Path.GetFileName));
        var reopened = WorkflowDocument.Open(folder).Current;
        Assert.Equal(document.Current.Id, reopened.Id);
        Assert.Equal("Plan release", reopened.Tasks[Design].Title);
        Assert.Equal(new CanvasPoint(40, 60), reopened.Positions[Design]);
    }

    [Fact]
    public void A_failed_save_keeps_the_previous_file_and_the_unsaved_state()
    {
        var folder = _temp.CopyOf(Sample);
        var document = WorkflowDocument.Open(folder);
        document.Apply(new EditTask(Design, TaskField.Title, "Changed"));

        Exception? error;
        using (BlockReplacing(document.FilePath))
        {
            error = Record.Exception(document.Save);
        }

        Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected {error}");
        Assert.Equal(File.ReadAllBytes(SampleFile), File.ReadAllBytes(document.FilePath));
        Assert.True(document.HasUnsavedChanges);
        Assert.Equal(new[] { SampleFileName }, Directory.EnumerateFiles(Path.GetDirectoryName(document.FilePath)!).Select(Path.GetFileName));

        document.Save();

        Assert.False(document.HasUnsavedChanges);
        Assert.Equal("Changed", WorkflowDocument.Open(folder).Current.Tasks[Design].Title);
    }

    [Fact]
    public void A_leftover_temp_file_from_a_crashed_save_is_ignored_and_kept()
    {
        var folder = _temp.CopyOf(Sample);
        var leftover = Path.Combine(folder, ".idp", "workflows", $"{SampleFileName}.5f0c9b7d1a01.tmp");
        File.WriteAllText(leftover, "{\"form");

        var document = WorkflowDocument.Open(folder);

        Assert.Equal("Design the workflow file format", document.Current.Tasks[Design].Title);
        Assert.Equal("{\"form", File.ReadAllText(leftover));
    }

    [Fact]
    public void A_file_that_starts_with_a_utf8_byte_order_mark_opens_and_saves_without_it()
    {
        var folder = _temp.CopyOf(Sample);
        var file = Path.Combine(folder, ".idp", "workflows", SampleFileName);
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(SampleFile)]);

        var document = WorkflowDocument.Open(folder);

        Assert.Equal("Design the workflow file format", document.Current.Tasks[Design].Title);
        Assert.False(document.HasUnsavedChanges);
        document.Save();
        Assert.Equal(File.ReadAllBytes(SampleFile), File.ReadAllBytes(file));
    }

    [Fact]
    public void Saving_beside_another_workflow_file_is_refused_and_keeps_the_unsaved_state()
    {
        var folder = _temp.Create("repository");
        var document = WorkflowDocument.Open(folder);
        document.Apply(new CreateTask(new TaskDefinition(Design) { Title = "Plan release" }, new CanvasPoint(40, 60)));
        var workflows = Directory.CreateDirectory(Path.Combine(folder, ".idp", "workflows")).FullName;
        var other = Path.Combine(workflows, SampleFileName);
        File.Copy(SampleFile, other);

        var error = Assert.Throws<ProjectException>(document.Save);

        Assert.Equal($"Not saved. {other} is another workflow file, and this version of iDevelop keeps one workflow per project.", error.Message);
        Assert.True(document.HasUnsavedChanges);
        Assert.Equal(new[] { SampleFileName }, Directory.EnumerateFiles(workflows).Select(Path.GetFileName));
        Assert.Equal(File.ReadAllBytes(SampleFile), File.ReadAllBytes(other));
    }

    [Fact]
    public void A_project_with_two_workflow_files_is_refused()
    {
        var folder = _temp.CopyOf(Sample);
        var workflows = Path.Combine(folder, ".idp", "workflows");
        File.Copy(Path.Combine(workflows, SampleFileName), Path.Combine(workflows, "019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a02.json"));

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.Open(folder));

        Assert.Equal($"{workflows} holds 2 workflow files. This version of iDevelop opens one workflow per project.", error.Message);
    }

    [Fact]
    public void A_missing_folder_is_refused()
    {
        var folder = Path.Combine(_temp.Create("parent"), "missing");

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.Open(folder));

        Assert.Equal($"The folder {folder} does not exist.", error.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void An_invalid_file_fails_to_open_with_a_message(string json, string message)
    {
        Assert.Equal(message, OpenFailure(json));
    }

    public static TheoryData<string, string> InvalidFiles => new()
    {
        {
            "{'format': 'idevelop.workflow/1',",
            "<file> is not a valid workflow file. Expected start of a property name or value, but instead reached end of data. Path: $ | LineNumber: 0 | BytePositionInLine: 32."
        },
        {
            "{'format': 'idevelop.workflow/2', 'nodes': []}",
            "<file> has format \"idevelop.workflow/2\". This version of iDevelop reads idevelop.workflow/1."
        },
        {
            Workflow($"{{'id': '{A}', 'titel': 'Design', 'instructions': [], 'acceptanceCriteria': []}}", "", At(A)),
            "<file> is not a valid workflow file. The JSON property 'titel' could not be mapped to any .NET member contained in type 'IDevelop.Projects.WorkflowFile+TaskDto'."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'instructions': []}}", "", At(A)),
            "<file> is not a valid workflow file. JSON deserialization for type 'IDevelop.Projects.WorkflowFile+TaskDto' was missing required properties including: 'acceptanceCriteria'."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'title': 'Build', 'instructions': [], 'acceptanceCriteria': []}}", "", At(A)),
            "<file> is not a valid workflow file. Duplicate property 'title' encountered during deserialization of type 'IDevelop.Projects.WorkflowFile+TaskDto'."
        },
        {
            Workflow(Task(A, "Design"), "", ""),
            $"<file>: task {A} has no position in the layout."
        },
        {
            Workflow(Task(A, "Design"), "", $"{At(A)}, {At(B)}"),
            $"<file>: the layout has a position for {B}, which is not a task."
        },
        {
            Workflow($"{Task(A, "Design")}, {Task(B, "Build")}", Link(A, B, "blocks"), $"{At(A)}, {At(B)}"),
            $"<file>: connection {A} -> {B} has unknown kind \"blocks\"."
        },
        {
            Workflow($"{Task(A, "Design")}, {Task(A, "Again")}", "", At(A)),
            $"<file>: task {A} repeats an earlier task."
        },
        {
            Workflow($"{Task(A, "Design")}, null", "", At(A)),
            "<file>: tasks[1] is null."
        },
        {
            Workflow($"{Task(A, "Design")}, {Task(B, "Build")}", "null", $"{At(A)}, {At(B)}"),
            "<file>: connections[0] is null."
        },
        {
            Workflow(Task(A, "Design"), "", $"'{A}': null"),
            $"<file>: task {A} has a null position in the layout."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'instructions': ['Draft', null], 'acceptanceCriteria': []}}", "", At(A)),
            $"<file>: task {A} has a null line in instructions."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'instructions': [], 'acceptanceCriteria': [null]}}", "", At(A)),
            $"<file>: task {A} has a null line in acceptanceCriteria."
        },
    };

    [Fact]
    public void A_file_whose_graph_breaks_a_rule_fails_with_the_rule()
    {
        var json = Workflow(
            $"{Task(A, "Design")}, {Task(B, "Build")}",
            $"{Link(A, B, "dependency")}, {Link(B, A, "review")}",
            $"{At(A)}, {At(B)}");

        Assert.Equal($"<file>: connection {B} -> {A} would close a cycle: Build -> Design -> Build.", OpenFailure(json));
    }

    private string OpenFailure(string json)
    {
        var folder = _temp.Create("invalid");
        var file = Path.Combine(folder, ".idp", "workflows", SampleFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json.Replace('\'', '"'));

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.Open(folder));

        return error.Message.Replace(file, "<file>");
    }

    private static string Workflow(string tasks, string connections, string layout) =>
        $"{{'format': 'idevelop.workflow/1', 'id': '019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01', 'tasks': [{tasks}], 'connections': [{connections}], 'layout': {{{layout}}}}}";

    private static string Task(string id, string title) =>
        $"{{'id': '{id}', 'title': '{title}', 'instructions': [], 'acceptanceCriteria': []}}";

    private static string Link(string from, string to, string kind) => $"{{'from': '{from}', 'to': '{to}', 'kind': '{kind}'}}";

    private static string At(string id) => $"'{id}': {{'x': 0, 'y': 0}}";

    private static IDisposable BlockReplacing(string file) => OperatingSystem.IsWindows()
        ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)
        : MakeReadOnly(Path.GetDirectoryName(file)!);

    [UnsupportedOSPlatform("windows")]
    private static IDisposable MakeReadOnly(string folder)
    {
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() => File.SetUnixFileMode(folder, mode));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
