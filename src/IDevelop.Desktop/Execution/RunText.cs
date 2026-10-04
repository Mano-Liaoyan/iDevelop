using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>What the interface says about clients and attempts. Core's reasons are already sentences for the user.</summary>
public static class RunText
{
    public static StatusTone Tone(ClientStatus status) => status switch
    {
        ClientStatus.Ready => StatusTone.Complete,
        ClientStatus.Checking => StatusTone.Running,
        ClientStatus.Unready => StatusTone.Problem,
        ClientStatus.Missing => StatusTone.Neutral,
        _ => throw new UnreachableException(),
    };

    /// <summary>"Ready · 4 models", "Ready · 2 of 5 models", "Checking…", "Not installed", or "Not ready".</summary>
    public static string Summary(ClientStatus status) => status switch
    {
        ClientStatus.Ready ready when ready.Models.Count(model => model.Problem is null) is var usable && usable < ready.Models.Length =>
            $"Ready · {usable} of {Count(ready.Models.Length, "model")}",
        ClientStatus.Ready ready => $"Ready · {Count(ready.Models.Length, "model")}",
        ClientStatus.Checking => "Checking…",
        ClientStatus.Missing => "Not installed",
        ClientStatus.Unready => "Not ready",
        _ => throw new UnreachableException(),
    };

    /// <summary>Why a client is not ready, or why some of its models are not, such as a Pi provider that is signed out.</summary>
    public static string? Detail(ClientStatus status) => status switch
    {
        ClientStatus.Missing missing => missing.Reason,
        ClientStatus.Unready unready => unready.Reason,
        ClientStatus.Ready ready when ready.Models.Select(model => model.Problem).OfType<string>().Distinct().ToList() is { Count: > 0 } problems =>
            string.Join(" ", problems),
        _ => null,
    };

    /// <summary>The card's agent label, such as "Codex · GPT-5.5 · high", with the model's catalog name when this machine
    /// offers it and its id otherwise. "No agent" when the task has none.</summary>
    public static string AgentLabel(ExecutionSettings? settings, ClientStatus status)
    {
        if (settings is null)
        {
            return "No agent";
        }

        var model = settings.Model is { } id ? ExecutionChoices.OfferedModel(status, id)?.Name ?? id : null;
        return Dotted(Clients.Name(settings.Client), model, settings.Reasoning);
    }

    /// <summary>A client in the inspector's picker, with what keeps it from running.</summary>
    public static string ClientChoice(ClientId client, ClientStatus status) => status switch
    {
        ClientStatus.Ready => Clients.Name(client),
        ClientStatus.Checking => $"{Clients.Name(client)} · checking",
        ClientStatus.Missing => $"{Clients.Name(client)} · not installed",
        ClientStatus.Unready => $"{Clients.Name(client)} · not ready",
        _ => throw new UnreachableException(),
    };

    /// <summary>A model in the picker. A stored model that the client does not offer is marked, unless the client is still
    /// being checked and nothing is known yet.</summary>
    public static string ModelChoice(ModelOption model, bool offered, ClientStatus status) => (offered, status) switch
    {
        (false, ClientStatus.Checking) => model.Id,
        (false, _) => $"{model.Id} (not offered on this machine)",
        _ => model.Problem is null ? model.Name : $"{model.Name} (not ready)",
    };

    /// <summary>A level in the picker. A stored level that the model does not offer is marked.</summary>
    public static string ReasoningChoice(string level, bool? offered) => offered is false ? $"{level} (not offered)" : level;

    /// <summary>What the chosen client may do in the project folder without asking.</summary>
    public static string PermissionNote(ClientId client) => client switch
    {
        ClientId.ClaudeCode => "Claude Code may edit files in the project folder. It denies any command its settings do not already allow.",
        ClientId.Codex => "Codex may edit files in the project folder. Its commands run in its workspace sandbox.",
        ClientId.Pi => "Pi has no permission system. It may edit any file and run any command that your account can.",
        ClientId.Antigravity => "Antigravity CLI may edit files in the project folder. It blocks commands in its accept-edits mode.",
    };

    /// <summary>A cancelled attempt is neutral: the user chose that outcome.</summary>
    public static StatusTone Tone(AttemptRecord? attempt) => attempt is null ? StatusTone.Neutral : attempt.Status switch
    {
        AttemptStatus.Running => StatusTone.Running,
        AttemptStatus.Succeeded => StatusTone.Complete,
        AttemptStatus.Failed or AttemptStatus.Interrupted => StatusTone.Problem,
        AttemptStatus.Cancelled => StatusTone.Neutral,
    };

    /// <param name="elsewhere">Another window started the attempt.</param>
    public static string StatusLabel(AttemptRecord? attempt, bool elsewhere) => attempt switch
    {
        null => "Not run",
        { Status: AttemptStatus.Running } when elsewhere => "Running in another window",
        { Status: AttemptStatus.Running, Stopping: true } => "Stopping",
        _ => attempt.Status.ToString(),
    };

    /// <summary>One or two sentences for each reason a task cannot start.</summary>
    public static string Describe(StartProblem problem) => problem switch
    {
        StartProblem.NoAgent => "Choose an agent for this task first.",
        StartProblem.NoInstructions => "Write instructions for this task first.",
        StartProblem.NoModel p => $"Choose a {Clients.Name(p.Client)} model first.",
        StartProblem.ClientChecking p => $"iDevelop is still checking {Clients.Name(p.Client)}.",
        StartProblem.ClientMissing p => $"{Clients.Name(p.Client)} is not installed. {p.Reason}",
        StartProblem.ClientUnready p => $"{Clients.Name(p.Client)} is not ready. {p.Reason}",
        StartProblem.ModelNotOffered p => $"{Clients.Name(p.Client)} does not offer {p.Model} on this machine. Choose another model.",
        StartProblem.ModelUnready p => $"{p.Model} cannot run now. {p.Reason}",
        StartProblem.ReasoningNotOffered { Reasoning: null } p => $"Choose a reasoning level for {p.Model}.",
        StartProblem.ReasoningNotOffered { Offered.IsEmpty: true } p =>
            $"{p.Model} takes no reasoning level. Choose another model and then {p.Model} again to clear {p.Reasoning}.",
        StartProblem.ReasoningNotOffered p => $"{p.Model} does not offer the {p.Reasoning} reasoning level. Choose {string.Join(", ", p.Offered)}.",
        StartProblem.UnsafeArgument p =>
            $"{Clients.Name(p.Client)} runs through cmd.exe, which could misread {p.Argument}, so iDevelop will not start it.",
        StartProblem.UncProjectFolder p =>
            $"{Clients.Name(p.Client)} runs through cmd.exe, which cannot work in a network folder and would run it in the Windows folder instead. Open the project from a drive letter to run this task.",
        StartProblem.AlreadyRunning p => $"\"{p.Title}\" is already running.",
        StartProblem.RunInAnotherWindow => "Another iDevelop window is starting this task.",
        StartProblem.CannotRecord p => p.Reason,
        _ => throw new UnreachableException(),
    };

    /// <summary>One or two sentences for each reason a message cannot go to a task's agent.</summary>
    public static string Describe(SendProblem problem) => problem switch
    {
        SendProblem.EmptyMessage => "Write a message first.",
        SendProblem.NeverRan => "Run this task first. Then you can write to its agent.",
        SendProblem.NoSession p => $"{Clients.Name(p.Client)} reported no session in the last run, so there is nothing to continue. Run the task again.",
        SendProblem.NoSessionYet p => $"{Clients.Name(p.Client)} has not reported its session yet. Send again in a moment.",
        SendProblem.ClientChanged p =>
            $"The last run used {Clients.Name(p.Ran)}, and this task now uses {Clients.Name(p.Now)}. Run the task to start a {Clients.Name(p.Now)} session.",
        SendProblem.Ending p => $"\"{p.Title}\" is finishing. Send your message again to continue it.",
        SendProblem.CannotStart p => Describe(p.Problem),
        _ => throw new UnreachableException(),
    };

    /// <summary>One or two sentences for each reason a task's session cannot go to a terminal.</summary>
    public static string Describe(TerminalProblem problem) => problem switch
    {
        TerminalProblem.NeverRan => "Run this task first.",
        TerminalProblem.NoSession p => $"{Clients.Name(p.Client)} reported no session in the last run, so there is nothing to open.",
        TerminalProblem.TurnRunning p => $"\"{p.Title}\" is running. Open it in a terminal after it ends.",
        TerminalProblem.Blocked p => Describe(p.Problem),
        _ => throw new UnreachableException(),
    };

    public static string HandedOff(TerminalResult.HandedOff handedOff) =>
        $"Copied {handedOff.Command}. Paste it in a terminal to continue this session in {handedOff.Folder}.";

    public static string NotCopied(TerminalResult.HandedOff handedOff) =>
        $"iDevelop could not copy the command. To continue this session in {handedOff.Folder}, run it in a terminal: {handedOff.Command}";

    /// <summary>What happened to a turn before the latest one, and why, when it did not succeed.</summary>
    public static string? EarlierTurnNote(TurnRecord turn) => turn.Outcome switch
    {
        TurnOutcome.Stopped => "You stopped this turn.",
        TurnOutcome.Failed => Sentences("This turn failed.", turn.Detail),
        TurnOutcome.Interrupted => Sentences("This turn was interrupted.", turn.Detail),
        TurnOutcome.Running or TurnOutcome.Succeeded => null,
    };

    /// <summary>The heading over the person's messages that wait for a turn, or that a settled attempt never sent.</summary>
    public static string? WaitingCaption(AttemptRecord attempt) => attempt switch
    {
        { Queued.IsEmpty: true } => null,
        { Status: AttemptStatus.Running } => "Waiting for the turn to end",
        _ => "Not sent",
    };

    public static string? TerminalNote(AttemptRecord attempt) => attempt.Terminal is { } handoff
        ? $"Opened in a terminal in {handoff.Folder} at {handoff.At.ToLocalTime():t}. Turns taken there are not in iDevelop's record."
        : null;

    /// <summary>"Requested Codex · gpt-5.5 · high", then what the client reported when that differs.</summary>
    public static string Configuration(AttemptRecord attempt)
    {
        var requested = attempt.Requested;
        var text = $"Requested {Dotted(Clients.Name(requested.Client), requested.Model, requested.Reasoning)}.";
        var model = attempt.ReportedModel ?? requested.Model;
        var reasoning = attempt.ReportedReasoning ?? requested.Reasoning;
        return model == requested.Model && reasoning == requested.Reasoning
            ? text
            : $"{text} Reported {Dotted(model, reasoning)}.";
    }

    /// <summary>
    /// An attempt's line in a conversation of several attempts, such as "Interrupted · Started 10/5/2026 2:00 PM · took 8 s".
    /// </summary>
    /// <param name="elsewhere">Another window runs the attempt.</param>
    public static string ExchangeLine(AttemptRecord attempt, bool elsewhere) => $"{StatusLabel(attempt, elsewhere)} · {Timing(attempt)}";

    public static string Timing(AttemptRecord attempt)
    {
        var started = $"Started {attempt.RequestedAt.ToLocalTime():g}";
        return attempt.EndedAt is { } ended ? $"{started} · took {Elapsed(ended - attempt.RequestedAt)}" : started;
    }

    /// <summary>"8 s", "1 min 05 s", or "2 h 05 min".</summary>
    public static string Elapsed(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => $"{Math.Max(0, (int)span.TotalSeconds)} s",
        { TotalHours: < 1 } => $"{span.Minutes} min {span.Seconds:00} s",
        _ => $"{(int)span.TotalHours} h {span.Minutes:00} min",
    };

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The sentences that are present, joined by spaces.</summary>
    private static string Sentences(params string?[] sentences) => string.Join(" ", sentences.OfType<string>());

    /// <summary>The parts that are present, joined by middle dots.</summary>
    private static string Dotted(params string?[] parts) => string.Join(" · ", parts.OfType<string>());
}
