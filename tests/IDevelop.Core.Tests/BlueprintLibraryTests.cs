using System.Text;
using System.Text.Json.Nodes;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Core.Tests;

public sealed class BlueprintLibraryTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Implement's structure under a new key, with a field of its own and a template that reads it.</summary>
    private static Blueprint BugFix(BlueprintKey key, string template = "Fix {{bug}}.\n{{instructions}}\n") => new(
        key,
        "Bug fix",
        new WorkSpec.Agent(AgentAccess.Edit, Proposes: false, PromptTemplate.Parse(template)),
        [
            new FieldSpec("bug", "Bug", FieldShape.Line, Required: true, ""),
            new FieldSpec("instructions", "Instructions", FieldShape.Text, Required: false, "Add a test first."),
        ],
        new NodeSettings(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-astra" }, ConversationMode.MayAsk))
    {
        Description = "Fixes one bug.",
        DerivedFrom = BuiltInBlueprints.Implement.Key,
    };

    [Theory]
    [InlineData(BlueprintIcon.Code, "code", BlueprintColor.Indigo, "indigo")]
    [InlineData(BlueprintIcon.TaskList, "taskList", BlueprintColor.Cyan, "cyan")]
    [InlineData(BlueprintIcon.Ruler, "ruler", BlueprintColor.Purple, "purple")]
    [InlineData(BlueprintIcon.Glasses, "glasses", BlueprintColor.Mint, "mint")]
    [InlineData(BlueprintIcon.PersonAvailable, "personAvailable", BlueprintColor.Brown, "brown")]
    [InlineData(BlueprintIcon.DocumentSearch, "documentSearch", BlueprintColor.Gray, "gray")]
    public void Icon_and_color_round_trip_through_the_library_and_the_embedded_workflow_copy(
        BlueprintIcon icon, string iconName, BlueprintColor color, string colorName)
    {
        var project = _temp.Create("project");
        var library = BlueprintLibrary.Project(project);
        var blueprint = BugFix(new BlueprintKey("bug-fix", 1)) with { Icon = icon, Color = color };
        library.Save(blueprint);
        var libraryFile = JsonNode.Parse(File.ReadAllText(Path.Combine(library.Folder, "bug-fix.json")))!;
        Assert.Equal(iconName, libraryFile["icon"]!.GetValue<string>());
        Assert.Equal(colorName, libraryFile["color"]!.GetValue<string>());
        var loaded = Assert.Single(library.Read().Blueprints);
        Assert.Equal(icon, loaded.Icon);
        Assert.Equal(color, loaded.Color);

        var document = WorkflowDocument.Create(project, "Build");
        document.Apply(new PlaceNode(TestTasks.Build, loaded, new CanvasPoint(0, 0)) { Title = "Implement" });
        document.Save();
        var workflowFile = JsonNode.Parse(File.ReadAllText(document.FilePath))!;
        Assert.Equal(iconName, workflowFile["blueprints"]![0]!["icon"]!.GetValue<string>());
        Assert.Equal(colorName, workflowFile["blueprints"]![0]!["color"]!.GetValue<string>());
        var embedded = WorkflowDocument.OpenProject(project).Single().Current.Tasks[TestTasks.Build].Blueprint;
        Assert.Equal(icon, embedded.Icon);
        Assert.Equal(color, embedded.Color);
        Assert.Equal("Fixes one bug.", embedded.Description);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Optional_appearance_properties_are_written_only_when_set(bool icon, bool color)
    {
        var project = _temp.Create("project");
        var blueprint = BugFix(new BlueprintKey("bug-fix", 1)) with
        {
            Icon = icon ? BlueprintIcon.DocumentSearch : null,
            Color = color ? BlueprintColor.Mint : null,
        };
        var library = JsonNode.Parse(BlueprintLibrary.Serialize(blueprint))!.AsObject();
        Assert.Equal(icon, library.ContainsKey("icon"));
        Assert.Equal(color, library.ContainsKey("color"));
        Assert.Equal("Bug fix", library["name"]!.GetValue<string>());

        var document = WorkflowDocument.Create(project, "Build");
        document.Apply(new PlaceNode(TestTasks.Build, blueprint, new CanvasPoint(0, 0)));
        document.Save();
        var embedded = JsonNode.Parse(File.ReadAllText(document.FilePath))!["blueprints"]![0]!.AsObject();
        Assert.Equal(icon, embedded.ContainsKey("icon"));
        Assert.Equal(color, embedded.ContainsKey("color"));
        Assert.Equal("Bug fix", embedded["name"]!.GetValue<string>());
        var reopened = WorkflowDocument.OpenProject(project).Single().Current.Tasks[TestTasks.Build].Blueprint;
        Assert.Equal(icon ? BlueprintIcon.DocumentSearch : null, reopened.Icon);
        Assert.Equal(color ? BlueprintColor.Mint : null, reopened.Color);
    }

    [Theory]
    [InlineData("icon", "unknownGlyph")]
    [InlineData("icon", "Code")]
    [InlineData("color", "unknownHue")]
    [InlineData("color", "Indigo")]
    public void Unknown_appearance_names_refuse_library_and_workflow_files_with_the_file_and_value(string property, string value)
    {
        var project = _temp.Create("project");
        var library = BlueprintLibrary.Project(project);
        var blueprint = BugFix(new BlueprintKey("bug-fix", 1));
        library.Save(blueprint);
        var libraryPath = Path.Combine(library.Folder, "bug-fix.json");
        var json = JsonNode.Parse(File.ReadAllText(libraryPath))!;
        json[property] = value;
        File.WriteAllText(libraryPath, json.ToJsonString());
        var libraryRead = library.Read();
        Assert.Equal($"{libraryPath}: blueprint bug-fix@1 has unknown {property} \"{value}\".", Assert.Single(libraryRead.Problems));
        Assert.Empty(libraryRead.Blueprints);

        var document = WorkflowDocument.Create(project, "Build");
        document.Apply(new PlaceNode(TestTasks.Build, blueprint, new CanvasPoint(0, 0)));
        document.Save();
        json = JsonNode.Parse(File.ReadAllText(document.FilePath))!;
        json["blueprints"]![0]![property] = value;
        File.WriteAllText(document.FilePath, json.ToJsonString());
        var error = Assert.Throws<ProjectException>(() => WorkflowDocument.OpenProject(project));
        Assert.Equal($"{document.FilePath}: blueprint bug-fix@1 has unknown {property} \"{value}\".", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_different_icon_or_color_under_an_embedded_blueprint_key_is_a_conflict(bool icon)
    {
        var document = WorkflowDocument.Create(_temp.Create("project"), "Build");
        var blueprint = BugFix(new BlueprintKey("bug-fix", 1));
        document.Apply(new PlaceNode(TestTasks.Build, blueprint, new CanvasPoint(0, 0)));
        var changed = icon ? blueprint with { Icon = BlueprintIcon.Code } : blueprint with { Color = BlueprintColor.Cyan };

        var rejected = Assert.IsType<EditResult.Rejected>(document.Apply(new PlaceNode(TestTasks.Review, changed, new CanvasPoint(100, 0))));

        Assert.Equal(new EditRejection.BlueprintConflict(new BlueprintKey("bug-fix", 1)), rejected.Reason);
        Assert.Equal([TestTasks.Build], document.Current.Tasks.Keys);
    }

    [Fact]
    public void A_derived_blueprint_edited_to_version_2_leaves_the_node_placed_from_version_1_on_version_1()
    {
        var project = _temp.Create("project");
        var library = BlueprintLibrary.Project(project);
        var key = BlueprintLibrary.NewKey("Bug fix");
        library.Save(BugFix(key));
        var document = WorkflowDocument.OpenProject(project).Single();
        var (first, second) = (TaskId.New(), TaskId.New());

        Assert.IsType<EditResult.Applied>(document.Apply(new PlaceNode(first, Assert.Single(library.Read().Blueprints), new CanvasPoint(0, 0))));
        library.Save(BugFix(key with { Version = 2 }, template: "Fix {{bug}} and say how you found it.\n{{instructions}}\n"));
        Assert.IsType<EditResult.Applied>(document.Apply(new PlaceNode(second, Assert.Single(library.Read().Blueprints), new CanvasPoint(0, 200))));
        document.Save();
        var reopened = WorkflowDocument.OpenProject(project).Single().Current;

        Assert.Equal(key, reopened.Tasks[first].Blueprint.Key);
        Assert.Equal("Fix {{bug}}.\n{{instructions}}\n", ((WorkSpec.Agent)reopened.Tasks[first].Blueprint.Work).Template.Text);
        Assert.Equal(key with { Version = 2 }, reopened.Tasks[second].Blueprint.Key);
        Assert.Equal([key, key with { Version = 2 }], reopened.Blueprints.Keys);
        Assert.Equal(key with { Version = 2 }, Assert.Single(library.Read().Blueprints).Key);
        Assert.Equal("Add a test first.", reopened.Tasks[first].Field("instructions"));
        Assert.Equal(ConversationMode.MayAsk, reopened.Tasks[second].Conversation);
    }

    [Fact]
    public void The_project_library_is_one_readable_file_per_blueprint_under_idp_blueprints()
    {
        var project = _temp.Create("project");
        var key = new BlueprintKey("bug-fix-0a1b2c3d", 1);

        BlueprintLibrary.Project(project).Save(BugFix(key));

        Assert.Equal(
            """
            {
              "format": "idevelop.blueprint/1",
              "id": "bug-fix-0a1b2c3d",
              "version": 1,
              "name": "Bug fix",
              "description": [
                "Fixes one bug."
              ],
              "derivedFrom": "idevelop.implement@1",
              "work": {
                "kind": "agent",
                "access": "edit",
                "proposes": false,
                "template": [
                  "Fix {{bug}}.",
                  "{{instructions}}",
                  ""
                ]
              },
              "fields": [
                {
                  "key": "bug",
                  "label": "Bug",
                  "shape": "line",
                  "required": true,
                  "default": []
                },
                {
                  "key": "instructions",
                  "label": "Instructions",
                  "shape": "text",
                  "required": false,
                  "default": [
                    "Add a test first."
                  ]
                }
              ],
              "defaults": {
                "execution": {
                  "client": "codex",
                  "model": "gpt-6-astra",
                  "reasoning": null
                },
                "conversation": "mayAsk"
              }
            }

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(Path.Combine(project, ".idp", "blueprints", "bug-fix-0a1b2c3d.json")));
        Assert.Equal("*.tmp\nattempts/\nruns/\n", File.ReadAllText(Path.Combine(project, ".idp", ".gitignore")));
    }

    [Fact]
    public void A_save_from_an_older_version_than_the_file_holds_is_refused_and_changes_nothing()
    {
        var library = BlueprintLibrary.Personal(_temp.Create("personal"));
        var key = new BlueprintKey("bug-fix-0a1b2c3d", 1);
        library.Save(BugFix(key));
        library.Save(BugFix(key with { Version = 2 }, template: "First window.\n"));

        var refused = Assert.Throws<ProjectException>(() => library.Save(BugFix(key with { Version = 2 }, template: "Second window.\n")));

        Assert.Equal(
            $"Not saved. {Path.Combine(library.Folder, "bug-fix-0a1b2c3d.json")} changed since you opened version 1 of Bug fix, and it holds version 2 now.",
            refused.Message);
        Assert.Equal("First window.\n", ((WorkSpec.Agent)Assert.Single(library.Read().Blueprints).Work).Template.Text);
    }

    [Fact]
    public void A_new_blueprint_never_replaces_a_file_and_a_next_version_needs_the_file_it_follows()
    {
        var library = BlueprintLibrary.Personal(_temp.Create("personal"));
        var key = new BlueprintKey("bug-fix-0a1b2c3d", 1);
        var path = Path.Combine(library.Folder, "bug-fix-0a1b2c3d.json");
        library.Save(BugFix(key));

        var taken = Assert.Throws<ProjectException>(() => library.Save(BugFix(key, template: "Another.\n")));
        File.Delete(path);
        var gone = Assert.Throws<ProjectException>(() => library.Save(BugFix(key with { Version = 2 })));

        Assert.Equal($"Not saved. {path} already holds a blueprint.", taken.Message);
        Assert.Equal($"Not saved. Bug fix is no longer in the personal library at {path}.", gone.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_built_in_is_never_saved_to_a_library()
    {
        var library = BlueprintLibrary.Personal(_temp.Create("personal"));

        var refused = Assert.Throws<ProjectException>(() => library.Save(BuiltInBlueprints.Implement));

        Assert.Equal("Implement is built in, and a built-in blueprint is read-only. Derive a blueprint to change it.", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(library.Folder));
    }

    [Fact]
    public void Reading_lists_the_good_files_by_name_and_names_each_bad_one()
    {
        var folder = _temp.Create("personal");
        var library = BlueprintLibrary.Personal(folder);
        library.Save(BugFix(new BlueprintKey("bug-fix-0a1b2c3d", 1)));
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{");
        File.Copy(Path.Combine(folder, "bug-fix-0a1b2c3d.json"), Path.Combine(folder, "renamed.json"));
        File.WriteAllBytes(
            Path.Combine(folder, "idevelop.implement.json"),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BlueprintLibrary.Serialize(BugFix(new BlueprintKey("x", 1)))).Replace("\"id\": \"x\"", "\"id\": \"idevelop.implement\"")));

        var contents = library.Read();

        Assert.Equal([new BlueprintKey("bug-fix-0a1b2c3d", 1)], contents.Blueprints.Select(blueprint => blueprint.Key));
        Assert.Equal(3, contents.Problems.Length);
        Assert.StartsWith($"{Path.Combine(folder, "broken.json")} is not a valid blueprint file.", contents.Problems[0]);
        Assert.Equal(
            $"{Path.Combine(folder, "idevelop.implement.json")} holds idevelop.implement@1, a built-in id, which only iDevelop ships.",
            contents.Problems[1]);
        Assert.Equal(
            $"{Path.Combine(folder, "renamed.json")} holds blueprint bug-fix-0a1b2c3d, so its name must be bug-fix-0a1b2c3d.json.",
            contents.Problems[2]);
    }

    [Fact]
    public void A_missing_library_folder_reads_as_empty()
    {
        var contents = BlueprintLibrary.Personal(Path.Combine(_temp.Create("home"), "blueprints")).Read();

        Assert.Empty(contents.Blueprints);
        Assert.Empty(contents.Problems);
    }

    [Theory]
    [InlineData("Bug fix", "bug-fix-")]
    [InlineData("  Review: API & docs! ", "review-api-docs-")]
    [InlineData("修复", "blueprint-")]
    public void A_new_key_is_the_name_in_id_letters_and_a_random_end_at_version_1(string name, string prefix)
    {
        var key = BlueprintLibrary.NewKey(name);

        Assert.StartsWith(prefix, key.Id);
        Assert.Equal(prefix.Length + 8, key.Id.Length);
        Assert.Equal(1, key.Version);
        Assert.NotEqual(key, BlueprintLibrary.NewKey(name));
    }
}
