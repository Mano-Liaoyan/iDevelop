using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>The left tree's node rows of a large workflow (ADR 0063).</summary>
public sealed class TreeRowsTests
{
    [AvaloniaFact]
    public void A_large_workflow_lists_the_nodes_that_need_the_person_first_and_folds_the_rest_after_12_rows()
    {
        var ids = Enumerable.Range(0, 14).Select(_ => new TaskId(Guid.NewGuid())).ToArray();
        using var f = new NodeFixture([.. ids.Select((id, index) => NodeFixture.Task(id, $"Node {index}", 105 + index * 300))]);
        var started = """{"entry":"turnStarted","basis":{"inputs":[],"instruction":"","model":null},"session":null,"round":false}""";
        var log = Engine.NodeLog.PathOf(f.Project, ids[^1]);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, [started, """{"entry":"failed","reason":"The model is not available."}"""]);
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;

        Assert.Equal(14, canvas.Outline.Count);
        Assert.Equal(WorkflowCanvasViewModel.TreeRowLimit, canvas.TreeRows.Count);
        Assert.Equal("Node 13", canvas.TreeRows[0].Title);
        Assert.Equal(["Node 0", "Node 1"], canvas.TreeRows.Skip(1).Take(2).Select(node => node.Title));
        Assert.True(canvas.HasFoldedRows);
        Assert.Equal("Show all 14 nodes", canvas.ShowAllLabel);

        // A selected node always has its row, so the list keeps the selection.
        var hidden = canvas.Outline.Single(node => node.Title == "Node 12");
        canvas.SelectedNode = hidden;
        Assert.Contains(hidden, canvas.TreeRows);
        Assert.Same(hidden, canvas.SelectedNode);

        canvas.ToggleAllRowsCommand.Execute(null);
        Assert.Equal(14, canvas.TreeRows.Count);
        Assert.Equal("Show fewer nodes", canvas.ShowAllLabel);
    }
}
