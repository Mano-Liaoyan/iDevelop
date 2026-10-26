using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>Retry starts a fresh session once a turn has ended, and the nodes after it run again on its new result (ADR 0042).</summary>
[Collection(ProcessCollection.Name)]
public sealed class RunRetryTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    [AvaloniaFact]
    public void Retry_of_a_failed_node_runs_it_again_in_a_fresh_session_and_the_next_node_follows()
    {
        using var f = new NodeFixture(Plan(A, "A", 105), Plan(B, "B", 405), Dependency(A, B));
        var a = Path.Combine(f.Evidence, "a");
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "1.json"), new System.Text.Json.Nodes.JsonArray([.. On().Stderr("The model is busy.").Exit(2).Steps.Select(step => step.DeepClone())]).ToJsonString());
        File.WriteAllText(Path.Combine(a, "2.json"), new System.Text.Json.Nodes.JsonArray([.. Reply("s-a2", "A ready.").Steps.Select(step => step.DeepClone())]).ToJsonString());
        f.Install(On().Choose((Fresh("A"), On().Scripted(a)), (Fresh("B"), Reply("s-b", "B ready."))));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.Failed);
        shell.Click(shell.Header(shell.Node("A")));
        Assert.Contains("The model is busy.", shell.InView<TextBlock>("RunTaskDetail").Text);
        Assert.False(shell.InView<Button>("RunTask").IsEffectivelyVisible);

        shell.Click(shell.InView<Button>("RetryTask"));

        shell.WaitForState("B", NodeState.Completed);
        Assert.Equal(NodeState.Completed, shell.NodeModel("A").State);
        Assert.Equal("A ready.", Assert.Single(shell.NodeModel("A").Results).Summary);
    }

    [AvaloniaFact]
    public void Retry_of_a_completed_node_gives_it_a_new_result_and_the_node_after_it_runs_again_on_it()
    {
        using var f = new NodeFixture(Plan(A, "A", 105), Plan(B, "B", 405), Dependency(A, B));
        f.Install(On().Choose((Fresh("A"), Reply("s-a", "A ready.")), (Fresh("B"), Reply("s-b", "B ready."))));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("B", NodeState.Completed);

        shell.NodeModel("A").RetryCommand.Execute(null);

        shell.WaitUntil(() => shell.NodeModel("B").Results.Count == 2 && shell.NodeModel("B").State == NodeState.Completed, "B runs again on A's new result");
        Assert.Equal(2, shell.NodeModel("A").Results.Count);
        Assert.Equal("Result 2 · Current", shell.NodeModel("B").Results[0].Heading);
    }
}
