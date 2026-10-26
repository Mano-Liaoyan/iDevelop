using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>What an Agent node's agent reads, and how its reply ends the turn under its conversation mode.</summary>
public static class AgentPrompt
{
    /// <summary>What a May ask agent reads after its template.</summary>
    private const string Ask =
        "If you cannot go on without an answer from the person, ask instead of guessing. End your final message with " +
        "this block and nothing after it, where the question is your own:\n\n" +
        "```idevelop\n{\"status\": \"asking\", \"question\": \"...\"}\n```\n\n";

    /// <summary>
    /// The rendered template, then iDevelop's contract for the node's conversation mode and for a proposal, which no
    /// blueprint can remove. An Autonomous node that proposes nothing has no contract.
    /// </summary>
    /// <param name="node">The node whose agent reads the prompt.</param>
    /// <param name="inputs">The input from earlier nodes, which the template can use.</param>
    /// <param name="planning">What a node whose agent proposes may fill and place, which its prompt lists.</param>
    public static string For(TaskDefinition node, string inputs, PlanningContext? planning = null)
    {
        var template = ((WorkSpec.Agent)node.Blueprint.Work).Template;
        var rendered = template.Render(name => name switch
        {
            "title" => node.Title,
            "inputs" => inputs,
            _ => node.Field(name),
        });
        return Contract(node, planning) is { } contract ? $"{rendered.TrimEnd()}\n\n{contract}\n" : rendered;
    }

    /// <summary>
    /// What a May ask agent's final message asks the person, or why its block cannot be read, or null when it asks nothing.
    /// </summary>
    public static string? Question(string? finalText) => ResultBlock.Read(finalText) switch
    {
        ResultBlock.Readable { Status: "asking" } block when !string.IsNullOrWhiteSpace(block.Text("question")) => block.Text("question")!.Trim(),
        ResultBlock.Readable { Status: "asking" } => "The block asks without a question.",
        ResultBlock.Unreadable unreadable => unreadable.Problem,
        _ => null,
    };

    private static string? Contract(TaskDefinition node, PlanningContext? planning)
    {
        var proposes = node.Blueprint.Work is WorkSpec.Agent { Proposes: true };
        var ask = node.Conversation switch
        {
            ConversationMode.Autonomous or ConversationMode.Chat => null,
            ConversationMode.MayAsk => Ask + (proposes
                ? "When you have finished, end with the proposal block below instead."
                : "When you have finished the work, end without that block."),
        };
        var proposal = proposes ? (planning ?? PlanningContext.None).Contract(node.Conversation) : null;
        string[] parts = [.. new[] { ask, proposal }.OfType<string>()];
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }
}
