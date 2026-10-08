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

    /// <summary>The node has no agent. A workflow run reaches it and waits for the person.</summary>
    public sealed record RunsInWorkflow : StartProblem;

    /// <summary>The review depends on no node that edits the project.</summary>
    public sealed record NoSubject : StartProblem;

    /// <summary>The review's subject has not succeeded yet.</summary>
    public sealed record SubjectNotDone(string Title) : StartProblem;

    /// <summary>
    /// iDevelop cannot show the subject's change: the project is not in Git, a sparse checkout left files out, Git
    /// failed or can no longer read the recorded trees, or the subject ran before iDevelop recorded changes.
    /// </summary>
    public sealed record NoChange(string Title) : StartProblem;

    /// <summary>The review goes back and forth with its subject until both agents agree, or the person cancels it.</summary>
    public sealed record InReview(string Title) : StartProblem;

    /// <summary>A review that goes on reviews this task, so only the review starts its next attempt.</summary>
    public sealed record UnderReview(string Review) : StartProblem;

    /// <summary>Another review goes on with the same subject, which takes only that review's fix rounds until it ends.</summary>
    public sealed record SubjectInReview(string Subject, string Review) : StartProblem;

    /// <summary>The review cannot go on, because its subject cannot start its fix round.</summary>
    public sealed record SubjectBlocked(string Title, StartProblem Problem) : StartProblem;

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

    /// <summary>A workflow run owns the task, so only that run starts its turns.</summary>
    public sealed record RunOwned(string Workflow) : StartProblem;

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

/// <summary>
/// A first turn whose prompt is given instead of rendered from the node: the person's message, a review's next message,
/// or a fix round. It resumes <paramref name="Session"/> when set.
/// </summary>
internal sealed record Resumption(string? Session, string Message);

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
    /// <param name="subject">A review's subject, which its first prompt reads.</param>
    public static StartVerdict Evaluate(
        TaskDefinition task, string projectFolder, IReadOnlyDictionary<ClientId, ClientStatus> clients, Resumption? resume = null,
        PlanningContext? planning = null, SubjectView? subject = null, HostQuestions? questions = null)
    {
        if (task.Blueprint.Work is WorkSpec.Person)
        {
            return Block(new StartProblem.RunsInWorkflow());
        }

        if (task.Execution is not { } settings)
        {
            return Block(new StartProblem.NoAgent());
        }

        var client = settings.Client;
        var readOnly = task.Blueprint.Work is WorkSpec.Agent { Access: AgentAccess.ReadOnly } or WorkSpec.Review;
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
            if (work is not IConverses)
            {
                return Block(new StartProblem.NoConversation());
            }

            prompt = resume.Message;
        }
        else if (task.Blueprint.Fields.FirstOrDefault(field => field.Required && string.IsNullOrWhiteSpace(task.Field(field.Key))) is { } missing)
        {
            return Block(new StartProblem.FieldMissing(missing.Label));
        }
        else
        {
            switch (work.Next(new NodeContext(task, "") { Planning = planning, Subject = subject }, null))
            {
                case NodeStep.RunTurn turn:
                    prompt = turn.Prompt;
                    break;
                case NodeStep.Fail:
                    return Block(new StartProblem.NoSubject());
                default:
                    throw new UnreachableException("A fresh start runs a turn or fails.");
            }
        }

        var request = new LaunchRequest(id, settings.Reasoning, prompt) { ResumeSession = resume?.Session, ReadOnly = readOnly, Policy = ClientPolicy.For(client, readOnly, task.Conversation, task.Blueprint.Work is WorkSpec.Review, questions ?? new HostQuestions.Disabled()) };
        var plan = new LaunchPlan(Clients.Get(client), ready.Command, settings, request);
        if (ready.Command.UnsafeArgument(plan.Launch.Arguments) is { } argument)
        {
            return Block(new StartProblem.UnsafeArgument(client, argument));
        }

        return new StartVerdict.Allowed(plan);
    }

    private static StartVerdict.Blocked Block(StartProblem problem) => new(problem);
}
