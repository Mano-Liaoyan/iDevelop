using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Why a task cannot start. Every case is shown instead of a launch.</summary>
public abstract record StartProblem
{
    private StartProblem() { }

    public sealed record NoAgent : StartProblem;

    public sealed record NoInstructions : StartProblem;

    public sealed record NoModel(ClientId Client) : StartProblem;

    public sealed record ClientChecking(ClientId Client) : StartProblem;

    public sealed record ClientMissing(ClientId Client, string Reason) : StartProblem;

    public sealed record ClientUnready(ClientId Client, string Reason) : StartProblem;

    public sealed record ModelNotOffered(ClientId Client, string Model) : StartProblem;

    public sealed record ModelUnready(ClientId Client, string Model, string Reason) : StartProblem;

    /// <summary>The level is missing or not offered. <paramref name="Offered"/> is empty when the model takes no level.</summary>
    public sealed record ReasoningNotOffered(ClientId Client, string Model, string? Reasoning, ImmutableArray<string> Offered) : StartProblem;

    /// <summary>The client is a batch shim, and cmd.exe could misread this argument.</summary>
    public sealed record UnsafeArgument(ClientId Client, string Argument) : StartProblem;

    /// <summary>The client is a batch shim, and cmd.exe cannot work in the project's network folder.</summary>
    public sealed record UncProjectFolder(ClientId Client) : StartProblem;

    /// <summary>This task is already running, in this window or another one.</summary>
    public sealed record AlreadyRunning(TaskId Task, string Title) : StartProblem;

    /// <summary>Another window holds this task's run but has not recorded its attempt yet.</summary>
    public sealed record RunInAnotherWindow : StartProblem;

    /// <summary>The attempt could not be recorded, so nothing was launched. The reason is for the user.</summary>
    public sealed record CannotRecord(string Reason) : StartProblem;
}

public abstract record StartResult
{
    private StartResult() { }

    /// <summary>The attempt is on record. Its status is already Failed when the client could not be launched.</summary>
    public sealed record Started(AttemptRecord Attempt) : StartResult;

    public sealed record Refused(StartProblem Problem) : StartResult;
}

internal sealed record LaunchPlan(ClientDefinition Client, ResolvedCommand Command, LaunchArguments Launch, ExecutionSettings Settings, string Prompt);

internal abstract record StartVerdict
{
    private StartVerdict() { }

    public sealed record Allowed(LaunchPlan Plan) : StartVerdict;

    public sealed record Blocked(StartProblem Problem) : StartVerdict;
}

/// <summary>Pure. The inspector's message before any click and the start itself use the same check.</summary>
internal static class StartCheck
{
    public static StartVerdict Evaluate(TaskDefinition task, string projectFolder, IReadOnlyDictionary<ClientId, ClientStatus> clients)
    {
        if (task.Execution is not { } settings)
        {
            return Block(new StartProblem.NoAgent());
        }

        var client = settings.Client;
        ClientStatus.Ready ready;
        switch (clients.GetValueOrDefault(client))
        {
            case ClientStatus.Ready status:
                ready = status;
                break;
            case ClientStatus.Missing missing:
                return Block(new StartProblem.ClientMissing(client, missing.Reason));
            case ClientStatus.Unready unready:
                return Block(new StartProblem.ClientUnready(client, unready.Reason));
            default:
                return Block(new StartProblem.ClientChecking(client));
        }

        // cmd.exe refuses a current folder that starts with \\ and runs in the Windows folder instead.
        if (ready.Command.IsBatchShim && projectFolder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Block(new StartProblem.UncProjectFolder(client));
        }

        if (settings.Model is not { } id)
        {
            return Block(new StartProblem.NoModel(client));
        }

        if (ready.Models.FirstOrDefault(model => model.Id == id) is not { } model)
        {
            return Block(new StartProblem.ModelNotOffered(client, id));
        }

        if (model.Problem is { } problem)
        {
            return Block(new StartProblem.ModelUnready(client, id, problem));
        }

        var levelOffered = settings.Reasoning is { } level ? model.ReasoningLevels.Contains(level) : model.ReasoningLevels.IsEmpty;
        if (!levelOffered)
        {
            return Block(new StartProblem.ReasoningNotOffered(client, id, settings.Reasoning, model.ReasoningLevels));
        }

        if (string.IsNullOrWhiteSpace(task.Instructions))
        {
            return Block(new StartProblem.NoInstructions());
        }

        var definition = ClientRegistry.Get(client);
        var prompt = Prompt.For(task);
        var launch = definition.Launch(new LaunchRequest(id, settings.Reasoning, prompt));
        if (ready.Command.UnsafeArgument(launch.Arguments) is { } argument)
        {
            return Block(new StartProblem.UnsafeArgument(client, argument));
        }

        return new StartVerdict.Allowed(new LaunchPlan(definition, ready.Command, launch, settings, prompt));
    }

    private static StartVerdict.Blocked Block(StartProblem problem) => new(problem);
}

internal static class Prompt
{
    /// <summary>The title as a heading, the instructions, and the acceptance criteria under their own heading when present.</summary>
    public static string For(TaskDefinition task)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(task.Title))
        {
            parts.Add($"# {task.Title.Trim()}");
        }

        parts.Add(task.Instructions.Trim());
        if (!string.IsNullOrWhiteSpace(task.AcceptanceCriteria))
        {
            parts.Add("## Acceptance criteria");
            parts.Add(task.AcceptanceCriteria.Trim());
        }

        return string.Join("\n\n", parts) + "\n";
    }
}
