using System.Collections.Immutable;
using System.Diagnostics;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>Why a task cannot start. Every case is shown instead of a launch.</summary>
public abstract record StartProblem
{
    private StartProblem() { }

    public sealed record NoAgent : StartProblem;

    /// <summary>A required field of the node's blueprint is blank.</summary>
    public sealed record FieldMissing(string Label) : StartProblem;

    /// <summary>The node waits for the person, who replies, marks it done, or cancels it first.</summary>
    public sealed record Waiting(string Title) : StartProblem;

    /// <summary>The node's work has no agent to send a message to.</summary>
    public sealed record NoConversation : StartProblem;

    /// <summary>The node's agent may only read, and the client has no mode that keeps it from writing.</summary>
    public sealed record NoReadOnlyMode(ClientId Client) : StartProblem;

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

internal sealed record LaunchPlan(ClientDefinition Client, ResolvedCommand Command, ExecutionSettings Settings, LaunchRequest Request)
{
    public LaunchArguments Launch => Client.Launch(Request);

    /// <summary>A later turn, which resumes the session with the person's message alone. The client holds the context.</summary>
    public LaunchPlan Resuming(string session, string message) => this with { Request = Request with { Prompt = message, ResumeSession = session } };
}

/// <summary>A first turn that resumes an earlier attempt's session with the person's message instead of the task's prompt.</summary>
internal sealed record Resumption(string Session, string Message);

internal abstract record StartVerdict
{
    private StartVerdict() { }

    public sealed record Allowed(LaunchPlan Plan) : StartVerdict;

    public sealed record Blocked(StartProblem Problem) : StartVerdict;
}

/// <summary>Pure. The inspector's message before any click and the start itself use the same check.</summary>
internal static class StartCheck
{
    /// <param name="resume">Set for a continuation, whose prompt is the person's message, so the task needs no field filled in.</param>
    /// <param name="planning">What a node whose agent proposes may fill and place, which its first prompt lists.</param>
    public static StartVerdict Evaluate(
        TaskDefinition task, string projectFolder, IReadOnlyDictionary<ClientId, ClientStatus> clients, Resumption? resume = null, PlanningContext? planning = null)
    {
        if (task.Execution is not { } settings)
        {
            return Block(new StartProblem.NoAgent());
        }

        var client = settings.Client;
        var readOnly = task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly };
        if (readOnly && !Clients.Get(client).HasReadOnlyMode)
        {
            return Block(new StartProblem.NoReadOnlyMode(client));
        }

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

        var work = NodeWorks.For(task.Blueprint.Work);
        string prompt;
        if (resume is not null)
        {
            if (work is not IConverses converses)
            {
                return Block(new StartProblem.NoConversation());
            }

            prompt = converses.Reply(resume.Message);
        }
        else if (task.Blueprint.Fields.FirstOrDefault(field => field.Required && string.IsNullOrWhiteSpace(task.Field(field.Key))) is { } missing)
        {
            return Block(new StartProblem.FieldMissing(missing.Label));
        }
        else
        {
            prompt = work.Next(new NodeContext(task, "") { Planning = planning }, null) is NodeStep.RunTurn turn
                ? turn.Prompt
                : throw new UnreachableException("A fresh start always runs a turn.");
        }

        var request = new LaunchRequest(id, settings.Reasoning, prompt) { ResumeSession = resume?.Session, ReadOnly = readOnly };
        var plan = new LaunchPlan(Clients.Get(client), ready.Command, settings, request);
        if (ready.Command.UnsafeArgument(plan.Launch.Arguments) is { } argument)
        {
            return Block(new StartProblem.UnsafeArgument(client, argument));
        }

        return new StartVerdict.Allowed(plan);
    }

    private static StartVerdict.Blocked Block(StartProblem problem) => new(problem);
}
