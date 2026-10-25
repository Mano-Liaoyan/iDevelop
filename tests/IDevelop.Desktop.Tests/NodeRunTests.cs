using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A node's own Run (ADR 0017, ADR 0042): it is off while the engine's CanRun refuses and says why, it runs the node and
/// then each node after it once all of that node's predecessors are completed, and Stop ends the running turn.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class NodeRunTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    [AvaloniaFact]
    public void A_nodes_Run_is_off_until_its_predecessor_completes_and_running_the_first_runs_the_next_by_itself()
    {
        using var f = new NodeFixture(Plan(A, "A", 105), Plan(B, "B", 405), Dependency(A, B));
        f.Install(On().Choose((Fresh("A"), Reply("s-a", "A ready.")), (Fresh("B"), Reply("s-b", "B ready."))));
        var shell = f.Open();
        var b = shell.NodeModel("B");

        Assert.Equal(("It waits for A.", false), (b.RunRefusal, b.RunCommand.CanExecute(null)));
        shell.Click(shell.Header(shell.Node("B")));
        Assert.Equal("It waits for A.", shell.InView<TextBlock>("StartProblem").Text);

        shell.RunNode("A");

        shell.WaitForState("B", NodeState.Completed);
        Assert.Equal(NodeState.Completed, shell.NodeModel("A").State);
        Assert.Equal("B ready.", Assert.Single(b.Results).Summary);
        Assert.Null(b.RunRefusal);
        Assert.Equal(("Completed", "Completed"), (shell.CardText("A", "CardStatus"), shell.CardText("B", "CardStatus")));
    }

    [AvaloniaFact]
    public void Stop_ends_a_running_nodes_turn_and_its_Run_is_offered_again()
    {
        using var f = new NodeFixture(Plan(A, "A", 105));
        f.Install(Reply("s-a", "A ready.", On().WaitForFile(f.Gate("never"))));
        var shell = f.Open();
        shell.Click(shell.Header(shell.Node("A")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitForState("A", NodeState.Running);
        Assert.True(shell.InView<Button>("StopTask").IsEffectivelyVisible);

        shell.Click(shell.InView<Button>("StopTask"));

        shell.WaitForState("A", NodeState.Idle);
        Assert.True(shell.InView<Button>("RunTask").IsEffectivelyEnabled);
        Assert.Empty(shell.NodeModel("A").Results);
    }
}
