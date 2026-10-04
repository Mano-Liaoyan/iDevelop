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

    internal static IEnumerable<ModelOption> Offered(ClientStatus status) => status is ClientStatus.Ready ready ? ready.Models : [];

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
