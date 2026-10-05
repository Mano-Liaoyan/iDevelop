using System.Collections.Immutable;

namespace IDevelop.Workflows;

/// <summary>
/// The blueprints that ship with iDevelop, read-only under the <see cref="Blueprint.BuiltInPrefix"/> id prefix. A change
/// to one ships as its next version and keeps the old one here, so a saved workflow's embedded copy never changes.
/// </summary>
public static class BuiltInBlueprints
{
    /// <summary>
    /// Carries out its instructions and may edit the project. Rendered without a contract, its prompt is the title as a
    /// heading, the instructions, and the acceptance criteria under their own heading when present, as single-task
    /// execution sent it.
    /// </summary>
    public static Blueprint Implement { get; } = new(
        new BlueprintKey("idevelop.implement", 1),
        "Implement",
        new WorkSpec.Agent(AgentAccess.Edit, Proposes: false, PromptTemplate.Parse(
            "{{#title}}# {{title}}\n\n{{/title}}{{instructions}}\n" +
            "{{#acceptanceCriteria}}\n## Acceptance criteria\n\n{{acceptanceCriteria}}\n{{/acceptanceCriteria}}")),
        [
            new FieldSpec("instructions", "Instructions", FieldShape.Text, Required: true, ""),
            new FieldSpec("acceptanceCriteria", "Acceptance criteria", FieldShape.Text, Required: false, ""),
        ],
        new NodeSettings(null, ConversationMode.Autonomous))
    {
        Description = "Carries out its instructions with the agent you choose, and may edit the project.",
    };

    /// <summary>Every built-in, in the order a palette lists them.</summary>
    public static ImmutableArray<Blueprint> All { get; } = [Implement];

    public static Blueprint? Find(BlueprintKey key) => All.FirstOrDefault(blueprint => blueprint.Key == key);
}
