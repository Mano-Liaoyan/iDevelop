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

    /// <summary>Plans the work toward a goal without changing the project, and proposes the tasks that carry it out.</summary>
    public static Blueprint Plan { get; } = new(
        new BlueprintKey("idevelop.plan", 1),
        "Plan",
        new WorkSpec.Agent(AgentAccess.ReadOnly, Proposes: true, PromptTemplate.Parse(
            "{{#title}}# {{title}}\n\n{{/title}}" +
            "Plan the work that reaches the goal below. Read the project as you need, but do not change any file. " +
            "Split the work into tasks that each make sense on their own, and order them by what each one needs first.\n\n" +
            "## Goal\n\n{{goal}}\n" +
            "{{#constraints}}\n## Constraints\n\n{{constraints}}\n{{/constraints}}" +
            "{{#inputs}}\n## What earlier tasks handed on\n\n{{inputs}}\n{{/inputs}}")),
        [
            new FieldSpec("goal", "Goal", FieldShape.Text, Required: true, ""),
            new FieldSpec("constraints", "Constraints", FieldShape.Text, Required: false, ""),
        ],
        new NodeSettings(null, ConversationMode.MayAsk))
    {
        Description = "Plans the work toward a goal and proposes the tasks that carry it out. It changes no file.",
    };

    /// <summary>Designs from a brief without changing the project, reports the design, and proposes the tasks that build it.</summary>
    public static Blueprint Architect { get; } = new(
        new BlueprintKey("idevelop.architect", 1),
        "Architect",
        new WorkSpec.Agent(AgentAccess.ReadOnly, Proposes: true, PromptTemplate.Parse(
            "{{#title}}# {{title}}\n\n{{/title}}" +
            "Design how to build what the brief below asks for. Read the project as you need, but do not change any file. " +
            "Write the design as your report: the parts, how they fit together, and each decision with its reason. " +
            "Then split the work into tasks that build the design.\n\n" +
            "## Brief\n\n{{brief}}\n" +
            "{{#inputs}}\n## What earlier tasks handed on\n\n{{inputs}}\n{{/inputs}}")),
        [new FieldSpec("brief", "Brief", FieldShape.Text, Required: true, "")],
        new NodeSettings(null, ConversationMode.MayAsk))
    {
        Description = "Designs from a brief, reports the design, and proposes the tasks that build it. It changes no file.",
    };

    /// <summary>Every built-in, in the order a palette lists them.</summary>
    public static ImmutableArray<Blueprint> All { get; } = [Implement, Plan, Architect];

    public static Blueprint? Find(BlueprintKey key) => All.FirstOrDefault(blueprint => blueprint.Key == key);
}
