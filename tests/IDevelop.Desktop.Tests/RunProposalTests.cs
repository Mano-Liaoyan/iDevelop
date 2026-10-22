using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.GenerateTests;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>A run's planner proposes, the tasks after it wait, and the person accepts the proposal into the run from the app (#84, #99).</summary>
[Collection(ProcessCollection.Name)]
public sealed class RunProposalTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    /// <summary>Adds "Export API" after the planner, on the agent the planner chose for it.</summary>
    private const string AddsApi = """
        Here is the plan.

        ```idevelop
        {"status": "proposal",
         "add": [{"id": "api", "type": "type-1", "title": "Export API", "fields": {"instructions": "Add the CSV endpoint."},
                  "agent": {"client": "codex", "model": "gpt-5.5", "reasoning": "low", "reason": "Routine."}}],
         "connect": [{"from": "planner", "to": "api"}]}
        ```
        """;

    /// <summary>A built-in Plan, then "Ship" after it.</summary>
    private static WorkflowRunFixture Planned(ConversationMode conversation = ConversationMode.MayAsk)
    {
        var plan = new WorkflowEdit.PlaceNode(A, BuiltInBlueprints.Plan, new CanvasPoint(105, 90))
        {
            Title = "Plan",
            Fields = ImmutableDictionary<string, string>.Empty.Add("goal", "Add CSV export."),
            Settings = new NodeSettings(WorkflowRunFixture.Codex, conversation),
        };
        var f = new WorkflowRunFixture(plan, Task(B, "Ship", 405), Dependency(A, B));
        return f.Answer("Plan", f.Says(AddsApi)).Answer("Export API", f.Says("API ready.")).Answer("Ship", f.Says("Shipped."));
    }

    private static TaskNodeViewModel Node(Shell shell, string title) => shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Title == title);

    /// <summary>Starts the run, selects the planner, and waits until its proposal can be accepted.</summary>
    private static void ShowProposal(Shell shell)
    {
        shell.StartRun();
        shell.Click(shell.Header(shell.Node("Plan")));
        shell.WaitUntil(() => shell.Has<Button>("AcceptProposal") && shell.Find<Button>("AcceptProposal").IsEffectivelyEnabled, "the plan's proposal shows",
            () => $"The run is {shell.WorkflowRun?.StatusLabel}.");
    }

    private static bool Amended(WorkflowRunFixture f) => f.Record().Receipts.Values.Any(entry => entry.Event is RunEvent.Amended);

    [AvaloniaFact]
    public void A_run_planner_waits_on_its_proposal_and_Accept_amends_the_run_with_the_agents_the_review_shows()
    {
        using var f = Planned();
        var shell = f.Window();

        ShowProposal(shell);

        Assert.Equal(NodeRole.Proposing, Node(shell, "Plan").Role);
        Assert.Equal("Proposal · 1 task", shell.CardText("Plan", "CardStatus"));
        Assert.Equal(TaskState.Pending, shell.WorkflowRun!.View.Tasks[B].State);
        Assert.True(shell.WorkflowRun.IsWaitingForPerson);
        Assert.Equal(("Export API", "Codex · GPT-5.5 · low", "Routine.", null), Assert.Single(Review(shell)));
        Assert.Equal(["Codex · GPT-5.5 · low Export API"], Ghosts(shell));
        Assert.Equal("Accept", Shell.TextOf(shell.InView<Button>("AcceptProposal")));
        Assert.False(shell.Find<Button>("ProposalAcceptFinish").IsVisible);
        shell.Click(EditButton(shell, "Export API"));
        shell.Pick("ProposalAgentReasoning", "medium");

        shell.Click(shell.InView<Button>("AcceptProposal"));

        shell.WaitForStatus("Completed");
        var chosen = WorkflowRunFixture.Codex with { Reasoning = "medium" };
        var added = f.Record().Revision.Snapshot.Tasks.Values.Single(task => task.Title == "Export API");
        Assert.Equal(chosen, added.Execution);
        Assert.Equal((1, 1, 1), (f.Launches("Plan"), f.Launches("Export API"), f.Launches("Ship")));
        // The document follows the run as an unsaved edit, and Undo takes nothing back from the run.
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.Equal(chosen, canvas.Workflow.Tasks[added.Id].Execution);
        Assert.True(shell.ShowsUnsavedChanges);
        Assert.False(shell.Has<StackPanel>("Proposal"));
        shell.Click(shell.Find<Button>("Undo"));
        Assert.DoesNotContain(added.Id, canvas.Workflow.Tasks.Keys);
        Assert.Contains(added.Id, f.Record().Revision.Snapshot.Tasks.Keys);
        Assert.False(shell.Has<StackPanel>("Proposal"));
    }

    [AvaloniaFact]
    public void Dismiss_lets_the_tasks_after_a_run_planner_go_on_without_its_proposal()
    {
        using var f = Planned();
        var shell = f.Window();
        ShowProposal(shell);

        shell.Click(shell.InView<Button>("DismissProposal"));

        shell.WaitForStatus("Completed");
        Assert.False(Amended(f));
        Assert.Equal((1, 0, 1), (f.Launches("Plan"), f.Launches("Export API"), f.Launches("Ship")));
        Assert.Equal(2, shell.Window.ViewModel.Canvas!.Workflow.Tasks.Count);
        Assert.False(shell.Has<StackPanel>("Proposal"));
    }

    [AvaloniaFact]
    public void A_Chat_planner_in_a_run_takes_Accept_and_Finish_which_amends_the_run_and_marks_it_done()
    {
        using var f = Planned(ConversationMode.Chat);
        var shell = f.Window();
        ShowProposal(shell);
        Assert.True(shell.Find<Button>("ProposalAcceptFinish").IsEffectivelyVisible);

        shell.Click(shell.InView<Button>("ProposalAcceptFinish"));

        shell.WaitUntil(() => shell.WorkflowRun!.StatusLabel == "Completed", "the run completes", () => $"It is {shell.WorkflowRun!.StatusLabel}: {shell.Window.ViewModel.Status}, amended {Amended(f)}, enabled {shell.Find<Button>("ProposalAcceptFinish").IsEffectivelyEnabled}");
        Assert.True(Amended(f));
        Assert.Equal((1, 1, 1), (f.Launches("Plan"), f.Launches("Export API"), f.Launches("Ship")));
    }

    [AvaloniaFact]
    public void After_Stop_the_proposal_stays_and_Accept_edits_only_the_workflow_for_the_next_run()
    {
        using var f = Planned();
        var shell = f.Window();
        ShowProposal(shell);

        shell.Click(shell.Find<Button>("StopWorkflow"));

        shell.WaitForStatus("Stopped");
        shell.WaitUntil(() => Shell.TextOf(shell.InView<Button>("AcceptProposal")) == "Accept for Next Run", "Accept edits only the next run",
            () => $"It reads {Shell.TextOf(shell.InView<Button>("AcceptProposal"))}.");
        Assert.Equal("The run has stopped, so Accept changes only the workflow for its next run.", shell.Text("ProposalRunNote"));
        Assert.False(shell.Find<Button>("ProposalAcceptFinish").IsVisible);
        shell.Click(shell.InView<Button>("AcceptProposal"));

        Assert.Contains(shell.Window.ViewModel.Canvas!.Workflow.Tasks.Values, task => task.Title == "Export API");
        Assert.False(Amended(f));
        Assert.Equal(0, f.Launches("Export API"));
    }

    [AvaloniaFact]
    public void A_window_that_does_not_control_the_run_shows_the_proposal_and_its_Accept_edits_only_the_next_run()
    {
        using var f = Planned();
        var first = f.Window();
        ShowProposal(first);
        var second = f.Window();
        second.WaitUntil(() => second.WorkflowRun is not null, "the second window shows the run");

        second.Click(second.Header(second.Node("Plan")));

        second.WaitUntil(() => second.Has<Button>("AcceptProposal"), "the plan's proposal shows in the second window",
            () => $"The run is {second.WorkflowRun?.StatusLabel}.");
        Assert.Equal("Accept for Next Run", Shell.TextOf(second.InView<Button>("AcceptProposal")));
        Assert.Equal("Another window controls this run, so Accept changes only the workflow for its next run.", second.Text("ProposalRunNote"));
        first.Click(first.InView<Button>("AcceptProposal"));
        first.WaitForStatus("Completed");
    }

    [AvaloniaFact]
    public void A_workflow_with_other_edits_keeps_them_and_says_the_run_took_the_proposal_without_it()
    {
        using var f = Planned();
        var shell = f.Window();
        ShowProposal(shell);
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.IsType<EditResult.Applied>(canvas.Document.Apply(new WorkflowEdit.EditTitle(B, "Ship it")));

        shell.Click(shell.InView<Button>("AcceptProposal"));

        shell.WaitForStatus("Completed");
        Assert.True(Amended(f));
        Assert.DoesNotContain(canvas.Workflow.Tasks.Values, task => task.Title == "Export API");
        Assert.Equal("The run took the proposal. Added 1 task there, but the workflow has other changes, so it stays as it is.",
            shell.Window.ViewModel.Status);
    }

    [AvaloniaFact]
    public void A_dismissed_proposal_stays_closed_in_a_window_opened_afterwards()
    {
        using var f = Planned();
        // Ship waits for its gate, so the run is still going when the later window opens it.
        f.Answer("Ship", f.Says("Shipped.", gate: "ship"));
        var shell = f.Window();
        ShowProposal(shell);
        shell.Click(shell.InView<Button>("DismissProposal"));
        shell.WaitForCard("Ship", "Running");

        var later = f.Window();
        later.WaitUntil(() => later.WorkflowRun is not null, "the later window shows the run");
        later.Click(later.Header(later.Node("Plan")));
        later.Render();

        Assert.False(later.Has<StackPanel>("Proposal"));
        Assert.NotEqual(NodeRole.Proposing, Node(later, "Plan").Role);
        f.Open("ship");
        shell.WaitForStatus("Completed");
    }
}
