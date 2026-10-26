using IDevelop.Workflows;

namespace IDevelop.Clients;

/// <summary>One turn's launch: the client, its command on this machine, the node's agent settings, and the request.</summary>
internal sealed record LaunchPlan(ClientDefinition Client, ResolvedCommand Command, ExecutionSettings Settings, LaunchRequest Request)
{
    public LaunchArguments Launch => Client.Launch(Request);

    /// <summary>
    /// The launch of a turn of <paramref name="node"/> that sends <paramref name="prompt"/> in <paramref name="folder"/>,
    /// resuming <paramref name="session"/> when set, or why it cannot start. Pure, so the inspector's message before any
    /// click and the start itself agree.
    /// </summary>
    /// <param name="skills">The skills the node's client lists in the folder, which a node with a skill choice needs.</param>
    public static (LaunchPlan? Plan, string? Problem) For(
        TaskDefinition node, string folder, string prompt, string? session, IReadOnlyDictionary<ClientId, ClientStatus> clients, SkillListing? skills)
    {
        if (node.Blueprint.Work is WorkSpec.Person)
        {
            return (null, "The agent cannot start.");
        }

        if (node.Execution is not { } settings)
        {
            return (null, "The node has no agent.");
        }

        var client = settings.Client;
        var name = ClientCatalog.Name(client);
        var readOnly = node.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review;
        if (readOnly && !ClientCatalog.Get(client).HasReadOnlyMode)
        {
            return (null, $"{name} has no read-only mode.");
        }

        ClientStatus.Ready ready;
        switch (clients.GetValueOrDefault(client))
        {
            case ClientStatus.Ready status:
                ready = status;
                break;
            case ClientStatus.Missing missing:
                return (null, $"{name} is not installed. {missing.Reason}");
            case ClientStatus.Unready unready:
                return (null, $"{name} is not ready. {unready.Reason}");
            default:
                return (null, $"iDevelop is still checking {name}.");
        }

        // cmd.exe refuses a current folder that starts with \\ and runs in the Windows folder instead.
        if (ready.Command.IsBatchShim && folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (null, $"{name} cannot run in a network folder.");
        }

        if (settings.Model is not { } id)
        {
            return (null, $"Choose a model for {name}.");
        }

        if (ready.Models.FirstOrDefault(model => model.Id == id) is not { } model)
        {
            return (null, $"{name} does not offer {id}.");
        }

        if (model.Problem is { } problem)
        {
            return (null, $"{id} is not ready. {problem}");
        }

        var levelOffered = settings.Reasoning is { } level ? model.ReasoningLevels.Contains(level) : model.ReasoningLevels.IsEmpty;
        if (!levelOffered)
        {
            return (null, $"{id} does not offer the reasoning level {settings.Reasoning ?? "(none)"}.");
        }

        // A chosen skill that is no longer installed is left out. A skill list that could not be read does not hold the node
        // back: the choice goes as it is (#93, 2026-10-11). Antigravity CLI has no choice to keep.
        SkillLaunch? skillLaunch = null;
        if (settings.Skills is { } chosen && SkillChoices.CanChoose(client))
        {
            switch (skills)
            {
                case SkillListing.Listed listed:
                    skillLaunch = SkillChoices.Launch(chosen, listed.Skills);
                    break;
                case SkillListing.Unavailable:
                    skillLaunch = SkillChoices.Unchecked(chosen);
                    break;
                default:
                    return (null, $"iDevelop is still listing the skills {name} loads.");
            }
        }

        var request = new LaunchRequest(id, settings.Reasoning, prompt)
        {
            ResumeSession = session, ReadOnly = readOnly, Skills = skillLaunch,
            Policy = ClientPolicy.For(client, readOnly, node.Conversation, node.Blueprint.Work is WorkSpec.Review, new HostQuestions.Disabled()),
        };
        var plan = new LaunchPlan(ClientCatalog.Get(client), ready.Command, settings, request);
        return ready.Command.UnsafeArgument(plan.Launch.Arguments) is { } argument
            ? (null, $"cmd.exe could misread the argument {argument}.")
            : (plan, null);
    }
}
