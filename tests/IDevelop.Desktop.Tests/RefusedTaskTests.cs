using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A task whose step the run refused, before its agent ran or after it finished: the card says why, and the person runs the
/// task again on its own, in the run or after it stops, without running the tasks before it that succeeded. The tasks after
/// it go on once it has a result (ADR 0020).
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class RefusedTaskTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = TestTasks.Review;
    private static readonly TaskId D = new(Guid.Parse("019a9d2e-5d10-7a44-8c2e-1f5b7a9c0d44"));

    /// <summary>The log each fixture's probe spoiled.</summary>
    private static readonly ConcurrentDictionary<WorkflowRunFixture, string> Spoiled = new();

    /// <summary>
    /// <c>A → B → D</c> and <c>A → C → D</c>, as a fan-out that joins again. By default, the first time the run accepts C's
    /// result, C's attempt log gains a line after its closure, so the run refuses the acceptance for good. With
    /// <paramref name="beforeStart"/>, the run refuses C's start before its agent runs instead: a stray log waits in the
    /// folder of C's reserved attempt, which <see cref="Spoiled"/> names, until the test removes it.
    /// </summary>
    private static WorkflowRunFixture FanOut(bool beforeStart = false)
    {
        var f = new WorkflowRunFixture(Task(A, "A", 105), AppTempFolder.TaskAt(B, "B", 405, 40, WorkflowRunFixture.Codex, "Build B."),
            AppTempFolder.TaskAt(C, "C", 405, 260, WorkflowRunFixture.Codex, "Build C."), AppTempFolder.TaskAt(D, "D", 705, 150, WorkflowRunFixture.Codex, "Build D."),
            Dependency(A, B), Dependency(A, C), Dependency(B, D), Dependency(C, D));
        f.Answer("A", f.Writes("out-a.txt", "A\n", "A ready.")).Answer("B", f.Writes("out-b.txt", "B\n", "B ready."))
            .Answer("C", f.Writes("out-c.txt", "C\n", "C ready."), f.Writes("out-c.txt", "C2\n", "C ready again."))
            .Answer("D", f.Says("D ready."));
        var spoiled = 0;
        f.Probe = point =>
        {
            if (point != (beforeStart ? "runner.request.before" : "coordinator.accept.before") || Volatile.Read(ref spoiled) != 0)
            {
                return;
            }

            var record = f.Record();
            var attempt = RunProjection.LatestAttempts(record).GetValueOrDefault(C);
            if (attempt == default)
            {
                return;
            }

            var folder = RunStore.Open(f.Project).AttemptFolder(record.Workflow, record.Id, C, attempt);
            var log = Path.Combine(folder, "events.jsonl");
            var spoils = beforeStart ? !File.Exists(log) : record.Closures.ContainsKey(attempt) && !record.Results.Any(result => result.Task == C);
            if (spoils && Interlocked.Exchange(ref spoiled, 1) == 0)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(log, beforeStart ? "{}\n" : "\n");
                Spoiled[f] = log;
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

    private static void Stop(Shell shell)
    {
        shell.Click(shell.Find<Button>("StopWorkflow"));
        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true }, "the run stops", () => shell.RunStatus);
    }

    /// <summary>C's Run opens its preflight, which lists C and D, and the run it starts settles.</summary>
    private static void RunC(Shell shell)
    {
        var earlier = shell.WorkflowRun?.Address;
        Select(shell, "C");
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Preflight is { IsReady: true }, "the preflight of \"C\" reads the project", () => $"Notice: {shell.Status}");
        Assert.Equal(["C", "D"], shell.TextsOf("PreflightTask"));
        shell.Click(shell.Find<Button>("PreflightStart"));
        shell.WaitUntil(() => shell.WorkflowRun is { IsActive: false, View.PinsReleased: true } run && run.Address != earlier, "the run of \"C\" settles",
            () => $"{Cards(shell)}, run: {shell.RunStatus}");
        Assert.Equal("Completed", shell.RunStatus);
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
        Stop(shell);

        RunC(shell);

        Assert.Equal("Succeeded, Succeeded, Succeeded, Succeeded", Cards(shell));
        Assert.Equal([1, 1, 2, 1], Launches(f));
    }

    [AvaloniaFact]
    public void A_task_whose_start_the_run_refused_offers_Retry_which_starts_only_it_and_the_tasks_after_it()
    {
        using var f = FanOut(beforeStart: true);
        var shell = f.Window();
        shell.StartRun();
        shell.WaitUntil(() => shell.CardText("C", "CardStatus") == "Couldn't start" && shell.CardText("B", "CardStatus") == "Succeeded",
            "the run refuses C's start", () => $"{Cards(shell)}, run: {shell.RunStatus}");
        Assert.Equal("Needs attention", shell.RunStatus);
        Select(shell, "C");
        Assert.Equal(WorkflowRunText.Unbroken("Its attempt's log no longer matches what the run recorded. Retry tries it again."),
            shell.Text("RunTaskDetail"));
        var run = shell.WorkflowRun!.View;
        Assert.Equal(WorkflowRunCoordinator.HeldRetryMessage, WorkflowRunCoordinator.Held(run.Tasks[C], run.Snapshot));
        Assert.Equal([1, 1, 0, 0], Launches(f));

        // What spoiled the start is gone, so trying again starts C.
        File.Delete(Spoiled[f]);
        shell.Click(shell.InView<Button>("RetryTask"));
        Assert.Equal("Retry tries again to start \"C\", or the step the run refused. The tasks after it run once it has a result.",
            shell.Text("RetryConfirmation"));
        Assert.False(shell.ShowsAny("RetryNote"));
        shell.Click(shell.InView<Button>("ConfirmRetry"));

        shell.WaitForStatus("Completed");
        Assert.Equal("Succeeded, Succeeded, Succeeded, Succeeded", Cards(shell));
        Assert.Equal([1, 1, 1, 1], Launches(f));
    }

    [AvaloniaFact]
    public void After_a_restart_a_task_whose_result_a_stopped_run_refused_reads_as_not_accepted_and_its_Run_runs_it_again()
    {
        using var f = FanOut();
        var shell = Refused(f);
        Stop(shell);
        shell.Window.Close();
        shell.Render();

        var reopened = f.Window();
        reopened.WaitUntil(() => reopened.CardText("C", "CardStatus") == "Not accepted", "the reopened window reads the stopped run",
            () => Cards(reopened));
        Select(reopened, "C");
        Assert.Equal(WorkflowRunText.Unbroken("Its agent finished, but the run did not accept its result."), reopened.Text("RunTaskDetail"));

        RunC(reopened);

        Assert.Equal("Succeeded, Succeeded, Succeeded, Succeeded", Cards(reopened));
        Assert.Equal([1, 1, 2, 1], Launches(f));
    }
}
