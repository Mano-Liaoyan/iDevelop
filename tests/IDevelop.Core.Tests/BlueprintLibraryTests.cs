using System.Text;
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

    [Fact]
    public void A_derived_blueprint_edited_to_version_2_leaves_the_node_placed_from_version_1_on_version_1()
    {
        var project = _temp.Create("project");
        var library = BlueprintLibrary.Project(project);
        var key = BlueprintLibrary.NewKey("Bug fix");
        library.Save(BugFix(key));
        var document = WorkflowDocument.Open(project);
        var (first, second) = (TaskId.New(), TaskId.New());

        Assert.IsType<EditResult.Applied>(document.Apply(new PlaceNode(first, Assert.Single(library.Read().Blueprints), new CanvasPoint(0, 0))));
        library.Save(BugFix(key with { Version = 2 }, template: "Fix {{bug}} and say how you found it.\n{{instructions}}\n"));
        Assert.IsType<EditResult.Applied>(document.Apply(new PlaceNode(second, Assert.Single(library.Read().Blueprints), new CanvasPoint(0, 200))));
        document.Save();
        var reopened = WorkflowDocument.Open(project).Current;

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
        Assert.Equal("*.tmp\nattempts/\n", File.ReadAllText(Path.Combine(project, ".idp", ".gitignore")));
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
