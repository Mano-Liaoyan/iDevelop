using Avalonia.Controls;
using IDevelop.Desktop.Execution;

namespace IDevelop.Desktop.Tests;

/// <summary>Helpers for Run Workflow's preflight, the workflow run's bar, and the tasks it owns.</summary>
internal sealed partial class Shell
{
    public RunPreflightViewModel? Preflight => Window.ViewModel.Canvas?.Preflight;

    public WorkflowRunViewModel? WorkflowRun => Window.ViewModel.Canvas?.Run;

    /// <summary>Opens the preflight from the toolbar and waits until it has read the project.</summary>
    public RunPreflightViewModel OpenPreflight()
    {
        WaitUntil(() => Has<Button>("RunWorkflow"), "the canvas shows Run Workflow");
        Click(Find<Button>("RunWorkflow"));
        WaitUntil(() => Preflight is { IsReady: true }, "the preflight reads the project");
        return Preflight!;
    }

    /// <summary>Starts a run from the preflight and waits until the bar shows it.</summary>
    public WorkflowRunViewModel StartRun()
    {
        OpenPreflight();
        Click(Find<Button>("PreflightStart"));
        WaitUntil(() => Preflight is null && WorkflowRun is not null, "the run starts");
        return WorkflowRun!;
    }

    public string RunStatus => Find<TextBlock>("RunStatus").Text ?? "";

    public string Text(string automationId) => Find<TextBlock>(automationId).Text ?? "";

    /// <summary>The texts of every element with the id, in visual order.</summary>
    public string[] TextsOf(string automationId) => [.. ById<TextBlock>(Window, automationId).Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "")];

    /// <summary>As <see cref="WaitUntil(Func{bool}, string)"/>, and a timeout says what <paramref name="state"/> found then.</summary>
    public void WaitUntil(Func<bool> condition, string what, Func<string> state)
    {
        try
        {
            WaitUntil(condition, what);
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting until {what}. {state()}");
        }
    }

    public void WaitForStatus(string status) =>
        WaitUntil(() => WorkflowRun?.StatusLabel == status, $"the run is {status}", () => $"It is {WorkflowRun?.StatusLabel}: {WorkflowRun?.Activity}");

    public void WaitForCard(string title, string status) =>
        WaitUntil(() => CardText(title, "CardStatus") == status, $"\"{title}\" shows {status}", () => $"It shows {CardText(title, "CardStatus")}.");
}
