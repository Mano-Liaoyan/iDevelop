using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A result goes stale when what it ran on changes: its inputs, its instruction, or its model (ADR 0034, ADR 0042). The
/// card flags it, Merge into my branch waits, and running it again makes it current.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class StaleTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    private static Shell Completed(NodeFixture f)
    {
        f.Install(On().Choose((Fresh("A"), Reply("s-a", "A ready.", On().Write("a.txt", "A\n"))), (Fresh("B"), Reply("s-b", "B ready.", On().Write("b.txt", "B\n")))));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("B", NodeState.Completed);
        shell.WaitUntil(() => shell.Window.ViewModel.Canvas!.CanMergeIntoBranch, "every node is current");
        return shell;
    }

    [AvaloniaFact]
    public void An_edited_instruction_flags_the_result_stale_holds_back_the_merge_and_a_retry_makes_it_current()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));
        var shell = Completed(f);
        var canvas = shell.Window.ViewModel.Canvas!;

        canvas.Edit(new WorkflowEdit.SetField(A, "instructions", "Build A again."));
        shell.Render();

        shell.WaitForState("A", NodeState.Stale);
        Assert.Equal(("Stale", false), (shell.CardText("A", "CardStatus"), canvas.CanMergeIntoBranch));
        Assert.Contains("Run it again", shell.NodeModel("A").StateDetail);

        shell.NodeModel("A").RetryCommand.Execute(null);

        shell.WaitUntil(() => shell.NodeModel("B").Results.Count == 2 && canvas.CanMergeIntoBranch, "A and B are current again");
        Assert.Equal((NodeState.Completed, NodeState.Completed), (shell.NodeModel("A").State, shell.NodeModel("B").State));
    }

    [AvaloniaFact]
    public void A_new_dependency_makes_the_node_after_it_stale_and_Undo_makes_it_current_again()
    {
        using var f = new NodeFixture(Plan(A, "A", 105), Plan(B, "B", 405));
        f.Install(On().Choose((Fresh("A"), Reply("s-a", "A ready.")), (Fresh("B"), Reply("s-b", "B ready."))));
        var shell = f.Open();
        shell.Click(shell.Find<Avalonia.Controls.Button>("RunWorkflow"));
        shell.WaitForState("A", NodeState.Completed);
        shell.WaitForState("B", NodeState.Completed);
        var canvas = shell.Window.ViewModel.Canvas!;

        canvas.Edit(Dependency(A, B));
        shell.Render();

        shell.WaitForState("B", NodeState.Stale);
        canvas.Document.Undo();
        shell.Render();
        shell.WaitForState("B", NodeState.Completed);
    }
}
