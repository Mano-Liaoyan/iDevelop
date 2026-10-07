using System.Text.Json.Nodes;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

public sealed class WorkspaceDocumentTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Two_workflows_open_in_display_order_with_their_own_tasks_and_saving_one_preserves_the_other()
    {
        var folder = _temp.Create("project");
        var release = WorkflowDocument.Create(folder, "Release");
        release.Apply(TestNodes.Place(TestNodes.Implement(TestTasks.Build, "Ship"), new CanvasPoint(0, 0)));
        release.Save();
        var build = WorkflowDocument.Create(folder, "build");
        build.Apply(TestNodes.Place(TestNodes.Implement(TestTasks.Design, "Implement"), new CanvasPoint(20, 40)));
        build.Save();
        var releaseBytes = File.ReadAllBytes(release.FilePath);

        var documents = WorkflowDocument.OpenProject(folder);

        Assert.Equal(["build", "Release"], documents.Select(document => document.Current.Name));
        Assert.Equal(["Implement", "Ship"], documents.Select(document => document.Current.Tasks.Values.Single().Title));
        Assert.Equal([TestTasks.Design], documents[0].Current.Tasks.Keys);
        Assert.Equal([TestTasks.Build], documents[1].Current.Tasks.Keys);
        documents[0].Apply(new EditTitle(TestTasks.Design, "Implemented"));
        documents[0].Save();
        Assert.Equal(releaseBytes, File.ReadAllBytes(release.FilePath));
        Assert.Equal("Implemented", WorkflowDocument.OpenProject(folder)[0].Current.Tasks[TestTasks.Design].Title);
    }

    [Fact]
    public void Display_order_uses_ordinal_ignore_case_then_id_and_the_unnamed_display_name()
    {
        var folder = _temp.Create("project");
        WriteEmpty(folder, "4", "00000000-0000-0000-0000-000000000004", null);
        WriteEmpty(folder, "3", "00000000-0000-0000-0000-000000000003", "build");
        WriteEmpty(folder, "2", "00000000-0000-0000-0000-000000000002", "Build");
        WriteEmpty(folder, "1", "00000000-0000-0000-0000-000000000001", "Zulu");

        var documents = WorkflowDocument.OpenProject(folder);

        Assert.Equal(
            ["00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000003", "00000000-0000-0000-0000-000000000004", "00000000-0000-0000-0000-000000000001"],
            documents.Select(document => document.Current.Id.ToString()));
        Assert.Equal(["Build", "build", null, "Zulu"], documents.Select(document => document.Current.Name));
    }

    [Fact]
    public void Equal_workflow_names_and_task_titles_in_two_projects_open_independently()
    {
        var firstFolder = _temp.Create("first");
        var secondFolder = _temp.Create("second");
        foreach (var folder in new[] { firstFolder, secondFolder })
        {
            var document = WorkflowDocument.Create(folder, "Build");
            document.Apply(TestNodes.Place(TestNodes.Implement(TestTasks.Build, "Implement"), new CanvasPoint(0, 0)));
            document.Save();
        }

        var first = WorkflowDocument.OpenProject(firstFolder).Single();
        var second = WorkflowDocument.OpenProject(secondFolder).Single();
        first.Apply(new Rename("Release"));
        first.Apply(new EditTitle(TestTasks.Build, "Ship"));
        first.Save();

        Assert.Equal("Release", WorkflowDocument.OpenProject(firstFolder).Single().Current.Name);
        Assert.Equal("Ship", WorkflowDocument.OpenProject(firstFolder).Single().Current.Tasks[TestTasks.Build].Title);
        Assert.Equal("Build", second.Current.Name);
        Assert.Equal("Implement", WorkflowDocument.OpenProject(secondFolder).Single().Current.Tasks[TestTasks.Build].Title);
        Assert.NotEqual(first.Current.Id, second.Current.Id);
    }

    [Fact]
    public void Duplicate_workflow_ids_refuse_the_project_and_name_both_files()
    {
        var folder = _temp.Create("project");
        const string id = "00000000-0000-0000-0000-000000000001";
        var first = WriteEmpty(folder, "first", id, "Build");
        var second = WriteEmpty(folder, "second", id, "Release");

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.OpenProject(folder));

        Assert.Equal($"{first} and {second} share workflow id {id}.", error.Message);
    }

    [Fact]
    public void A_task_id_shared_by_two_workflows_refuses_the_project_and_names_both_files()
    {
        var folder = _temp.Create("project");
        var first = WorkflowDocument.Create(folder, "Build");
        first.Apply(TestNodes.Place(TestNodes.Implement(TestTasks.Build, "Implement"), new CanvasPoint(0, 0)));
        first.Save();
        var second = WorkflowDocument.Create(folder, "Release");
        second.Apply(TestNodes.Place(TestNodes.Implement(TestTasks.Build, "Ship"), new CanvasPoint(0, 0)));
        second.Save();

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.OpenProject(folder));

        Assert.Contains(first.FilePath, error.Message);
        Assert.Contains(second.FilePath, error.Message);
        Assert.EndsWith($"share task id {TestTasks.Build}.", error.Message);
    }

    [Fact]
    public void Any_invalid_sibling_refuses_the_whole_project()
    {
        var folder = _temp.Create("project");
        WriteEmpty(folder, "first", "00000000-0000-0000-0000-000000000001", "Build");
        var invalid = Path.Combine(folder, ".idp", "workflows", "second.json");
        File.WriteAllText(invalid, "{}");

        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.OpenProject(folder));

        Assert.Equal($"{invalid} has format \"\". This version of iDevelop reads idevelop.workflow/3 and idevelop.workflow/2.", error.Message);
    }

    [Fact]
    public void Create_is_fresh_unsaved_and_its_first_save_adds_a_file_beside_the_existing_workflow()
    {
        var folder = _temp.Create("project");
        var existing = WorkflowDocument.OpenProject(folder).Single();
        existing.Save();
        var before = File.ReadAllBytes(existing.FilePath);

        var created = WorkflowDocument.Create(folder, "  Release  ");

        Assert.NotEqual(existing.Current.Id, created.Current.Id);
        Assert.NotEqual(created.Current.Id, WorkflowDocument.Create(folder, null).Current.Id);
        Assert.Equal("Release", created.Current.Name);
        Assert.True(created.HasUnsavedChanges);
        Assert.False(created.CanUndo);
        Assert.False(File.Exists(created.FilePath));
        Assert.Equal(Path.Combine(folder, ".idp", "workflows", $"{created.Current.Id}.json"), created.FilePath);
        created.Save();
        Assert.False(created.HasUnsavedChanges);
        Assert.Equal(before, File.ReadAllBytes(existing.FilePath));
        Assert.Equal([created.Current.Id, existing.Current.Id], WorkflowDocument.OpenProject(folder).Select(document => document.Current.Id));
    }

    [Fact]
    public void Rename_trims_round_trips_and_blank_names_omit_the_property()
    {
        var folder = _temp.Create("project");
        var document = WorkflowDocument.OpenProject(folder).Single();
        document.Apply(new Rename("  Release checks  "));
        document.Apply(TestNodes.Place(TestNodes.Implement(TestTasks.Build, "Implement"), new CanvasPoint(0, 0)));
        document.Save();

        var reopened = WorkflowDocument.OpenProject(folder).Single();
        Assert.Equal("Release checks", reopened.Current.Name);
        Assert.Equal("Release checks", JsonNode.Parse(File.ReadAllText(reopened.FilePath))!["name"]!.GetValue<string>());
        reopened.Apply(new Rename(" \t\n "));
        reopened.Save();
        Assert.False(JsonNode.Parse(File.ReadAllText(reopened.FilePath))!.AsObject().ContainsKey("name"));
        Assert.Null(WorkflowDocument.OpenProject(folder).Single().Current.Name);
        Assert.Equal("Implement", WorkflowDocument.OpenProject(folder).Single().Current.Tasks[TestTasks.Build].Title);
    }

    [Fact]
    public void Each_rename_is_its_own_undo_step()
    {
        var document = WorkflowDocument.OpenProject(_temp.Create("project")).Single();
        document.Apply(new Rename("Build"));
        document.Apply(new Rename("Release"));

        Assert.Equal("Release", document.Current.Name);
        document.Undo();
        Assert.Equal("Build", document.Current.Name);
        Assert.True(document.HasUnsavedChanges);
        document.Undo();
        Assert.Null(document.Current.Name);
        Assert.False(document.CanUndo);
        Assert.False(document.HasUnsavedChanges);
        document.Redo();
        Assert.Equal("Build", document.Current.Name);
        document.Save();
        document.Apply(new Rename("Checks"));
        document.Undo();
        Assert.Equal("Build", document.Current.Name);
        Assert.False(document.HasUnsavedChanges);
    }

    [Fact]
    public void A_normalized_rename_with_no_change_preserves_the_snapshot_and_undo_history()
    {
        var document = WorkflowDocument.OpenProject(_temp.Create("project")).Single();
        var opened = document.Current;
        var changes = 0;
        document.Changed += (_, _) => changes++;
        document.Apply(new Rename(" \t "));
        document.Apply(new Rename(null));
        Assert.Same(opened, document.Current);
        Assert.False(document.CanUndo);
        Assert.Equal(0, changes);

        document.Apply(new Rename("Build"));
        Assert.Equal(("Build", true), (document.Current.Name, document.CanUndo));
        document.Save();
        var saved = document.Current;
        document.Apply(new Rename(" Build "));
        Assert.Same(saved, document.Current);
        Assert.False(document.HasUnsavedChanges);
        document.Undo();
        Assert.Null(document.Current.Name);
    }

    private static string WriteEmpty(string folder, string fileName, string id, string? name)
    {
        var workflows = Directory.CreateDirectory(Path.Combine(folder, ".idp", "workflows")).FullName;
        var path = Path.Combine(workflows, $"{fileName}.json");
        var json = JsonNode.Parse($$$"""{"format":"idevelop.workflow/3","id":"{{{id}}}","blueprints":[],"tasks":[],"connections":[],"layout":{}}""")!.AsObject();
        if (name is not null)
        {
            json["name"] = name;
        }

        File.WriteAllText(path, json.ToJsonString());
        return path;
    }
}
