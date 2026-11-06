using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Blueprints;
using IDevelop.Desktop.Canvas;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.AgyTurn;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>The one run bar, the live pins, the keyboard's moves along the graph, and a blueprint's field folds.</summary>
[Collection(ProcessCollection.Name)]
public sealed class RunBarTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;
    private static readonly TaskId C = TestTasks.Review;

    [AvaloniaFact]
    public void A_failed_node_shows_the_bar_with_Next_and_a_count_and_its_tooltip_holds_the_whole_summary()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(On().Stderr("The model is not available.").Exit(2));
        var shell = f.Open();

        shell.RunNode("A");
        shell.WaitForCard("A", "Failed");

        Assert.True(shell.Find<UserControl>("WorkflowRunBar").IsEffectivelyVisible);
        Assert.Equal("0 of 1 complete", shell.Text("RunStatus"));
        Assert.Equal("1 failed", shell.Window.ViewModel.Canvas!.BarSummary);
        Assert.Equal("1 failed", Assert.Single(shell.Window.ViewModel.Canvas!.BarChips).Label);
        Assert.Equal("0/1 done", shell.Window.ViewModel.Canvas!.BarShort);
        Assert.True(shell.Find<Button>("NextWaiting").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Next_selects_a_failed_node_as_it_selects_one_that_waits()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Task(B, "B", 405));
        f.Install(On().Stderr("The model is not available.").Exit(2));
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        shell.RunNode("B");
        shell.WaitForCard("B", "Failed");
        Assert.NotEqual("B", canvas.SelectedNode?.Title);

        shell.Click(shell.Find<Button>("NextWaiting"));

        Assert.Equal("B", canvas.SelectedNode?.Title);
    }

    [AvaloniaFact]
    public void A_completed_node_lights_its_output_pin_and_the_next_nodes_input_pin_and_its_wire_is_delivered()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Task(B, "B", 405), Dependency(A, B));
        f.Install(Reply("s-1", "Done."));
        var shell = f.Open();
        var (first, second) = (shell.NodeModel("A"), shell.NodeModel("B"));
        var wire = shell.Window.ViewModel.Canvas!.Connections.Single();
        Assert.False(second.Input.IsLive);
        Assert.False(first.Output.IsLive);
        Assert.Equal(WireFlow.Idle, wire.Flow);

        shell.RunNode("A");
        shell.WaitForState("B", NodeState.Completed);

        Assert.True(first.Output.IsLive);
        Assert.True(second.Input.IsLive);
        Assert.False(first.Input.IsLive);
        Assert.Equal("Input: A", second.Input.Tip);
        Assert.Equal("Output: Result 1", first.OutputLine);
        Assert.Equal("Input: 1", second.InputLine);
        Assert.Equal(WireFlow.Delivered, wire.Flow);
    }

    [AvaloniaFact]
    public void An_empty_workflow_says_why_it_cannot_run_and_shows_no_zoom_panel()
    {
        using var temp = new TempFolder();
        var shell = Shell.Open(temp.Seed());

        Assert.False(shell.Find<Button>("RunWorkflow").IsEffectivelyEnabled);
        Assert.Contains("Add a node first", shell.Window.ViewModel.Canvas!.RunWorkflowTip);
        Assert.False(shell.ShowsAny("FitToScreen"));
    }

    [AvaloniaFact]
    public void The_keys_move_the_selection_along_the_graph_and_Tab_follows_the_outline()
    {
        using var temp = new TempFolder();
        var shell = Shell.Open(temp.Seed(
            TaskAt(A, "A", 105, 90), TaskAt(B, "B", 405, 90), TaskAt(C, "C", 405, 290),
            new WorkflowEdit.Connect(new ConnectionKey(A, B), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(A, C), ConnectionKind.Dependency)));
        var canvas = shell.Window.ViewModel.Canvas!;
        string? Selected() => canvas.SelectedNode?.Title;
        shell.Click(shell.Header(shell.Node("A")));

        Assert.True(canvas.SelectConnected(CardStep.Successor));
        Assert.Equal("B", Selected());
        Assert.True(canvas.SelectConnected(CardStep.NextSibling));
        Assert.Equal("C", Selected());
        Assert.False(canvas.SelectConnected(CardStep.NextSibling));
        Assert.True(canvas.SelectConnected(CardStep.PreviousSibling));
        Assert.Equal("B", Selected());
        Assert.True(canvas.SelectConnected(CardStep.Predecessor));
        Assert.Equal("A", Selected());

        Assert.True(canvas.SelectInOutline(1));
        Assert.True(canvas.SelectInOutline(1));
        Assert.Equal("C", Selected());
        Assert.False(canvas.SelectInOutline(1));
    }

    [AvaloniaFact]
    public void A_blueprint_field_is_one_fold_row_that_hides_its_key_and_a_new_field_starts_open()
    {
        using var temp = new TempFolder();
        var shell = Shell.Open(temp.Seed(TaskAt(A, "Design", 105, 90)));
        var blueprints = shell.Window.ViewModel.Canvas!.Blueprints;
        blueprints.Derive(BuiltInBlueprints.Implement);
        shell.Render();
        var fields = blueprints.Editor!.Fields;

        Assert.All(fields, field => Assert.False(field.IsOpen));
        Assert.Equal("Required · Several lines", fields[0].Summary);
        Assert.All(Shell.ById<TextBox>(shell.Window, "FieldKey"), key => Assert.False(key.IsEffectivelyVisible));
        Assert.True(shell.ShowsAny("SaveBlueprint"));

        blueprints.Editor.AddFieldCommand.Execute(null);
        shell.Render();

        Assert.True(fields[^1].IsOpen);
        fields[^1].Label = "";
        fields[^1].Key = "bug";
        Assert.Equal("bug", fields[^1].Title);
    }
}
