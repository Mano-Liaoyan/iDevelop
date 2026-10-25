using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A task whose agent finished but whose result the run refused: the card says why, and the person runs the task again on
/// its own, in the run or after it stops, without running the tasks before it that succeeded. The tasks after it go on once
/// it has a result (ADR 0020).
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class RefusedTaskTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = TestTasks.Review;
    private static readonly TaskId D = new(Guid.Parse("019a9d2e-5d10-7a44-8c2e-1f5b7a9c0d44"));

    /// <summary>
    /// <c>A → B → D</c> and <c>A → C → D</c>, as a fan-out that joins again. The first time the run accepts C's result, C's attempt log gains a line after its
    /// closure, so the run refuses the acceptance for good.
    /// </summary>
    private static WorkflowRunFixture FanOut()
    {
        var f = new WorkflowRunFixture(Task(A, "A", 105), AppTempFolder.TaskAt(B, "B", 405, 40, WorkflowRunFixture.Codex, "Build B."),
            AppTempFolder.TaskAt(C, "C", 405, 260, WorkflowRunFixture.Codex, "Build C."), AppTempFolder.TaskAt(D, "D", 705, 150, WorkflowRunFixture.Codex, "Build D."), Dependency(A, B), Dependency(A, C), Dependency(B, D),
            Dependency(C, D));
        f.Answer("A", f.Writes("out-a.txt", "A\n", "A ready.")).Answer("B", f.Writes("out-b.txt", "B\n", "B ready."))
            .Answer("C", f.Writes("out-c.txt", "C\n", "C ready."), f.Writes("out-c.txt", "C2\n", "C ready again."))
            .Answer("D", f.Says("D ready."));
        var spoiled = 0;
        f.Probe = point =>
        {
            if (point != "coordinator.accept.before" || Volatile.Read(ref spoiled) != 0)
            {
                return;
            }

            var record = f.Record();
            if (RunProjection.LatestAttempts(record).GetValueOrDefault(C) is var attempt && attempt != default &&
                record.Closures.ContainsKey(attempt) && !record.Results.Any(result => result.Task == C) &&
                Interlocked.Exchange(ref spoiled, 1) == 0)
            {
                var folder = RunStore.Open(f.Project).AttemptFolder(record.Workflow, record.Id, C, attempt);
                File.AppendAllText(Path.Combine(folder, "events.jsonl"), "\n");
            }
        };
        return f;
    }

    private static void Select(Shell shell, string title) => shell.Click(shell.Header(shell.Node(title)));

    private static string Cards(Shell shell) => string.Join(", ", new[] { "A", "B", "C", "D" }.Select(title => shell.CardText(title, "CardStatus")));

    private static int[] Launches(WorkflowRunFixture f) => [f.Launches("A"), f.Launches("B"), f.Launches("C"), f.Launches("D")];

    /// <summary>Starts the workflow's run and waits until the run refuses C's result while B succeeded and D waits.</summary>
    private static Shell Refused(WorkflowRunFixture f)
    {
        var shell = f.Window();
        shell.StartRun();
        shell.WaitUntil(() => shell.CardText("C", "CardStatus") == "Not accepted" && shell.CardText("B", "CardStatus") == "Succeeded",
            "the run refuses C's result", () => $"{Cards(shell)}, run: {shell.RunStatus}");
        Assert.Equal("Needs attention", shell.RunStatus);
        return shell;
    }

    [AvaloniaFact]
    public void A_task_whose_result_the_run_refused_says_why_and_offers_Retry_which_runs_only_it_and_the_tasks_after_it()
    {
        using var f = FanOut();
        var shell = Refused(f);
        Select(shell, "C");

        Assert.Equal(WorkflowRunText.Unbroken("Its agent finished, but the run could not accept its result: its attempt's log no longer " +
            "matches what the run recorded. Retry runs it again."), shell.Text("RunTaskDetail"));
        shell.Click(shell.InView<Button>("RetryTask"));
        shell.Click(shell.InView<Button>("ConfirmRetry"));

        shell.WaitForStatus("Completed");
        Assert.Equal("Succeeded, Succeeded, Succeeded, Succeeded", Cards(shell));
        Assert.Equal([1, 1, 2, 1], Launches(f));
    }

    [AvaloniaFact]
    public void After_the_run_stops_the_Run_of_a_task_whose_result_it_refused_runs_only_it_and_the_tasks_after_it()
    {
        using var f = FanOut();
        var shell = Refused(f);
        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true }, "the run stops", () => shell.RunStatus);
        Select(shell, "C");
        var stopped = shell.WorkflowRun!.Address;

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Preflight is { IsReady: true }, "the preflight of \"C\" reads the project", () => $"Notice: {shell.Status}");
        Assert.Equal(["C", "D"], shell.TextsOf("PreflightTask"));
        shell.Click(shell.Find<Button>("PreflightStart"));

        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true } run && run.Address != stopped, "the run of \"C\" settles",
            () => $"{Cards(shell)}, run: {shell.RunStatus}");
        Assert.Equal("Completed", shell.RunStatus);
        Assert.Equal("Succeeded, Succeeded, Succeeded, Succeeded", Cards(shell));
        Assert.Equal([1, 1, 2, 1], Launches(f));
    }
}
