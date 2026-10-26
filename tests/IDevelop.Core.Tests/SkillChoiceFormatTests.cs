using System.Text.Json;
using System.Text;
using IDevelop.Clients;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.TestTasks;

namespace IDevelop.Core.Tests;

/// <summary>
/// A node's skill choice (#93) is part of its agent. No choice means every skill the client loads, and a choice, even an
/// empty one, means exactly those. The workflow file keeps it only when it is set, so a file without one reads as before.
/// </summary>
public sealed class SkillChoiceFormatTests
{
    private static readonly ExecutionSettings Claude = new(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "high" };

    [Fact]
    public void A_choice_compares_by_its_names_and_trims_and_drops_blank_ones()
    {
        var chosen = Claude with { Skills = [" tdd", "find-skills", "", "tdd"] };

        Assert.Equal(["find-skills", "tdd"], chosen.Skills!);
        Assert.Equal(Claude with { Skills = ["tdd", "find-skills"] }, chosen);
        Assert.Equal((Claude with { Skills = ["tdd", "find-skills"] }).GetHashCode(), chosen.GetHashCode());
        Assert.NotEqual(Claude, chosen);
        Assert.NotEqual(Claude, Claude with { Skills = [] });
        Assert.NotEqual(Claude with { Skills = ["tdd"] }, chosen);
    }

    [Fact]
    public void Choosing_another_client_clears_the_choice_and_choosing_a_model_keeps_it()
    {
        var chosen = Claude with { Skills = ["tdd"] };
        var catalog = new ClientStatus.Ready(new ResolvedCommand("/bin/codex", IsBatchShim: false),
            [new ModelOption("gpt-5.5", "GPT-5.5", ["high"]) { DefaultReasoning = "high" }]);

        Assert.Null(ExecutionChoices.ForClient(ClientId.Codex, catalog).Skills);
        Assert.Equal(["tdd"], ExecutionChoices.ForModel(chosen, new ModelOption("claude-haiku-4-5", "Haiku", ["high"])).Skills!);
    }

    [Fact]
    public void A_workflow_file_keeps_a_choice_sorted_keeps_an_empty_one_and_writes_nothing_without_one()
    {
        var workflow = Place(
            TestNodes.Implement(Design, "Design", execution: Claude with { Skills = ["tdd", "find-skills"] }),
            TestNodes.Implement(Build, "Build", execution: Claude with { Skills = [] }),
            TestNodes.Implement(Review, "Review", execution: Claude));

        var bytes = WorkflowFile.Serialize(workflow);
        var read = WorkflowFile.Parse(bytes, "file").Workflow;

        using var json = JsonDocument.Parse(bytes);
        var executions = json.RootElement.GetProperty("tasks").EnumerateArray()
            .ToDictionary(task => task.GetProperty("title").GetString()!, task => task.GetProperty("execution"));
        Assert.Equal("""["find-skills","tdd"]""", executions["Design"].GetProperty("skills").GetRawText().Replace(" ", "").Replace("\n", ""));
        Assert.Equal("[]", executions["Build"].GetProperty("skills").GetRawText());
        Assert.False(executions["Review"].TryGetProperty("skills", out _));
        Assert.Equal(["find-skills", "tdd"], read.Tasks[Design].Execution!.Skills!);
        Assert.Empty(read.Tasks[Build].Execution!.Skills!);
        Assert.Null(read.Tasks[Review].Execution!.Skills);
        Assert.Equal(workflow.Tasks.Values, read.Tasks.Values);
        Assert.Equal(bytes, WorkflowFile.Serialize(read));
    }

    [Fact]
    public void A_blueprint_saved_from_a_node_keeps_the_nodes_choice()
    {
        var task = TestNodes.Implement(Design, "Design", execution: Claude with { Skills = ["tdd"] });
        var saved = new Blueprint(new BlueprintKey("chosen", 1), "Chosen", BuiltInBlueprints.Implement.Work, BuiltInBlueprints.Implement.Fields,
            new NodeSettings(task.Execution, task.Conversation));

        var read = BlueprintJson.FromDto(
            JsonSerializer.Deserialize<BlueprintDto>(JsonSerializer.SerializeToUtf8Bytes(BlueprintJson.ToDto(saved), WorkflowFile.Options), WorkflowFile.Options)!, "file");

        Assert.Equal(["tdd"], read.Defaults.Execution!.Skills!);
    }

    [Theory]
    [InlineData("""["tdd", null]""", "has a null skill name.")]
    [InlineData("""["tdd", " "]""", "has a blank skill name.")]
    [InlineData("""["tdd", "tdd"]""", "names skill \"tdd\" twice.")]
    public void A_file_with_a_bad_skill_name_is_refused(string skills, string problem)
    {
        var bytes = WorkflowFile.Serialize(Place(TestNodes.Implement(Design, "Design", execution: Claude with { Skills = ["x"] })));
        var text = Encoding.UTF8.GetString(bytes);
        var start = text.IndexOf("\"skills\": [", StringComparison.Ordinal);
        var edited = text[..start] + "\"skills\": " + skills + text[(text.IndexOf(']', start) + 1)..];

        var error = Assert.Throws<ProjectException>(() => WorkflowFile.Parse(Encoding.UTF8.GetBytes(edited), "file"));

        Assert.Equal($"file: task {Design} {problem}", error.Message);
    }

    private static Workflow Place(params TaskDefinition[] tasks) => tasks.Index().Aggregate(Workflow.Empty(new WorkflowId(Guid.CreateVersion7())),
        (workflow, entry) => ((EditResult.Applied)workflow.Apply(TestNodes.Place(entry.Item, new CanvasPoint(entry.Index * 300, 0)))).Workflow);
}
