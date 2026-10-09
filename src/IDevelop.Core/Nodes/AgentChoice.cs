using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Nodes;

/// <summary>
/// The agent a planner chose for a task it adds, as it wrote it: the client, the model, and the reasoning level by name,
/// and its one-line reason. <see cref="AgentCheck.Of"/> checks it against what this machine offers.
/// </summary>
public sealed record ProposedAgent(string? Client, string? Model, string? Reasoning, string? Reason)
{
    /// <summary>Why the choice could not be read, such as an agent that is not an object, or null.</summary>
    public string? Unreadable { get; init; }
}

/// <summary>A client that is ready on this machine, with the models it can run now, which a planner may choose from.</summary>
public sealed record AgentOffer(ClientId Client, ImmutableArray<ModelOption> Models)
{
    // ImmutableArray compares by reference.
    public bool Equals(AgentOffer? other) => other is not null && Client == other.Client && Models.SequenceEqual(other.Models);

    public override int GetHashCode() => HashCode.Combine(Client, Models.Length);
}

/// <summary>What a planner's choice of agent comes to on this machine.</summary>
public abstract record AgentCheck
{
    private AgentCheck() { }

    /// <summary>The planner chose no agent, or the task takes none, as an Approval.</summary>
    public sealed record None : AgentCheck;

    /// <summary>This machine can run the choice, as these settings under the catalog's own names.</summary>
    public sealed record Usable(ExecutionSettings Settings) : AgentCheck;

    /// <summary>This machine cannot run the choice now. <paramref name="Problem"/> says why in a sentence, and <paramref name="Brief"/>
    /// in the few words a card has room for, such as "Pi isn't ready".</summary>
    public sealed record Unusable(string Problem, string Brief) : AgentCheck;

    /// <summary>
    /// Checks a choice as a start would: a known client, a read-only mode for a type that only reads, a ready client, an
    /// offered model without a problem, and one of its levels, or none for a model without levels. Names match the
    /// catalog's ids, then its names, ignoring case. Pure, so the same clients always give the same answer.
    /// </summary>
    public static AgentCheck Of(ProposedAgent? agent, Blueprint blueprint, IReadOnlyDictionary<ClientId, ClientStatus> clients)
    {
        if (agent is null || blueprint.Work is WorkSpec.Person)
        {
            return new None();
        }

        if (agent.Unreadable is { } unreadable)
        {
            return new Unusable($"The planner's agent could not be read. {unreadable}", "Agent unreadable");
        }

        if (agent.Client is not { } named)
        {
            return new Unusable("The planner chose no client.", "No client chosen");
        }

        if (ClientNamed(named) is not { } client)
        {
            return new Unusable($"The planner chose {named}, which is not a client iDevelop runs.", "Unknown client");
        }

        var name = Clients.Name(client);
        if (blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review && !Clients.HasReadOnlyMode(client))
        {
            return new Unusable($"The planner chose {name}, which has no read-only mode for a {blueprint.Name}.", $"{name} can't run read-only");
        }

        if (clients.GetValueOrDefault(client) is not ClientStatus.Ready ready)
        {
            return clients.GetValueOrDefault(client) switch
            {
                ClientStatus.Unready => new Unusable($"The planner chose {name}, which isn't ready.", $"{name} isn't ready"),
                ClientStatus.Checking => new Unusable($"The planner chose {name}, which iDevelop is still checking.", $"{name} is being checked"),
                _ => new Unusable($"The planner chose {name}, which isn't installed.", $"{name} isn't installed"),
            };
        }

        if (agent.Model is not { } modelName)
        {
            return new Unusable($"The planner chose no {name} model.", "No model chosen");
        }

        if (ModelNamed(ready.Models, modelName) is not { } model)
        {
            return new Unusable($"The planner chose {modelName}, which {name} doesn't offer.", "Model not offered");
        }

        if (model.Problem is { } problem)
        {
            return new Unusable($"The planner chose {model.Name}, which isn't ready. {problem}", "Model not ready");
        }

        string? level = null;
        if (agent.Reasoning is { } reasoning)
        {
            if (model.ReasoningLevels.IsEmpty)
            {
                return new Unusable($"The planner chose {reasoning} reasoning, but {model.Name} takes no reasoning level.", "Level not offered");
            }

            level = model.ReasoningLevels.FirstOrDefault(offered => Same(offered, reasoning));
            if (level is null)
            {
                return new Unusable($"The planner chose {reasoning} reasoning, which {model.Name} doesn't offer.", "Level not offered");
            }
        }
        else if (!model.ReasoningLevels.IsEmpty)
        {
            return new Unusable($"The planner chose no reasoning level for {model.Name}.", "No reasoning level");
        }

        return new Usable(new ExecutionSettings(client) { Model = model.Id, Reasoning = level });
    }

    /// <summary>The client whose wire name or display name this is, ignoring case.</summary>
    private static ClientId? ClientNamed(string name) =>
        Clients.All.Where(id => Same(Clients.WireName(id), name) || Same(Clients.Name(id), name)).Select(id => (ClientId?)id).FirstOrDefault();

    private static ModelOption? ModelNamed(ImmutableArray<ModelOption> models, string name) =>
        models.FirstOrDefault(model => model.Id == name) ?? models.FirstOrDefault(model => Same(model.Id, name) || Same(model.Name, name));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
