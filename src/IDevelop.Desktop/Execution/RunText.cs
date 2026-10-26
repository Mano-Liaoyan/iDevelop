using System.Diagnostics;
using IDevelop.Clients;
using IDevelop.Nodes;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>What the interface says about clients. Core's reasons are already sentences for the user.</summary>
public static class RunText
{
    /// <summary>A failed skill listing lasts until the next check of the agents, whose button in the Agents section says this.</summary>
    public const string TryListingAgain = "Check agents again to list them once more.";

    public static StatusTone Tone(ClientStatus status) => status switch
    {
        ClientStatus.Ready => StatusTone.Complete,
        ClientStatus.Checking => StatusTone.Running,
        ClientStatus.Unready => StatusTone.Problem,
        ClientStatus.Missing => StatusTone.Neutral,
        _ => throw new UnreachableException(),
    };

    /// <summary>"Ready · 4 models", "Ready · 2 of 5 models", "Checking…", "Not installed", "Not signed in", or "Not ready".
    /// <see cref="Detail"/> holds the whole reason.</summary>
    public static string Summary(ClientStatus status) => status switch
    {
        ClientStatus.Ready ready when ready.Models.Count(model => model.Problem is null) is var usable && usable < ready.Models.Length =>
            $"Ready · {usable} of {Count(ready.Models.Length, "model")}",
        ClientStatus.Ready ready => $"Ready · {Count(ready.Models.Length, "model")}",
        ClientStatus.Checking => "Checking…",
        ClientStatus.Missing => "Not installed",
        ClientStatus.Unready { SignedOut: true } => "Not signed in",
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
        return Dotted(ClientCatalog.Name(settings.Client), model, settings.Reasoning);
    }

    /// <summary>
    /// The agent on two lines, as every task card and the proposal review show it: the client, with the provider of a
    /// model that names one, such as "Pi · deepseek", then the model and its level, such as "Claude Haiku 4.5 · low".
    /// The model line is null when the agent names neither. "No agent" when the task has none.
    /// </summary>
    public static (string Client, string? Model) AgentLines(ExecutionSettings? settings, ClientStatus status)
    {
        if (settings is null)
        {
            return ("No agent", null);
        }

        var client = ClientCatalog.Name(settings.Client);
        var offered = settings.Model is { } id ? ExecutionChoices.OfferedModel(status, id) : null;
        var model = offered?.Name ?? settings.Model;
        if (offered is { Provider: { } provider } && model is not null && model.EndsWith($" ({provider})", StringComparison.Ordinal))
        {
            client = Dotted(client, provider);
            model = model[..^(provider.Length + 3)];
        }

        return (client, Dotted(model, settings.Reasoning) is { Length: > 0 } line ? line : null);
    }

    /// <summary>A client in the inspector's picker, with what keeps it from running.</summary>
    public static string ClientChoice(ClientId client, ClientStatus status) => status switch
    {
        ClientStatus.Ready => ClientCatalog.Name(client),
        ClientStatus.Checking => $"{ClientCatalog.Name(client)} · checking",
        ClientStatus.Missing => $"{ClientCatalog.Name(client)} · not installed",
        ClientStatus.Unready => $"{ClientCatalog.Name(client)} · not ready",
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
    public static string PermissionNote(ClientId client, bool readOnly) => !readOnly ? PermissionNote(client) : client switch
    {
        ClientId.ClaudeCode => "This task only reads the project. Claude Code runs in plan mode.",
        ClientId.Codex => "This task only reads the project. Codex runs in its read-only sandbox.",
        ClientId.Pi => "This task only reads the project, and Pi has no read-only mode, so it does not start on Pi.",
        ClientId.Antigravity => "This task only reads the project. Antigravity CLI runs in plan mode.",
    };

    public static string PermissionNote(ClientId client) => client switch
    {
        ClientId.ClaudeCode => "Claude Code may edit files in the project folder. It denies any command its settings do not already allow.",
        ClientId.Codex => "Codex may edit files in the project folder. Its commands run in its workspace sandbox.",
        ClientId.Pi => "Pi has no permission system. It may edit any file and run any command that your account can.",
        ClientId.Antigravity => "Antigravity CLI may edit files in the project folder. It blocks commands in its accept-edits mode.",
    };

    /// <summary>The permission note in one short line under the pickers. The info glyph holds the whole note.</summary>
    public static string PermissionSummary(ClientId client, bool readOnly) => (client, readOnly) switch
    {
        (ClientId.Pi, true) => "Can't run read-only tasks.",
        (ClientId.Codex, true) => "Reads only, in a read-only sandbox.",
        (_, true) => "Reads only, in plan mode.",
        (ClientId.ClaudeCode, false) => "Edits files. Runs only allowed commands.",
        (ClientId.Codex, false) => "Edits files. Runs commands in a sandbox.",
        (ClientId.Pi, false) => "Edits files and runs any command.",
        (ClientId.Antigravity, false) => "Edits files. Runs no commands.",
    };

    /// <summary>The reviewer note in one short line.</summary>
    public static string ReviewerSummary(ClientId client) => client switch
    {
        ClientId.Pi => "Can't review without a read-only mode.",
        ClientId.Codex => "Reviews in a read-only sandbox.",
        ClientId.ClaudeCode or ClientId.Antigravity => "Reviews in plan mode.",
    };

    /// <summary>How the chosen client keeps a reviewer from editing the project.</summary>
    public static string ReviewerNote(ClientId client) => client switch
    {
        ClientId.ClaudeCode => "Claude Code reviews in plan mode, which edits no file.",
        ClientId.Codex => "Codex reviews in its read-only sandbox.",
        ClientId.Pi => "Pi has no read-only mode, so a review does not start on Pi.",
        ClientId.Antigravity => "Antigravity CLI reviews in plan mode, and a review turn that changes a file fails.",
    };

    public static string ConversationChoice(ConversationMode mode) => mode switch
    {
        ConversationMode.Autonomous => "Autonomous",
        ConversationMode.MayAsk => "May ask",
        ConversationMode.Chat => "Chat",
    };

    /// <summary>What each conversation mode does, under its picker.</summary>
    public static string ConversationNote(ConversationMode mode) => mode switch
    {
        ConversationMode.Autonomous => "The agent works without waiting for you. You can still write to it.",
        ConversationMode.MayAsk => "The agent may end a turn with a question and wait for your answer.",
        ConversationMode.Chat => "The node waits for you after every turn, until you approve its reply.",
    };

    /// <summary>The conversation note in one short line under its picker.</summary>
    public static string ConversationSummary(ConversationMode mode) => mode switch
    {
        ConversationMode.Autonomous => "Works without waiting for you.",
        ConversationMode.MayAsk => "May stop to ask you a question.",
        ConversationMode.Chat => "Waits for you after every turn.",
    };

    public static string Ago(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var age = now - at;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return age switch
        {
            { TotalMinutes: < 1 } => "Just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
            _ when local.Date == today => $"Today {local:t}",
            _ when local.Date == today.AddDays(-1) => $"Yesterday {local:t}",
            _ => $"{local:g}",
        };
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The parts that are present, joined by middle dots.</summary>
    private static string Dotted(params string?[] parts) => string.Join(" · ", parts.OfType<string>());
}
