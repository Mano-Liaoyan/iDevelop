using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Run Workflow and the bar at the bottom center (ADR 0017): the bar counts what runs and waits, Stop All stops every
/// running node, and Merge into my branch shows only while every node is completed and nothing is stale.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class RunTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = TestTasks.Review;

    [AvaloniaFact]
    public void Run_Workflow_starts_every_ready_node_the_bar_counts_them_and_Stop_All_stops_them()
    {
        using var f = new NodeFixture(Plan(A, "A", 105), Plan(B, "B", 105 + 300), Plan(C, "C", 405 + 300), Dependency(B, C));
        f.Install(Reply("s-1", "Done.", On().WaitForFile(f.Gate("never"))));
        var shell = f.Open();
        Assert.False(shell.Find<UserControl>("WorkflowRunBar").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("RunWorkflow"));

        shell.WaitForState("A", NodeState.Running);
        shell.WaitForState("B", NodeState.Running);
        Assert.Equal(NodeState.Idle, shell.NodeModel("C").State);
        Assert.True(shell.Find<UserControl>("WorkflowRunBar").IsEffectivelyVisible);
        Assert.Equal("2 running", shell.Text("RunStatus"));
        Assert.Equal("\"A\", \"B\"", shell.Text("RunActivity"));
        Assert.False(shell.Find<Button>("MergeIntoBranch").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("StopAll"));

        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.RunningCount == 0, "every node stops");
        Assert.False(shell.Find<UserControl>("WorkflowRunBar").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Merge_into_my_branch_shows_once_every_node_is_completed_and_merges_the_end_nodes_work()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));
        f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "A ready.", On().Write("a.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "B ready.", On().WaitForFile(f.Gate("b")).Write("b.txt", "B\n")))));
        var shell = f.Open();
        shell.Click(shell.Find<Button>("RunWorkflow"));
        shell.WaitForState("B", NodeState.Running);
        Assert.False(shell.Find<Button>("MergeIntoBranch").IsEffectivelyVisible);

        f.Release("b");
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.CanMergeIntoBranch, "Merge into my branch is offered");
        Assert.Equal("Every node is complete", shell.Text("RunStatus"));
        Assert.True(shell.Find<Button>("MergeIntoBranch").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("MergeIntoBranch"));

        Assert.Equal("Merged the workflow's work into your branch.", shell.Status);
        Assert.Equal(("A\n", "B\n"), (File.ReadAllText(Path.Combine(f.Project, "a.txt")), File.ReadAllText(Path.Combine(f.Project, "b.txt"))));
    }

    [AvaloniaFact]
    public void Run_Workflow_that_cannot_write_the_nodes_logs_says_why_and_runs_nothing()
    {
        using var f = new NodeFixture(Plan(A, "A", 105));
        f.Install(Reply("s-a", "A ready."));
        // A file where the node logs go keeps them from being written.
        File.WriteAllText(Path.Combine(f.Project, ".idp", "nodes"), "");
        var shell = f.Open();

        shell.Click(shell.Find<Button>("RunWorkflow"));

        Assert.StartsWith("Couldn't run the workflow.", shell.Status);
        Assert.Equal(NodeState.Idle, shell.NodeModel("A").State);
    }
}
