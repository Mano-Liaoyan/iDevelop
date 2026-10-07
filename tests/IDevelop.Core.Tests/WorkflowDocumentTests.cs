using System.Runtime.Versioning;
using System.Text;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.TestTasks;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

public sealed class WorkflowDocumentTests : IDisposable
{
    private const string SampleFileName = "019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01.json";

    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "sample-project");
    private static readonly string SampleFile = Path.Combine(Sample, ".idp", "workflows", SampleFileName);
    private static readonly string A = Design.ToString();
    private static readonly string B = Build.ToString();

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_sample_file_parses_and_serializes_back_to_identical_bytes()
    {
        var golden = File.ReadAllBytes(SampleFile);

        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();
        File.Delete(document.FilePath);
        document.Save();

        Assert.Null(document.Converted);
        Assert.Null(document.Current.Name);
        Assert.Equal(golden, File.ReadAllBytes(document.FilePath));
    }

    [Fact]
    public void A_format_2_file_opens_converted_says_so_saves_as_format_3_and_reopens()
    {
        var folder = _temp.Create("format2");
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, ".idp", "workflows")).FullName, SampleFileName);
        File.Copy(Fixture.Path("storage-change-format2.json"), file);

        var document = WorkflowDocument.OpenProject(folder).Single();

        Assert.Equal(
            "iDevelop converted this workflow from format 2. Its 3 tasks became Implement nodes, and its review connection became a dependency. Save to keep it in format 3.",
            document.Converted);
        Assert.True(document.HasUnsavedChanges);
        Assert.Null(document.Current.Name);
        Assert.All(document.Current.Tasks.Values, task => Assert.Same(BuiltInBlueprints.Implement, task.Blueprint));
        Assert.Equal(ConnectionKind.Dependency, document.Current.Connections[new ConnectionKey(Build, Review)]);

        document.Save();
        var reopened = WorkflowDocument.OpenProject(folder).Single();

        Assert.Null(reopened.Converted);
        Assert.False(reopened.HasUnsavedChanges);
        Assert.Equal(File.ReadAllBytes(SampleFile), File.ReadAllBytes(file));
        Assert.Equal(document.Current.Tasks.Values, reopened.Current.Tasks.Values);
        Assert.Equal(document.Current.Connections, reopened.Current.Connections);
        Assert.Equal(document.Current.Positions, reopened.Current.Positions);
    }

    [Fact]
    public void A_format_2_file_without_tasks_says_only_that_it_converted()
    {
        var parsed = WorkflowFile.Parse(Encoding.UTF8.GetBytes(Workflow("", "", "").Replace('\'', '"')), SampleFile);

        Assert.Equal("iDevelop converted this workflow from format 2. Save to keep it in format 3.", parsed.Converted);
        Assert.Empty(parsed.Workflow.Tasks);
    }

    [Fact]
    public void Opening_the_sample_restores_its_text_kinds_and_positions()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();

        var workflow = document.Current;
        Assert.Equal("Design the workflow file format", workflow.Tasks[Design].Title);
        Assert.Equal(
            "Propose a JSON layout for tasks, connections, and canvas positions.\n" +
            "Keep multi-line text readable in a plain text editor.\n" +
            "\n" +
            "Store no view state other than node positions.",
            workflow.Tasks[Design].Field("instructions"));
        Assert.Equal(
            "A three-task sample opens in a text editor without escaped line breaks.\n" +
            "Chinese text such as 审查说明 stays readable.",
            workflow.Tasks[Design].Field("acceptanceCriteria"));
        Assert.Equal("", workflow.Tasks[Review].Field("instructions"));
        Assert.Equal(new CanvasPoint(720, 247.5), workflow.Positions[Review]);
        Assert.Equal(ConnectionKind.Context, workflow.Connections[new ConnectionKey(Design, Review)]);
        Assert.Equal(ConnectionKind.Dependency, workflow.Connections[new ConnectionKey(Build, Review)]);
        Assert.Equal([BuiltInBlueprints.Implement], workflow.Blueprints.Values);
        Assert.Equal(new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "high" }, workflow.Tasks[Design].Execution);
        Assert.Equal(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "medium" }, workflow.Tasks[Build].Execution);
        Assert.Null(workflow.Tasks[Review].Execution);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void A_folder_without_a_data_folder_opens_empty_and_writes_nothing()
    {
        var folder = _temp.Create("repository");
        File.WriteAllText(Path.Combine(folder, "README.md"), "# Existing repository\n");

        var document = WorkflowDocument.OpenProject(folder).Single();

        Assert.Empty(document.Current.Tasks);
        Assert.False(document.HasUnsavedChanges);
        Assert.Equal(Path.Combine(folder, ".idp", "workflows", $"{document.Current.Id}.json"), document.FilePath);
        Assert.Equal(new[] { "README.md" }, Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void The_first_save_creates_the_data_folder_and_the_task_reopens()
    {
        var folder = _temp.Create("repository");
        var document = WorkflowDocument.OpenProject(folder).Single();
        document.Apply(TestNodes.Place(TestNodes.Implement(Design, "Plan release"), new CanvasPoint(40, 60)));
        Assert.True(document.HasUnsavedChanges);

        document.Save();

        Assert.False(document.HasUnsavedChanges);
        Assert.Equal("*.tmp\nattempts/\nruns/\n", File.ReadAllText(Path.Combine(folder, ".idp", ".gitignore")));
        Assert.Equal(
            new[] { $"{document.Current.Id}.json" },
            Directory.EnumerateFiles(Path.Combine(folder, ".idp", "workflows")).Select(Path.GetFileName));
        var reopened = WorkflowDocument.OpenProject(folder).Single().Current;
        Assert.Equal(document.Current.Id, reopened.Id);
        Assert.Equal("Plan release", reopened.Tasks[Design].Title);
        Assert.Equal(new CanvasPoint(40, 60), reopened.Positions[Design]);
    }

    [Fact]
    public void Saving_keeps_the_users_ignore_lines_and_adds_the_missing_ones()
    {
        var folder = _temp.CopyOf(Sample);
        var gitignore = Path.Combine(folder, ".idp", ".gitignore");
        File.WriteAllText(gitignore, "# mine\r\n*.tmp\r\nnotes.md");
        var document = WorkflowDocument.OpenProject(folder).Single();
        document.Apply(new EditTitle(Design, "Changed"));

        document.Save();
        document.Apply(new EditTitle(Design, "Changed again"));
        document.Save();

        Assert.Equal("# mine\r\n*.tmp\r\nnotes.md\nattempts/\nruns/\n", File.ReadAllText(gitignore));
    }

    [Fact]
    public void A_failed_save_keeps_the_previous_file_and_the_unsaved_state()
    {
        var folder = _temp.CopyOf(Sample);
        var document = WorkflowDocument.OpenProject(folder).Single();
        document.Apply(new EditTitle(Design, "Changed"));

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
        Assert.Equal("Changed", WorkflowDocument.OpenProject(folder).Single().Current.Tasks[Design].Title);
    }

    [Fact]
    public void A_leftover_temp_file_from_a_crashed_save_is_ignored_and_kept()
    {
        var folder = _temp.CopyOf(Sample);
        var leftover = Path.Combine(folder, ".idp", "workflows", $"{SampleFileName}.5f0c9b7d1a01.tmp");
        File.WriteAllText(leftover, "{\"form");

        var document = WorkflowDocument.OpenProject(folder).Single();

        Assert.Equal("Design the workflow file format", document.Current.Tasks[Design].Title);
        Assert.Equal("{\"form", File.ReadAllText(leftover));
    }

    [Fact]
    public void A_file_that_starts_with_a_utf8_byte_order_mark_opens_and_saves_without_it()
    {
        var folder = _temp.CopyOf(Sample);
        var file = Path.Combine(folder, ".idp", "workflows", SampleFileName);
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(SampleFile)]);

        var document = WorkflowDocument.OpenProject(folder).Single();

        Assert.Equal("Design the workflow file format", document.Current.Tasks[Design].Title);
        Assert.False(document.HasUnsavedChanges);
        document.Save();
        Assert.Equal(File.ReadAllBytes(SampleFile), File.ReadAllBytes(file));
    }

    [Fact]
    public void A_missing_folder_is_refused()
    {
        var folder = Path.Combine(_temp.Create("parent"), "missing");

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.OpenProject(folder).Single());

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
            "{'format': 'idevelop.workflow/2',",
            "<file> is not a valid workflow file. Expected start of a property name or value, but instead reached end of data. Path: $ | LineNumber: 0 | BytePositionInLine: 32."
        },
        {
            "{'format': 'idevelop.workflow/4', 'nodes': []}",
            "<file> has format \"idevelop.workflow/4\". This version of iDevelop reads idevelop.workflow/3 and idevelop.workflow/2."
        },
        {
            $"{{'format': 'idevelop.workflow/1', 'id': '019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01', 'tasks': [{{'id': '{A}', 'title': 'Design', 'instructions': [], 'acceptanceCriteria': []}}], 'connections': [], 'layout': {{{At(A)}}}}}",
            "<file> has format \"idevelop.workflow/1\". This version of iDevelop reads idevelop.workflow/3 and idevelop.workflow/2."
        },
        {
            Workflow($"{{'id': '{A}', 'titel': 'Design', 'instructions': [], 'acceptanceCriteria': [], 'execution': null}}", "", At(A)),
            "<file> is not a valid workflow file. The JSON property 'titel' could not be mapped to any .NET member contained in type 'IDevelop.Projects.WorkflowFile+TaskDtoV2'."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'instructions': [], 'execution': null}}", "", At(A)),
            "<file> is not a valid workflow file. JSON deserialization for type 'IDevelop.Projects.WorkflowFile+TaskDtoV2' was missing required properties including: 'acceptanceCriteria'."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'title': 'Build', 'instructions': [], 'acceptanceCriteria': [], 'execution': null}}", "", At(A)),
            "<file> is not a valid workflow file. Duplicate property 'title' encountered during deserialization of type 'IDevelop.Projects.WorkflowFile+TaskDtoV2'."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'instructions': [], 'acceptanceCriteria': []}}", "", At(A)),
            "<file> is not a valid workflow file. JSON deserialization for type 'IDevelop.Projects.WorkflowFile+TaskDtoV2' was missing required properties including: 'execution'."
        },
        {
            Workflow(Task(A, "Design", "{'client': 'cursor', 'model': null, 'reasoning': null}"), "", At(A)),
            $"<file>: task {A} names unknown agent \"cursor\"."
        },
        {
            Workflow(Task(A, "Design", "{'client': 'codex', 'model': 'gpt-6-sol'}"), "", At(A)),
            "<file> is not a valid workflow file. JSON deserialization for type 'IDevelop.Projects.ExecutionDto' was missing required properties including: 'reasoning'."
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
            Workflow($"{{'id': '{A}', 'title': 'Design', 'instructions': ['Draft', null], 'acceptanceCriteria': [], 'execution': null}}", "", At(A)),
            $"<file>: task {A} has a null line in instructions."
        },
        {
            Workflow($"{{'id': '{A}', 'title': 'Design', 'instructions': [], 'acceptanceCriteria': [null], 'execution': null}}", "", At(A)),
            $"<file>: task {A} has a null line in acceptanceCriteria."
        },
    };

    [Theory]
    [MemberData(nameof(InvalidFormat3Files))]
    public void An_invalid_format_3_file_fails_to_open_with_a_message(string json, string message)
    {
        Assert.Equal(message, OpenFailure(json));
    }

    public static TheoryData<string, string> InvalidFormat3Files => new()
    {
        {
            Format3(Implement, Node(A, "team.spec@1")),
            $"<file>: task {A} names blueprint \"team.spec@1\", which the file does not hold."
        },
        {
            Format3($"{Implement}, {Blueprint("team.spec", "'Write it.'")}", Node(A)),
            "<file>: no task uses blueprint team.spec@1."
        },
        {
            Format3(Implement, Node(A, fields: "'instructions': [], 'acceptanceCriteria': [], 'goal': []")),
            $"<file>: task {A} has a value for goal, which its blueprint has no field for."
        },
        {
            Format3(Implement, Node(A, conversation: "sometimes")),
            $"<file>: task {A} has unknown conversation mode \"sometimes\"."
        },
        {
            Format3(Blueprint("team.spec", "'{{#goal}}Write it.'"), Node(A, "team.spec@1", "")),
            "<file>: blueprint team.spec@1 has a template iDevelop cannot read. {{#goal}} has no {{/goal}}."
        },
        {
            Format3(Blueprint("team.spec", "'Write {{topic}}.'"), Node(A, "team.spec@1", "")),
            "<file>: Blueprint team.spec@1's template reads {{topic}}, which is not a field."
        },
        {
            Format3($"{Implement}, {Implement}", Node(A)),
            "<file>: blueprint idevelop.implement@1 appears twice."
        },
        {
            Format3(Implement, Node(A, fields: "'instructions': []")),
            $"<file>: task {A} has no value for acceptanceCriteria."
        },
        {
            Format3(Implement.Replace("'name': 'Implement'", "'name': 'Do it'"), Node(A)),
            "<file>: blueprint idevelop.implement@1 differs from the built-in it names."
        },
    };

    [Fact]
    public void Review_and_approval_nodes_save_their_built_ins_and_reopen_the_same()
    {
        var folder = _temp.Create("review");
        var document = WorkflowDocument.OpenProject(folder).Single();
        var review = new TaskId(Guid.Parse("019a9d2e-5f00-7000-8000-000000000066"));
        var approval = new TaskId(Guid.Parse("019a9d2e-6000-7000-8000-000000000077"));
        WorkflowEdit[] edits =
        [
            TestNodes.Place(TestNodes.Implement(TestTasks.Design, "Build", "Build it."), new CanvasPoint(0, 0)),
            new WorkflowEdit.PlaceNode(review, BuiltInBlueprints.Review, new CanvasPoint(300, 0)) { Title = "Review" },
            new WorkflowEdit.PlaceNode(approval, BuiltInBlueprints.Approval, new CanvasPoint(600, 0)) { Title = "Approve" },
            new WorkflowEdit.Connect(new ConnectionKey(TestTasks.Design, review), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(review, approval), ConnectionKind.Dependency),
        ];
        foreach (var edit in edits)
        {
            Assert.IsType<EditResult.Applied>(document.Apply(edit));
        }

        document.Save();
        var text = File.ReadAllText(Directory.GetFiles(Path.Combine(folder, ".idp", "workflows")).Single());
        var reopened = WorkflowDocument.OpenProject(folder).Single().Current;

        Assert.Contains("\"kind\": \"review\",\n        \"reviewer\": [", text);
        Assert.Contains("\"work\": {\n        \"kind\": \"person\"\n      }", text);
        Assert.Equal([BuiltInBlueprints.Approval, BuiltInBlueprints.Implement, BuiltInBlueprints.Review], reopened.Blueprints.Values);
        Assert.Equal(TestTasks.Design, reopened.SubjectOf(review));
    }

    [Fact]
    public void A_file_whose_graph_breaks_a_rule_fails_with_the_rule()
    {
        var json = Workflow(
            $"{Task(A, "Design")}, {Task(B, "Build")}",
            $"{Link(A, B, "dependency")}, {Link(B, A, "review")}",
            $"{At(A)}, {At(B)}");

        Assert.Equal($"<file>: connection {B} -> {A} would close a cycle: Build -> Design -> Build.", OpenFailure(json));
    }

    [Fact]
    public void Undoing_a_delete_restores_the_node_and_its_connections()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();
        var changes = 0;
        document.Changed += (_, _) => changes++;
        document.Apply(new Delete([Review], []));
        Assert.Equal([new ConnectionKey(Design, Build)], document.Current.Connections.Keys);

        document.Undo();

        Assert.Equal("Review the storage change", document.Current.Tasks[Review].Title);
        Assert.Equal(new CanvasPoint(720, 247.5), document.Current.Positions[Review]);
        Assert.Equal(3, document.Current.Connections.Count);
        Assert.Equal(ConnectionKind.Context, document.Current.Connections[new ConnectionKey(Design, Review)]);
        Assert.Equal(ConnectionKind.Dependency, document.Current.Connections[new ConnectionKey(Build, Review)]);
        Assert.False(document.HasUnsavedChanges);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Redo_deletes_the_node_again_until_a_new_edit_drops_the_redo_step()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();
        document.Apply(new Delete([Review], []));
        document.Undo();

        document.Redo();

        Assert.Equal([Design, Build], document.Current.Tasks.Keys.Order());
        Assert.Equal([new ConnectionKey(Design, Build)], document.Current.Connections.Keys);
        Assert.True(document.HasUnsavedChanges);
        Assert.False(document.CanRedo);

        document.Undo();
        document.Apply(new EditTitle(Design, "Changed"));
        document.Redo();

        Assert.False(document.CanRedo);
        Assert.Equal("Changed", document.Current.Tasks[Design].Title);
        Assert.Equal("Review the storage change", document.Current.Tasks[Review].Title);
    }

    [Fact]
    public void Ten_characters_typed_into_a_field_undo_as_one_step()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();

        Type(document, Review, "instructions", "Read diffs");
        Assert.Equal("Read diffs", document.Current.Tasks[Review].Field("instructions"));
        document.Undo();

        Assert.Equal("", document.Current.Tasks[Review].Field("instructions"));
        Assert.False(document.CanUndo);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void Typing_again_after_an_undo_starts_a_new_step()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();
        Type(document, Review, "instructions", "Read");
        document.Undo();
        Type(document, Review, "instructions", "Skim");

        document.Undo();
        Assert.Equal("", document.Current.Tasks[Review].Field("instructions"));

        document.Redo();
        Assert.Equal("Skim", document.Current.Tasks[Review].Field("instructions"));
    }

    [Fact]
    public void A_move_between_two_runs_of_typing_splits_them_into_three_steps()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();
        Type(document, Review, "instructions", "Read");
        document.Apply(new MoveTasks([new TaskPosition(Review, new CanvasPoint(800, 300))]));
        Type(document, Review, "instructions", " diffs");

        document.Undo();
        Assert.Equal("Read", document.Current.Tasks[Review].Field("instructions"));
        Assert.Equal(new CanvasPoint(800, 300), document.Current.Positions[Review]);

        document.Undo();
        Assert.Equal("Read", document.Current.Tasks[Review].Field("instructions"));
        Assert.Equal(new CanvasPoint(720, 247.5), document.Current.Positions[Review]);

        document.Undo();
        Assert.Equal("", document.Current.Tasks[Review].Field("instructions"));
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void Typing_into_a_title_then_a_field_then_the_same_field_of_another_task_undoes_as_three_steps()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();
        Type(document, Review, null, " again");
        Type(document, Review, "instructions", "Read");
        Type(document, Build, "instructions", "!");

        document.Undo();
        Assert.Equal("Read", document.Current.Tasks[Review].Field("instructions"));
        Assert.Equal("Write the file to a temporary sibling, flush it to disk, then rename it over the target.", document.Current.Tasks[Build].Field("instructions"));

        document.Undo();
        Assert.Equal("Review the storage change again", document.Current.Tasks[Review].Title);
        Assert.Equal("", document.Current.Tasks[Review].Field("instructions"));

        document.Undo();
        Assert.Equal("Review the storage change", document.Current.Tasks[Review].Title);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void Undoing_back_to_the_saved_workflow_reads_as_saved()
    {
        var folder = _temp.CopyOf(Sample);
        var document = WorkflowDocument.OpenProject(folder).Single();
        Type(document, Review, "instructions", "Read");
        document.Save();
        Type(document, Review, "instructions", " diffs");

        document.Undo();

        Assert.Equal("Read", document.Current.Tasks[Review].Field("instructions"));
        Assert.False(document.HasUnsavedChanges);

        document.Undo();

        Assert.Equal("", document.Current.Tasks[Review].Field("instructions"));
        Assert.True(document.HasUnsavedChanges);
        Assert.Equal("Read", WorkflowDocument.OpenProject(folder).Single().Current.Tasks[Review].Field("instructions"));
    }

    [Fact]
    public void Undo_and_redo_without_a_step_and_edits_that_change_nothing_leave_the_document_alone()
    {
        var document = WorkflowDocument.OpenProject(_temp.CopyOf(Sample)).Single();
        var opened = document.Current;
        var changes = 0;
        document.Changed += (_, _) => changes++;

        document.Undo();
        document.Redo();
        document.Apply(new EditTitle(Design, "Design the workflow file format"));
        Assert.IsType<EditResult.Rejected>(document.Apply(new SetField(Design, "goal", "Ship")));

        Assert.Same(opened, document.Current);
        Assert.False(document.CanUndo);
        Assert.Equal(0, changes);

        document.Apply(new EditTitle(Design, "Changed"));
        document.Undo();

        Assert.Same(opened, document.Current);
        Assert.Equal(2, changes);
    }

    /// <summary>Applies one edit per character, as the inspector does while the person types. A null key types into the title.</summary>
    private static void Type(WorkflowDocument document, TaskId task, string? key, string text)
    {
        var start = key is null ? document.Current.Tasks[task].Title : document.Current.Tasks[task].Field(key);
        for (var length = 1; length <= text.Length; length++)
        {
            WorkflowEdit edit = key is null ? new EditTitle(task, start + text[..length]) : new SetField(task, key, start + text[..length]);
            Assert.IsType<EditResult.Applied>(document.Apply(edit));
        }
    }

    private string OpenFailure(string json)
    {
        var folder = _temp.Create("invalid");
        var file = Path.Combine(folder, ".idp", "workflows", SampleFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json.Replace('\'', '"'));

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.OpenProject(folder).Single());

        return error.Message.Replace(file, "<file>");
    }

    private static readonly string Implement = Encoding.UTF8.GetString(WorkflowFile.Serialize(IDevelop.Workflows.Workflow.Empty(WorkflowId.New())
        .Apply(TestNodes.Place(TestNodes.Implement(Design), new CanvasPoint(0, 0))) is EditResult.Applied applied ? applied.Workflow : null!))
        .Split("\"blueprints\": [")[1].Split("],\n  \"tasks\"")[0].Replace('"', '\'');

    private static string Blueprint(string id, string template) =>
        $"{{'id': '{id}', 'version': 1, 'name': 'Spec', 'description': [], 'derivedFrom': null, " +
        $"'work': {{'kind': 'agent', 'access': 'readOnly', 'proposes': false, 'template': [{template}]}}, " +
        "'fields': [], 'defaults': {'execution': null, 'conversation': 'autonomous'}}";

    private static string Node(string id, string blueprint = "idevelop.implement@1", string fields = "'instructions': [], 'acceptanceCriteria': []", string conversation = "autonomous") =>
        $"{{'id': '{id}', 'title': 'Design', 'blueprint': '{blueprint}', 'fields': {{{fields}}}, 'execution': null, 'conversation': '{conversation}'}}";

    private static string Format3(string blueprints, string tasks) =>
        $"{{'format': 'idevelop.workflow/3', 'id': '019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01', 'blueprints': [{blueprints}], 'tasks': [{tasks}], 'connections': [], 'layout': {{{At(A)}}}}}";

    private static string Workflow(string tasks, string connections, string layout) =>
        $"{{'format': 'idevelop.workflow/2', 'id': '019a9d2e-4c10-7a3b-8e21-5f0c9b7d1a01', 'tasks': [{tasks}], 'connections': [{connections}], 'layout': {{{layout}}}}}";

    private static string Task(string id, string title, string execution = "null") =>
        $"{{'id': '{id}', 'title': '{title}', 'instructions': [], 'acceptanceCriteria': [], 'execution': {execution}}}";

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
