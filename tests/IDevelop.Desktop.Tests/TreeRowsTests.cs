using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>The left tree's node rows, grouped by state (ADR 0064).</summary>
public sealed class TreeRowsTests
{
    private static string Started => """{"entry":"turnStarted","basis":{"inputs":[],"instruction":"","model":null},"session":null,"round":false}""";

    private static void Log(NodeFixture f, TaskId id, string last)
    {
        var log = Engine.NodeLog.PathOf(f.Project, id);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, [Started, last]);
    }

    private static string Row(object row) => row switch
    {
        NodeGroup group => $"[{group.Name} {group.Count}]",
        TaskNodeViewModel node => node.Title,
        _ => "?",
    };

    [AvaloniaFact]
    public void A_workflow_lists_every_node_under_a_header_of_its_group()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => new TaskId(Guid.NewGuid())).ToArray();
        using var f = new NodeFixture([.. ids.Select((id, index) => NodeFixture.Task(id, $"Node {index}", 105 + index * 300))]);
        Log(f, ids[^1], """{"entry":"failed","reason":"The model is not available."}""");
        var canvas = f.Open().Window.ViewModel.Canvas!;

        Assert.Equal(["[Needs you 1]", "Node 3", "[Not started 3]", "Node 0", "Node 1", "Node 2"], canvas.TreeRows.Select(Row));
    }

    [Fact]
    public void A_group_starts_folded_above_8_nodes_except_Needs_you()
    {
        foreach (var kind in new[] { NodeGroupKind.Running, NodeGroupKind.NotStarted, NodeGroupKind.Done })
        {
            Assert.False(new NodeGroup(kind, () => { }) { Count = 8 }.IsFolded, $"{kind} 8");
            Assert.True(new NodeGroup(kind, () => { }) { Count = 9 }.IsFolded, $"{kind} 9");
        }

        Assert.False(new NodeGroup(NodeGroupKind.NeedsYou, () => { }) { Count = 40 }.IsFolded);

        var done = new NodeGroup(NodeGroupKind.Done, () => { }) { Count = 9 };
        done.Toggle();
        done.Count = 12;
        Assert.False(done.IsFolded);
    }

    [AvaloniaFact]
    public void A_header_folds_and_opens_its_group()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => new TaskId(Guid.NewGuid())).ToArray();
        using var f = new NodeFixture([.. ids.Select((id, index) => NodeFixture.Task(id, $"Node {index}", 105 + index * 300))]);
        var canvas = f.Open().Window.ViewModel.Canvas!;
        var group = canvas.Groups.Single(group => group.Kind == NodeGroupKind.NotStarted);

        group.Toggle();
        Assert.Equal(["[Not started 3]"], canvas.TreeRows.Select(Row));
        group.Toggle();
        Assert.Equal(["[Not started 3]", "Node 0", "Node 1", "Node 2"], canvas.TreeRows.Select(Row));
    }

    [AvaloniaFact]
    public void Selecting_a_node_of_a_folded_group_opens_the_group()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => new TaskId(Guid.NewGuid())).ToArray();
        using var f = new NodeFixture([.. ids.Select((id, index) => NodeFixture.Task(id, $"Node {index}", 105 + index * 300))]);
        var canvas = f.Open().Window.ViewModel.Canvas!;
        var group = canvas.Groups.Single(group => group.Kind == NodeGroupKind.NotStarted);
        group.Toggle();

        var node = canvas.Outline[1];
        canvas.SelectedNode = node;
        Assert.False(group.IsFolded);
        Assert.Contains(node, canvas.TreeRows);
        Assert.Same(node, canvas.SelectedNode);
    }
}
