using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A workflow run's conversations and approvals, reached from its cards and the waiting pill, and the run's activity while
/// its project is not shown or another window controls it.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class WorkflowRunNavigationTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId G = TestTasks.Review;

    /// <summary>A Chat task A, which waits for the person after each turn, then B.</summary>
    private static WorkflowRunFixture ChatChain()
    {
        var f = new WorkflowRunFixture(Task(A, "A", 105, ConversationMode.Chat), Task(B, "B", 405), Dependency(A, B));
        f.Answer("A", f.Says("Which file?"), f.Says("Done.", "session-1")).Answer("B", f.Says("B ready.")).Route("Use the fixture", "A");
        return f;
    }

    private static WorkflowRunFixture Gated()
    {
        var f = new WorkflowRunFixture(Task(A, "A", 105), Approval(G, "Approve", 405), Dependency(A, G));
        f.Answer("A", f.Says("A ready."));
        return f;
    }

    [AvaloniaFact]
    public void A_task_that_waits_in_the_run_is_counted_and_its_conversation_goes_through_the_run()
    {
        using var f = ChatChain();
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Waiting for you");

        Assert.Equal(("Waiting", "\"A\" waits for you."), (shell.RunStatus, shell.Text("RunActivity")));
        Assert.Equal("1 task waits for you", shell.Text("WaitingCount"));
        Assert.True(shell.WorkflowShows("seed", "Workflow", "WorkflowWaiting"));
        Assert.True(shell.ProjectShows("seed", "ProjectWaiting"));
        shell.Click(shell.Find<Button>("NextWaiting"));
        Assert.Equal("A", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.Equal("Waiting for you", shell.InView<TextBlock>("RunTaskStatus").Text);
        Assert.True(shell.Find<TextBlock>("RunConversationNote").IsEffectivelyVisible);
        Assert.False(shell.Find<TextBox>("Composer").IsEffectivelyVisible);

        shell.Click(shell.InCard<Button>("A", "CardAttention"));

        var conversation = shell.Window.ViewModel.Conversation!;
        shell.WaitUntil(() => conversation.SelectedAttempt is not null, "the conversation lists its attempts");
        Assert.Contains("· Run 1 ·", conversation.SelectedAttempt!.Label);
        shell.Click(shell.Find<TextBox>("ConversationComposer"));
        shell.Type("Use the fixture");
        shell.WaitUntil(() => shell.Find<Button>("ConversationSend").IsEffectivelyEnabled, "the run takes a reply");
        shell.Click(shell.Find<Button>("ConversationSend"));

        shell.WaitUntil(() => f.Launches("A") == 2 && shell.CardText("A", "CardStatus") == "Waiting for you", "the reply's turn ends");
        Assert.Equal("Use the fixture", f.Prompt("A", 2));
        shell.WaitUntil(() => shell.Find<Button>("ConversationMarkDone").IsEffectivelyEnabled, "the run takes Mark done");
        shell.Click(shell.Find<Button>("ConversationMarkDone"));
        shell.WaitForStatus("Completed");
        Assert.Equal((2, 1), (f.Launches("A"), f.Launches("B")));
        Assert.False(shell.Has<Button>("NextWaiting") && shell.Find<Button>("NextWaiting").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void An_approval_waits_for_the_person_and_Approve_completes_the_run()
    {
        using var f = Gated();
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Approve", "Waiting for approval");

        Assert.Equal("\"Approve\" waits for your approval.", shell.Text("RunActivity"));
        shell.Click(shell.InCard<Button>("Approve", "CardAttention"));
        Assert.Equal("Approve", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.Equal(("Waiting for approval", "Request 1"), (shell.InView<TextBlock>("GateStatus").Text, shell.Text("GateRequest")));
        shell.WaitUntil(() => shell.Find<TextBox>("GateReport").Text is { Length: > 0 }, "the panel reads what approving hands on");
        Assert.Equal("## A (dependency)\n\nA ready.", shell.Find<TextBox>("GateReport").Text?.TrimEnd());

        shell.Click(shell.InView<Button>("ApproveGate"));

        shell.WaitForStatus("Completed");
        Assert.Equal("Approved", shell.CardText("Approve", "CardStatus"));
        Assert.Equal("Approved", shell.InView<TextBlock>("GateStatus").Text);
        Assert.False(shell.Find<Button>("ApproveGate").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Send_back_holds_the_approval_with_its_reason()
    {
        using var f = Gated();
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Approve", "Waiting for approval");
        shell.Click(shell.Find<Button>("NextWaiting"));
        Assert.False(shell.InView<Button>("SendBackGate").IsEffectivelyEnabled);

        shell.Click(shell.InView<TextBox>("GateReason"));
        shell.Type("Needs tests");
        shell.Click(shell.InView<Button>("SendBackGate"));

        shell.WaitForCard("Approve", "Sent back");
        Assert.Equal("Sent back: Needs tests", shell.InView<TextBlock>("GateSentBackReason").Text);
        Assert.Equal("Needs attention", shell.WorkflowRun!.StatusLabel);
        Assert.Equal("\"Approve\": Sent back.", shell.Text("RunActivity"));
    }

    [AvaloniaFact]
    public void A_second_window_shows_the_request_but_only_the_controlling_window_answers_it()
    {
        using var f = Gated();
        var first = f.Window();
        first.StartRun();
        first.WaitForCard("Approve", "Waiting for approval");

        var second = f.Window();
        second.WaitUntil(() => second.WorkflowRun is not null, "the second window shows the run");
        second.Click(second.InCard<Button>("Approve", "CardAttention"));

        Assert.Equal("Waiting for approval", second.InView<TextBlock>("GateStatus").Text);
        Assert.False(second.Find<Button>("ApproveGate").IsEffectivelyEnabled);
        first.Click(first.Find<Button>("NextWaiting"));
        Assert.True(first.InView<Button>("ApproveGate").IsEffectivelyEnabled);
        first.Click(first.Find<Button>("ApproveGate"));
        first.WaitForStatus("Completed");
    }

    [AvaloniaFact]
    public void A_busy_journal_is_tried_again_and_then_shown_and_a_later_Approve_records_one_answer()
    {
        using var f = Gated();
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Approve", "Waiting for approval");
        shell.Click(shell.Find<Button>("NextWaiting"));

        using (var held = new FileStream(Path.Combine(f.Project, ".idp", "runs", "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            shell.Click(shell.InView<Button>("ApproveGate"));
            Assert.False(shell.Find<Button>("ApproveGate").IsEffectivelyEnabled);
            shell.WaitUntil(() => shell.Has<TextBlock>("GateNotice") && shell.Find<TextBlock>("GateNotice").IsEffectivelyVisible, "the refusal shows");
            Assert.Equal("The run's records are busy. Try again in a moment.", shell.Text("GateNotice"));
            Assert.Equal("Waiting for approval", shell.CardText("Approve", "CardStatus"));
        }

        shell.Click(shell.InView<Button>("ApproveGate"));
        shell.WaitForStatus("Completed");
        var run = shell.WorkflowRun!;
        var record = Assert.IsType<RunRead.Loaded>(RunStore.Open(f.Project).Read(run.Workflow, run.Address.Run)).Record;
        Assert.Single(record.Gates.Values, gate => gate.Decision is not null);
    }

    [AvaloniaFact]
    public void A_journal_that_frees_up_while_Approve_retries_records_the_answer()
    {
        using var f = Gated();
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Approve", "Waiting for approval");
        shell.Click(shell.Find<Button>("NextWaiting"));
        var held = new FileStream(Path.Combine(f.Project, ".idp", "runs", "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(1500)).ContinueWith(_ => held.Dispose());

        shell.Click(shell.InView<Button>("ApproveGate"));

        shell.WaitForStatus("Completed");
        Assert.False(shell.Find<TextBlock>("GateNotice").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Switching_to_another_project_stops_nothing_and_the_rows_show_the_run()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105));
        f.Answer("A", f.Says("A ready.", gate: "a"));
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Running");

        shell.Window.ViewModel.Open(f.Other());
        shell.Render();

        Assert.Equal("other - iDevelop", shell.Window.Title);
        Assert.False(shell.Find<Control>("WorkflowRunBar").IsEffectivelyVisible);
        Assert.True(shell.ProjectShows("seed", "ProjectRunning"));
        Assert.True(shell.WorkflowShows("seed", "Workflow", "WorkflowRunning"));
        f.Open("a");
        var seed = shell.Window.ViewModel.Projects.Single(project => project.Name == "seed");
        shell.WaitUntil(() => seed.Workflows[0].Run?.StatusLabel == "Completed", "the hidden run completes");
        Assert.False(shell.ProjectShows("seed", "ProjectRunning"));

        shell.Click(shell.WorkflowRow("seed", "Workflow"));
        Assert.Equal(("Completed", "Succeeded"), (shell.RunStatus, shell.CardText("A", "CardStatus")));
    }

    [AvaloniaFact]
    public void A_second_window_reads_the_run_and_takes_it_over_with_its_conversation_once_the_first_lets_go()
    {
        using var f = ChatChain();
        var first = f.Window();
        first.StartRun();
        first.WaitForCard("A", "Waiting for you");

        var second = f.Window();
        second.WaitUntil(() => second.WorkflowRun is not null, "the second window shows the run");
        Assert.Equal(("Controlled by another window", "Waiting for you"), (second.RunStatus, second.CardText("A", "CardStatus")));
        Assert.False(second.Find<Button>("StopWorkflow").IsEffectivelyVisible);
        second.Click(second.InCard<Button>("A", "CardAttention"));
        var reading = second.Window.ViewModel.Conversation!;
        second.Click(second.Find<TextBox>("ConversationComposer"));
        second.Type("Use the fixture");
        Assert.False(second.Find<Button>("ConversationSend").IsEffectivelyEnabled);
        Assert.Equal(WorkflowRunCoordinator.ElsewhereMessage, reading.ComposerHint);

        var closing = first.Window.ViewModel.Projects[0].CloseAsync().AsTask();
        first.WaitUntil(() => closing.IsCompleted, "the first window lets go of the project");

        second.WaitForStatus("Paused");
        var owning = second.Window.ViewModel.Conversation!;
        Assert.NotSame(reading, owning);
        Assert.Equal("Use the fixture", owning.Draft);
        second.WaitUntil(() => second.Find<Button>("ConversationSend").IsEffectivelyEnabled, "the reopened conversation takes the reply");
        second.Click(second.Find<Button>("ConversationSend"));
        second.WaitUntil(() => owning.Draft == "", "the reply is queued");
        // The conversation takes the canvas's place, so the run bar shows again once it closes.
        second.Click(second.Find<Button>("CloseConversation"));
        second.Click(second.Find<Button>("ResumeRun"));
        second.WaitUntil(() => f.Launches("A") == 2 && second.CardText("A", "CardStatus") == "Waiting for you", "the reply's turn ends",
            () => $"Launches {f.Launches("A")}, card {second.CardText("A", "CardStatus")}, run {second.WorkflowRun?.StatusLabel}, " +
                $"notice {owning.Notice}, hint {owning.ComposerHint}, draft {owning.Draft}, problem {second.WorkflowRun?.Problem}");
        Assert.Equal("Use the fixture", f.Prompt("A", 2));
    }

    [AvaloniaFact]
    public void A_project_that_opens_with_an_active_run_shows_it_paused_and_Resume_goes_on()
    {
        using var f = ChatChain();
        var first = f.Window();
        first.StartRun();
        first.WaitForCard("A", "Waiting for you");
        var closing = first.Window.ViewModel.Projects[0].CloseAsync().AsTask();
        first.WaitUntil(() => closing.IsCompleted, "the first window lets go of the project");

        var shell = f.Window();

        shell.WaitUntil(() => shell.WorkflowRun is not null, "the window shows the run");
        Assert.Equal(("Paused", "Resume to start its next tasks in this window."), (shell.RunStatus, shell.Text("RunActivity")));
        Assert.True(shell.Find<Button>("ResumeRun").IsEffectivelyVisible);
        Assert.Equal("Waiting for you", shell.CardText("A", "CardStatus"));
        shell.Click(shell.Find<Button>("ResumeRun"));
        shell.WaitForStatus("Waiting");
        Assert.False(shell.Find<Button>("ResumeRun").IsEffectivelyVisible);
        Assert.Equal(1, f.Launches("A"));
    }

    [AvaloniaFact]
    public void Closing_the_window_with_an_active_run_asks_first_and_Stop_and_leave_records_the_stop()
    {
        using var f = ChatChain();
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("A", "Waiting for you");
        var address = shell.WorkflowRun!.Address;

        shell.Window.Close();
        shell.Render();

        Assert.Equal(["A run of the \"Workflow\" workflow is active. Stop it and leave?", "Stop and leave", "Keep running"], shell.DialogTexts());
        shell.Choose("StopAndLeave");
        shell.WaitUntil(() => !shell.Window.IsVisible, "the window closes");
        var record = Assert.IsType<RunRead.Loaded>(RunStore.Open(f.Project).Read(address.Workflow, address.Run)).Record;
        Assert.NotEqual(RunPhase.Approved, record.Phase);
    }
}
