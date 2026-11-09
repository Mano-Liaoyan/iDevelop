using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Sidebar;
using IDevelop.Desktop.Theme;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>The Index's four levels: a workflow's stages by depth, their folds, and the rollup marks.</summary>
[Collection(ProcessCollection.Name)]
public sealed class TreeRowsTests
{
    private static string Started => """{"entry":"turnStarted","basis":{"inputs":[],"instruction":"","model":null},"session":null,"round":false}""";

    private static void Log(NodeFixture f, TaskId id, string last)
    {
        var log = Engine.NodeLog.PathOf(f.Project, id);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, [Started, last]);
    }

    private static NodeFixture Chain(params TaskId[] ids) => new(
        [.. ids.Select((id, index) => (WorkflowEdit)NodeFixture.Task(id, $"Node {index}", 105 + index * 300)),
         .. ids.Zip(ids.Skip(1), (from, to) => (WorkflowEdit)NodeFixture.Dependency(from, to))]);

    private static string[] Stages(WorkflowCanvasViewModel canvas) =>
        [.. canvas.Stages.Select(stage => $"{stage.Title} {stage.Progress}: {string.Join(", ", stage.Agents.Select(node => node.Title))}")];

    [AvaloniaFact]
    public void A_workflow_lists_its_agents_in_stages_by_depth()
    {
        var (first, second, third) = (new TaskId(Guid.NewGuid()), new TaskId(Guid.NewGuid()), new TaskId(Guid.NewGuid()));
        using var f = Chain(first, second, third);
        var canvas = f.Open().Window.ViewModel.Canvas!;

        Assert.Equal(["Stage 1 0/1: Node 0", "Stage 2 0/1: Node 1", "Stage 3 0/1: Node 2"], Stages(canvas));
    }

    [AvaloniaFact]
    public void Agents_of_one_depth_share_a_stage_and_stand_by_their_saved_place()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => new TaskId(Guid.NewGuid())).ToArray();
        using var f = new NodeFixture(
            AppTempFolder.TaskAt(ids[0], "Low", 105, 400), AppTempFolder.TaskAt(ids[1], "High", 405, 90), AppTempFolder.TaskAt(ids[2], "Same row", 705, 90));
        var canvas = f.Open().Window.ViewModel.Canvas!;

        Assert.Equal(["Stage 1 0/3: High, Same row, Low"], Stages(canvas));
    }

    [AvaloniaFact]
    public void A_stage_with_every_agent_done_folds_by_default_and_a_fold_the_person_sets_stays()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var canvas = shell.Window.ViewModel.Canvas!;
        var (done, open) = (canvas.Stages[0], canvas.Stages[1]);

        Assert.Equal((true, false), (done.IsFolded, open.IsFolded));
        Assert.Empty(done.Agents);
        done.ToggleFold();
        Assert.Equal(["Design the API"], done.Agents.Select(node => node.Title));
        open.ToggleFold();
        canvas.ShowEngine();
        Assert.Equal((false, true), (done.IsFolded, open.IsFolded));
    }

    [AvaloniaFact]
    public void Selecting_a_node_opens_its_folded_stage_and_expands_its_workflow()
    {
        var (first, second) = (new TaskId(Guid.NewGuid()), new TaskId(Guid.NewGuid()));
        using var f = Chain(first, second);
        var canvas = f.Open().Window.ViewModel.Canvas!;
        canvas.Stages[1].ToggleFold();
        canvas.IsExpanded = false;

        canvas.SelectedNode = canvas.Nodes.Single(node => node.Title == "Node 1");

        Assert.True(canvas.IsExpanded);
        Assert.False(canvas.Stages[1].IsFolded);
        Assert.True(canvas.SelectedNode.IsSelected || canvas.SelectedNodes.Count == 0);
    }

    [AvaloniaFact]
    public void Opening_a_workflow_expands_it_and_leaves_the_other_workflows_as_they_are()
    {
        var (first, second) = (new TaskId(Guid.NewGuid()), new TaskId(Guid.NewGuid()));
        using var f = Chain(first, second);
        var shell = f.Open();
        var view = shell.Window.ViewModel;
        var other = view.NewWorkflow(view.Projects[0])!;
        var previous = view.Projects[0].Workflows[0];
        previous.IsExpanded = false;
        shell.Render();

        view.Select(previous);

        Assert.True(previous.IsExpanded);
        Assert.True(other.IsExpanded);
    }

    [AvaloniaFact]
    public void A_workflow_row_shows_its_rollup_and_gives_way_to_a_chevron_while_it_is_expanded()
    {
        var (first, second) = (new TaskId(Guid.NewGuid()), new TaskId(Guid.NewGuid()));
        using var f = Chain(first, second);
        Log(f, first, """{"entry":"failed","reason":"The model is not available."}""");
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        canvas.IsExpanded = false;
        shell.Render();
        var mark = shell.Find<StateMark>("WorkflowState");

        Assert.Equal((NodeState.Failed, true), (canvas.Rollup, mark.IsEffectivelyVisible));

        canvas.IsExpanded = true;
        shell.Render();

        Assert.False(mark.IsEffectivelyVisible);
    }

    [Theory]
    [InlineData(1, 1, 1, 1, 1, 8, NodeState.InputRequired)]
    [InlineData(1, 1, 0, 1, 1, 8, NodeState.Failed)]
    [InlineData(1, 1, 0, 0, 1, 8, NodeState.Running)]
    [InlineData(1, 0, 0, 0, 1, 8, NodeState.Stale)]
    [InlineData(8, 0, 0, 0, 0, 8, NodeState.Completed)]
    [InlineData(1, 0, 0, 0, 0, 8, null)]
    [InlineData(0, 0, 0, 0, 0, 0, null)]
    public void The_rollup_is_needs_you_then_failed_then_running_then_stale_then_done(int completed, int running, int waiting, int failed, int stale, int total, NodeState? mark) =>
        Assert.Equal(mark, new StateCounts(completed, running, waiting, failed, stale, total).Rollup);
}
