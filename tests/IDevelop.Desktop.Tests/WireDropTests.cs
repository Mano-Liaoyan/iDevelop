using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>A wire dropped on empty canvas offers the Add popover, which places the node at the wire's end.</summary>
public sealed class WireDropTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static TaskNodeViewModel Added(Shell shell) => shell.Window.ViewModel.Canvas!.Nodes.Single(node => node.Id != Design);

    [AvaloniaFact]
    public void A_wire_from_an_output_dropped_on_empty_canvas_adds_a_plan_whose_input_sits_on_the_drop()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        var drop = shell.InEditor(700, 420);

        shell.Drag(shell.Thumb(shell.Output("Design")), drop);

        Assert.True(shell.AddPopoverIsOpen);
        Assert.Equal("Design", shell.Find<TextBlock>("AddNodeConnect").Text);
        Assert.Contains("Connect from", Shell.Texts(shell.AddPopover));
        Assert.Equal(NodeKind.Implement, shell.AddPopover.GetVisualDescendants().OfType<KindTile>().First().Kind);
        Assert.DoesNotContain("Select All", shell.AddRows());
        shell.Click(shell.AddRow("Plan"));

        var added = Added(shell);
        var workflow = shell.Window.ViewModel.Canvas!.Workflow;
        Assert.Equal(BuiltInBlueprints.Plan.Key, workflow.Tasks[added.Id].Blueprint.Key);
        Assert.Equal(ConnectionKind.Dependency, workflow.Connections[new ConnectionKey(Design, added.Id)]);
        Assert.Single(workflow.Connections);
        var released = shell.CanvasPointAt(drop + new Vector(1, 1));
        Assert.True(Point.Distance(released, added.Input.Anchor) <= 2, $"The input sits at {added.Input.Anchor}, not at {released}.");
        Assert.Same(added, shell.Window.ViewModel.Canvas!.SelectedNode);
    }

    [AvaloniaFact]
    public void A_wire_from_an_input_dropped_on_empty_canvas_adds_a_node_whose_output_sits_on_the_drop()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 505, 290)));
        var drop = shell.InEditor(150, 120);

        shell.Drag(shell.Thumb(shell.Input("Design")), drop);

        Assert.Contains("Connect to", Shell.Texts(shell.AddPopover));
        shell.Click(shell.AddRow("Implement"));

        var added = Added(shell);
        var workflow = shell.Window.ViewModel.Canvas!.Workflow;
        Assert.Equal(ConnectionKind.Dependency, workflow.Connections[new ConnectionKey(added.Id, Design)]);
        var released = shell.CanvasPointAt(drop + new Vector(1, 1));
        Assert.True(Point.Distance(released, added.Output.Anchor) <= 2, $"The output sits at {added.Output.Anchor}, not at {released}.");
    }

    [AvaloniaFact]
    public void A_wire_dropped_on_a_card_or_closed_with_escape_adds_nothing()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(TestTasks.Build, "Build", 505, 90)));

        shell.Drag(shell.Thumb(shell.Output("Design")), shell.Header(shell.Node("Build")));
        Assert.False(shell.AddPopoverIsOpen);

        shell.Drag(shell.Thumb(shell.Output("Design")), shell.InEditor(500, 500));
        shell.Press(Avalonia.Input.Key.Escape);

        Assert.False(shell.AddPopoverIsOpen);
        Assert.Equal(2, shell.Nodes().Count());
        Assert.Empty(shell.Window.ViewModel.Canvas!.Workflow.Connections);
        Assert.Equal("seed - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void While_the_popover_is_open_the_dropped_wire_stays_from_its_port_to_the_drop_in_the_ports_hue()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        var drop = shell.InEditor(700, 420);

        shell.Drag(shell.Thumb(shell.Output("Design")), drop);

        var wire = Assert.Single(Dangling(shell));
        var (source, target) = shell.Ends(wire);
        Assert.True(Point.Distance(shell.Thumb(shell.Output("Design")), source) <= 1, $"The wire leaves {source}, not the port.");
        Assert.True(Point.Distance(drop, target) <= 2, $"The wire ends at {target}, not at the drop.");
        Assert.Contains("kind-implement", wire.Classes);

        shell.Press(Avalonia.Input.Key.Escape);
        Assert.Empty(Dangling(shell));
    }

    [AvaloniaFact]
    public void The_dropped_wire_gives_way_to_the_connection_of_the_node_the_popover_adds()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 505, 290)));

        shell.Drag(shell.Thumb(shell.Input("Design")), shell.InEditor(150, 120));
        Assert.Contains("kind-implement", Assert.Single(Dangling(shell)).Classes);
        shell.Click(shell.AddRow("Plan"));

        Assert.Empty(Dangling(shell));
        Assert.Single(shell.Drawn());
    }

    private static IEnumerable<LineConnection> Dangling(Shell shell) =>
        shell.Window.GetVisualDescendants().OfType<LineConnection>().Where(line => Avalonia.Automation.AutomationProperties.GetAutomationId(line) == "DanglingWire");
}
