using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>One agent session, which continues with the person's messages.</summary>
public sealed class AgentWork : IConverses
{
    public static readonly AgentWork Instance = new();

    /// <summary>What a May ask agent reads after its template.</summary>
    private const string Ask =
        "If you cannot go on without an answer from the person, ask instead of guessing. End your final message with " +
        "this block and nothing after it, where the question is your own:\n\n" +
        "```idevelop\n{\"status\": \"asking\", \"question\": \"...\"}\n```\n\n";

    private AgentWork() { }

    public WorkKind Kind => WorkKind.Agent;

    public NodeStep Next(NodeContext context, AttemptRecord? latest) => latest switch
    {
        null => new NodeStep.RunTurn(Prompt(context)),
        { Status: AttemptStatus.Running } => throw new ArgumentException("A turn of this attempt is running.", nameof(latest)),
        { Status: AttemptStatus.WaitingForInput, Pending: { } pending } => new NodeStep.WaitForPerson(pending),
        { Status: AttemptStatus.Succeeded } => new NodeStep.Finish(latest.Result),
        _ => new NodeStep.Fail(latest.Detail ?? $"The attempt ended {latest.Status}."),
    };

    public MessageUse Receive(string message) => new MessageUse.Turn(message);

    /// <summary>The rendered template alone, without a contract: what a reviewer reads as the node's ticket.</summary>
    internal static string Ticket(TaskDefinition node) => ((WorkSpec.Agent)node.Blueprint.Work).Template.Render(name => name switch
    {
        "title" => node.Title,
        "inputs" => "",
        _ => node.Field(name),
    }).TrimEnd();

    /// <summary>
    /// The rendered template, then iDevelop's contract for the node's conversation mode and for a proposal, which no
    /// blueprint can remove. An Autonomous node that proposes nothing has no contract, so the built-in Implement's prompt
    /// is single-task execution's.
    /// </summary>
    internal static string Prompt(NodeContext context)
    {
        var node = context.Node;
        var template = ((WorkSpec.Agent)node.Blueprint.Work).Template;
        var rendered = template.Render(name => name switch
        {
            "title" => node.Title,
            "inputs" => context.Inputs,
            _ => node.Field(name),
        });
        return Contract(node, context.Planning) is { } contract ? $"{rendered.TrimEnd()}\n\n{contract}\n" : rendered;
    }

    /// <summary>
    /// Why the node waits after a turn that succeeded with no message of the person's waiting, or null when it is done.
    /// Pure, so folding the same log always settles the attempt the same way.
    /// </summary>
    internal static Pending? AfterTurn(ConversationMode mode, string? finalText) => mode switch
    {
        ConversationMode.Autonomous => null,
        ConversationMode.Chat => new Pending.Reply(),
        ConversationMode.MayAsk => ResultBlock.Read(finalText) switch
        {
            ResultBlock.Readable { Status: "asking" } block when !string.IsNullOrWhiteSpace(block.Text("question")) =>
                new Pending.Question(block.Text("question")!.Trim()),
            ResultBlock.Readable { Status: "asking" } => new Pending.UnreadableBlock("The block asks without a question."),
            ResultBlock.Unreadable unreadable => new Pending.UnreadableBlock(unreadable.Problem),
            _ => null,
        },
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
