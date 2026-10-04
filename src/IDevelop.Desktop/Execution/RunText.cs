using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>PlanWeave's four status tones. Controls take them as the style classes running, complete, and problem.</summary>
public enum StatusTone { Neutral, Running, Complete, Problem }

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

        var model = settings.Model is { } id ? Offered(status).FirstOrDefault(option => option.Id == id)?.Name ?? id : null;
        return string.Join(" · ", new[] { Clients.Name(settings.Client), model, settings.Reasoning }.OfType<string>());
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

    public static string ModelChoice(ModelOption model, bool offered) =>
        !offered ? $"{model.Id} (not offered on this machine)" : model.Problem is null ? model.Name : $"{model.Name} (not ready)";

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
        StartProblem.AlreadyRunning p => $"\"{p.Title}\" is running, and a project runs one task at a time.",
        StartProblem.RunInAnotherWindow => "Another iDevelop window is starting a task in this project.",
        StartProblem.CannotRecord p => p.Reason,
        _ => throw new UnreachableException(),
    };

    /// <summary>"Requested Codex · gpt-5.5 · high", then what the client reported when that differs.</summary>
    public static string Configuration(AttemptRecord attempt)
    {
        var requested = attempt.Requested;
        var text = $"Requested {string.Join(" · ", new[] { Clients.Name(requested.Client), requested.Model, requested.Reasoning }.OfType<string>())}.";
        var model = attempt.ReportedModel ?? requested.Model;
        var reasoning = attempt.ReportedReasoning ?? requested.Reasoning;
        return model == requested.Model && reasoning == requested.Reasoning
            ? text
            : $"{text} Reported {string.Join(" · ", new[] { model, reasoning }.OfType<string>())}.";
    }

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

    internal static IEnumerable<ModelOption> Offered(ClientStatus status) => status is ClientStatus.Ready ready ? ready.Models : [];

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
