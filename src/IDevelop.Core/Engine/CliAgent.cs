using System.Text.Json;
using System.Threading.Channels;
using IDevelop.Clients;
using IDevelop.Nodes;
using IDevelop.Processes;
using IDevelop.Workflows;

namespace IDevelop.Engine;

/// <summary>
/// A node whose agent is Claude Code, Codex, Pi, or Antigravity CLI. Each turn is one client process through the client
/// layer: <see cref="LaunchPlan"/> builds the launch, <see cref="ChildProcess"/> runs it, and the client's <see cref="TurnProtocol"/>
/// reads its output and writes its input. A turn runs in the folder the engine gives it: an editing node's worktree, or
/// the project folder for a node that only reads. Questions and permission requests are declined, so a question reaches
/// the person as text in the agent's reply.
/// </summary>
internal sealed class CliAgent(ClientDirectory clients, string projectFolder) : IAgent
{
    private static readonly TimeSpan WriteTime = TimeSpan.FromSeconds(5);

    /// <summary>How long a stopped client has to end its turn after the interrupt before its process tree is stopped.</summary>
    public TimeSpan ShutdownTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a client has to exit after it reported success, a clock that stands still while it lists background work (ADR 0030).</summary>
    public TimeSpan SuccessExitTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a turn with a skill choice waits for the project's skill listing that is under way (ADR 0025).</summary>
    public TimeSpan SkillWait { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>Why the node cannot start. A skill listing under way holds nothing back, because the turn waits for it.</summary>
    public string? Problem(TaskDefinition node) =>
        node.Blueprint.Fields.FirstOrDefault(field => field.Required && string.IsNullOrWhiteSpace(node.Field(field.Key))) is { } missing
            ? $"{missing.Label} is empty."
            : LaunchPlan.For(node, projectFolder, "", null, clients.Current, Skills(node) is SkillListing.Checking or null ? new SkillListing.Listed([]) : Skills(node)).Problem;

    public async Task<TurnOutcome> Turn(TaskDefinition node, string folder, string prompt, string? session, Action<AgentEvent> report, CancellationToken stop)
    {
        // Each turn uses the project folder's skill listing, the one the person chose from (ADR 0025).
        var skills = node.Execution is { Skills: not null } chosen ? await clients.SkillsAsync(chosen.Client, projectFolder, SkillWait, stop) : null;
        if (LaunchPlan.For(node, folder, prompt, session, clients.Current, skills) is not ({ } plan, _))
        {
            return new TurnOutcome.Failed(Problem(node) ?? "The agent cannot start.");
        }

        if (session is null && plan.Request.Policy?.Native is NativePolicy.Claude { Mode: "bypassPermissions" })
        {
            report(new AgentEvent.Notice("Every tool runs without asking for permission."));
        }

        if (session is null && SkillChoices.TurnNote(plan.Settings.Client, plan.Settings.Skills, skills) is { } note) report(new AgentEvent.Notice(note));

        ChildProcess process;
        try
        {
            process = ChildProcess.Start(plan.Command, plan.Launch.Arguments, folder, ProcessLifetime.Workflow);
        }
        catch (LaunchException e)
        {
            return new TurnOutcome.Failed(e.Message);
        }

        using (process)
        {
            try
            {
                return await Drive(node, plan, process, report, stop);
            }
            finally
            {
                // A drain that failed leaves no client behind.
                if (!process.HasExited) process.StopTree();
            }
        }
    }

    private SkillListing? Skills(TaskDefinition node) => node.Execution is { Skills: not null } chosen ? clients.Skills(chosen.Client, projectFolder) : null;

    /// <summary>
    /// One drain owns the turn: stdout lines, a stop, and the exit arrive on one channel in order, and it writes the
    /// protocol's frames to stdin one at a time. A stop sends the client's own interrupt when it has one and stops the
    /// process tree after <see cref="ShutdownTime"/>, or at once without one. After a success, a client that lists no background
    /// work and does not exit within <see cref="SuccessExitTime"/> is stopped, and the turn fails.
    /// </summary>
    private async Task<TurnOutcome> Drive(TaskDefinition node, LaunchPlan plan, ChildProcess process, Action<AgentEvent> report, CancellationToken stop)
    {
        var protocol = plan.Client.Protocol(plan.Request);
        var inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
        string? lastError = null;
        process.ReadStdout(line => inbox.Writer.TryWrite(line));
        process.ReadStderr(line => lastError = string.IsNullOrWhiteSpace(line) ? lastError : line);
        _ = Task.Run(async () =>
        {
            var code = await process.WaitForExitAsync();
            await process.WaitForOutputAsync();
            inbox.Writer.TryWrite(code);
        });
        using var registration = stop.Register(() => inbox.Writer.TryWrite(stop));

        AgentEvent? verdict = null;
        string? lastMessage = null;
        var stopping = false;
        var background = false;
        object? deadline = null;
        var shutDown = false;

        // The clock runs while the client reported success and lists no background work, and starts again in full after it.
        void Clock()
        {
            if (verdict is not AgentEvent.Succeeded || background || stopping)
            {
                deadline = null;
            }
            else if (deadline is null)
            {
                var token = deadline = new object();
                _ = Task.Delay(SuccessExitTime, CancellationToken.None).ContinueWith(_ => inbox.Writer.TryWrite(token), TaskScheduler.Default);
            }
        }

        async Task Output(ProtocolOutput output)
        {
            foreach (var e in output.Events)
            {
                switch (e)
                {
                    case AgentEvent.MessageDelta or AgentEvent.Message { Partial: true }:
                        break;
                    case AgentEvent.Message message:
                        // A subagent's message is its own and never the turn's reply (ADR 0030).
                        if (message.Subagent is null) lastMessage = message.Text;
                        report(e);
                        break;
                    case AgentEvent.BackgroundTasks tasks:
                        background = !tasks.Tasks.IsEmpty;
                        report(e);
                        break;
                    case AgentEvent.QuestionAsked question:
                        report(e);
                        await Output(protocol.Decline(question.RequestId));
                        break;
                    case AgentEvent.PermissionRequested permission:
                        report(e);
                        await Output(protocol.Decline(permission.RequestId));
                        break;
                    case AgentEvent.Succeeded or AgentEvent.Failed:
                        verdict = e;
                        report(e);
                        break;
                    default:
                        report(e);
                        break;
                }
            }

            foreach (var frame in output.Writes)
            {
                var oneShot = protocol is OneShotProtocol;
                // Only a one-shot prompt may take long to write, so only it gives way to a stop. The interrupt still goes out.
                await process.WriteInputAsync(oneShot ? frame : frame + "\n", close: false,
                    output.IsOneShotPrompt ? Timeout.InfiniteTimeSpan : WriteTime, output.IsOneShotPrompt ? stop : CancellationToken.None);
            }

            if (output.CloseInput)
            {
                await process.WriteInputAsync("", close: true, WriteTime, CancellationToken.None);
            }
        }

        await Output(protocol.Start());
        await foreach (var item in inbox.Reader.ReadAllAsync())
        {
            switch (item)
            {
                case string line when !string.IsNullOrWhiteSpace(line):
                    try
                    {
                        await Output(protocol.Read(line));
                    }
                    catch (JsonException)
                    {
                        report(new AgentEvent.Notice($"iDevelop could not read a line that {ClientCatalog.Name(plan.Settings.Client)} printed."));
                    }
                    catch (ProtocolException e)
                    {
                        verdict = new AgentEvent.Failed(e.Message);
                        report(verdict);
                        process.StopTree();
                    }

                    Clock();
                    break;
                case CancellationToken when !stopping:
                    stopping = true;
                    _ = Task.Delay(ShutdownTime, CancellationToken.None).ContinueWith(_ => process.StopTree(), TaskScheduler.Default);
                    if (protocol.Interrupt() is { } interrupt)
                    {
                        await Output(interrupt);
                    }
                    else
                    {
                        process.StopTree();
                    }

                    break;
                case int code:
                    // What the client left running when it exited stops with its turn.
                    await process.CleanUpAsync(TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);
                    return stopping ? new TurnOutcome.Stopped()
                        : shutDown ? new TurnOutcome.Failed($"{ClientCatalog.Name(plan.Settings.Client)} reported success but did not shut down.")
                        : Outcome(node, plan.Settings.Client, verdict, lastMessage, code, lastError);
                case var token when token == deadline && !stopping:
                    shutDown = true;
                    process.StopTree();
                    break;
            }
        }

        throw new InvalidOperationException("The turn's output ended before its exit.");
    }

    /// <summary>
    /// The client's own verdict decides, and a success still needs exit code 0. A success then waits for the person as the
    /// node's conversation mode says: a Chat node after every turn, a May ask node when its result block asks.
    /// </summary>
    private static TurnOutcome Outcome(TaskDefinition node, ClientId client, AgentEvent? verdict, string? lastMessage, int code, string? lastError)
    {
        var name = ClientCatalog.Name(client);
        switch (verdict)
        {
            case AgentEvent.Failed failed:
                return new TurnOutcome.Failed(failed.Reason);
            case AgentEvent.Succeeded succeeded when code == 0:
                var text = succeeded.Result ?? lastMessage;
                return node.Conversation switch
                {
                    ConversationMode.Chat => new TurnOutcome.Waits(NodeState.Reply, text),
                    ConversationMode.MayAsk when AgentPrompt.Question(text) is { } question => new TurnOutcome.Waits(question, text),
                    _ => new TurnOutcome.Done(text ?? ""),
                };
            case AgentEvent.Succeeded:
                return new TurnOutcome.Failed($"{name} reported success but exited with code {code}.");
            default:
                return new TurnOutcome.Failed(code == 0 ? $"{name} ended without a result."
                    : lastError is { } line ? $"{name} exited with code {code}: {line}" : $"{name} exited with code {code}.");
        }
    }
}
