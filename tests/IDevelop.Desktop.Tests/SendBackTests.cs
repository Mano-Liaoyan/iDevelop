using Avalonia.Controls;
using IDevelop.TestSupport;
using Avalonia.Headless.XUnit;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>After a send back: Ask again on the Approval node, and a Retry's note to the agent prefilled with the reason (#126).</summary>
[Collection(ProcessCollection.Name)]
public sealed class SendBackTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId G = TestTasks.Build;

    private static Shell SentBack(WorkflowRunFixture f)
    {
        var shell = f.Window();
        shell.StartRun();
        shell.WaitForCard("Approve", "Waiting for approval");
        shell.Click(shell.Find<Button>("NextWaiting"));
        shell.Click(shell.InView<TextBox>("GateReason"));
        shell.Type("Needs tests");
        shell.Click(shell.InView<Button>("SendBackGate"));
        shell.WaitForCard("Approve", "Sent back");
        return shell;
    }

    private static WorkflowRunFixture Gated()
    {
        var f = new WorkflowRunFixture(Task(A, "A", 105), Approval(G, "Approve", 405), Dependency(A, G));
        f.Answer("A", f.Says("A ready."), f.Says("A again."));
        return f;
    }

    [AvaloniaFact]
    public void Ask_again_asks_first_then_opens_a_new_request_on_the_same_inputs()
    {
        using var f = Gated();
        var shell = SentBack(f);
        Assert.False(shell.ShowsAny("AskAgainConfirmation"));

        shell.Click(shell.InView<Button>("AskGateAgain"));

        Assert.Equal("Ask again opens a new request on the same inputs. The sent-back request and its reason stay in the run's history.",
            shell.Text("AskAgainConfirmation"));
        shell.Click(shell.InView<Button>("KeepAskAgain"));
        Assert.False(shell.ShowsAny("AskAgainConfirmation"));
        shell.Click(shell.InView<Button>("AskGateAgain"));
        shell.Click(shell.InView<Button>("ConfirmAskAgain"));

        shell.WaitForCard("Approve", "Waiting for approval");
        shell.WaitUntil(() => shell.ShowsAny("GateRequest") && shell.Text("GateRequest") == "2 of this run", "the new request shows",
            () => shell.Text("GateRequest"));
        Assert.False(shell.ShowsAny("AskGateAgain"));
        Assert.Equal(1, f.Launches("A"));
        shell.Click(shell.InView<Button>("ApproveGate"));
        shell.WaitForStatus("Completed");
    }

    [AvaloniaFact]
    public void A_retry_of_the_task_before_a_sent_back_approval_opens_with_the_reason_as_its_note()
    {
        using var f = Gated();
        var shell = SentBack(f);
        shell.Click(shell.Header(shell.Node("A")));

        shell.Click(shell.InView<Button>("RetryTask"));

        Assert.Equal("Needs tests", shell.InView<TextBox>("RetryNote").Text);
        shell.Click(shell.InView<Button>("ConfirmRetry"));
        shell.WaitUntil(() => f.Launches("A") == 2, "A runs again", () => $"{f.Launches("A")} launches");
        shell.WaitForCard("Approve", "Waiting for approval");
        Assert.Contains("Needs tests", f.Prompt("A", 2));
    }
}
