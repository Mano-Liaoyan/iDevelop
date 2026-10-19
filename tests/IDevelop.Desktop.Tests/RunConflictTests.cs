using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.WorkflowRunFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A merge conflict that no Merge node takes waits for the person (#148): the card and the inspector of the task whose
/// predecessors conflict, and the run's bar for the final merge, open the conflict's worktree and mark it resolved, which
/// is refused while the conflict markers remain.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class RunConflictTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = new(Guid.Parse("019a9d2e-5d10-7a44-8e2b-9f1c3a6b4d44"));

    private static void Select(Shell shell, string title)
    {
        shell.Click(shell.Find<Button>("FitToScreen"));
        shell.Click(shell.Header(shell.Node(title)));
    }

    /// <summary>Opens nothing, and keeps the folder the canvas asked to open.</summary>
    private static List<string> Opened(Shell shell)
    {
        List<string> folders = [];
        shell.Window.ViewModel.Canvas!.OpenFolder = folder =>
        {
            folders.Add(folder);
            return System.Threading.Tasks.Task.FromResult(true);
        };
        return folders;
    }

    [AvaloniaFact]
    public void The_person_resolves_a_conflict_between_predecessors_from_the_inspector_and_the_task_starts_from_their_merge()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Task(B, "B", 405), Task(C, "C", 705), Dependency(A, C), Dependency(B, C));
        f.Answer("A", f.Writes("settings.txt", "A\n", "A ready.")).Answer("B", f.Writes("settings.txt", "B\n", "B ready."))
            .Answer("C", f.Says("C ready."));
        var shell = f.Window();
        var opened = Opened(shell);
        shell.StartRun();
        shell.WaitForCard("C", "Conflict");
        Select(shell, "C");
        Assert.True(shell.ShowsAny("OpenConflict"));

        // Before the worktree is open, nothing of the person's is there to commit.
        shell.Click(shell.InView<Button>("MarkResolved"));
        shell.WaitUntil(() => shell.Status == "Open the conflict's worktree first, and fix the files there.", "the notice asks to open it",
            () => shell.Status);

        shell.Click(shell.InView<Button>("OpenConflict"));
        shell.WaitUntil(() => opened.Count == 1, "the worktree opens", () => shell.Status);
        var file = Path.Combine(opened[0], "settings.txt");
        Assert.Contains("<<<<<<<", File.ReadAllText(file));
        Assert.Equal($"The conflict's worktree is open at {opened[0]}. Fix settings.txt there, then choose Mark resolved.", shell.Status);

        // While the markers remain, Mark resolved is refused with the reason.
        shell.Click(shell.InView<Button>("MarkResolved"));
        shell.WaitUntil(() => shell.Status == "settings.txt still has conflict markers. Fix it, then mark the conflict resolved.",
            "the notice names the file with markers", () => shell.Status);
        Assert.Equal(0, f.Launches("C"));

        File.WriteAllText(file, "A\nB\n");
        shell.Click(shell.InView<Button>("MarkResolved"));
        shell.WaitForStatus("Completed");
        Assert.Equal(1, f.Launches("C"));
        Assert.Equal("A\nB\n", File.ReadAllText(Path.Combine(f.Checkout(C), "settings.txt")));
        Assert.False(shell.ShowsAny("OpenConflict"));
    }

    [AvaloniaFact]
    public void The_expanded_card_of_the_conflicting_task_opens_its_worktree()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105), Task(B, "B", 405), Task(C, "C", 705), Dependency(A, C), Dependency(B, C));
        f.Answer("A", f.Writes("settings.txt", "A\n", "A ready.")).Answer("B", f.Writes("settings.txt", "B\n", "B ready."));
        var shell = f.Window();
        var opened = Opened(shell);
        shell.StartRun();
        shell.WaitForCard("C", "Conflict");
        Assert.False(shell.ShowsAny("CardOpenConflict"));
        shell.Click(shell.Find<Button>("FitToScreen"));
        shell.Click(shell.InCard<ToggleButton>("C", "CardExpand"));
        Assert.True(shell.ShowsAny("CardMarkResolved"));

        shell.Click(shell.InCard<Button>("C", "CardOpenConflict"));
        shell.WaitUntil(() => opened.Count == 1, "the worktree opens", () => shell.Status);
        // The cards of the tasks that merged cleanly offer nothing of it.
        Assert.False(ById(shell, "A", "CardOpenConflict"));
    }

    private static bool ById(Shell shell, string title, string automationId) =>
        Shell.ById<Control>(shell.Node(title), automationId).Any(control => control.IsEffectivelyVisible);

    [AvaloniaFact]
    public void The_person_resolves_the_final_merges_conflict_from_the_run_bar_and_the_run_completes()
    {
        using var f = new WorkflowRunFixture(Task(A, "A", 105));
        f.Answer("A", f.Writes("base.txt", "A\n", "A ready.", gate: "a"));
        var shell = f.Window();
        var opened = Opened(shell);
        shell.StartRun();
        shell.WaitForCard("A", "Running");
        // main moves while the run works, so its work conflicts with main in base.txt.
        File.WriteAllText(Path.Combine(f.Project, "base.txt"), "main\n");
        f.Run("commit", "-q", "-am", "main");
        f.Open("a");
        shell.WaitUntil(() => shell.ShowsAny("RunMarkResolved"), "the run's bar offers Mark resolved", () => shell.WorkflowRun?.Activity ?? "");

        shell.Click(shell.Find<Button>("RunOpenConflict"));
        shell.WaitUntil(() => opened.Count == 1, "the worktree opens", () => shell.Status);
        var file = Path.Combine(opened[0], "base.txt");
        Assert.Contains("<<<<<<<", File.ReadAllText(file));
        shell.Click(shell.Find<Button>("RunMarkResolved"));
        shell.WaitUntil(() => shell.Status == "base.txt still has conflict markers. Fix it, then mark the conflict resolved.",
            "the notice names the file with markers", () => shell.Status);

        File.WriteAllText(file, "main and A\n");
        shell.Click(shell.Find<Button>("RunMarkResolved"));
        shell.WaitForStatus("Completed");
        Assert.Equal("main and A\n", File.ReadAllText(Path.Combine(f.Project, "base.txt")));
        Assert.False(shell.ShowsAny("RunMarkResolved"));
    }
}
