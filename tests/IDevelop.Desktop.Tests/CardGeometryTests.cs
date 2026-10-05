using Avalonia;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The card's geometry constants, which placement uses, against where the card template really puts its ports.</summary>
public sealed class CardGeometryTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [AvaloniaFact]
    public void Each_port_sits_at_its_constant_in_the_cards_container()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(TestTasks.Design, "Design", 105, 90),
            TaskAt(TestTasks.Build, "Build", 465, 180),
            TaskAt(TestTasks.Review, "Review", 825, 90),
            new WorkflowEdit.Connect(new ConnectionKey(TestTasks.Design, TestTasks.Build), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(TestTasks.Build, TestTasks.Review), ConnectionKind.Context)));
        var nodes = shell.Window.ViewModel.Canvas!.Nodes.ToDictionary(node => node.Title);

        Vector[] offsets =
        [
            nodes["Design"].Output.Anchor - nodes["Design"].Location,
            nodes["Build"].Input.Anchor - nodes["Build"].Location,
            nodes["Build"].Output.Anchor - nodes["Build"].Location,
            nodes["Review"].Input.Anchor - nodes["Review"].Location,
        ];
        Point[] expected =
        [
            WorkflowCanvasViewModel.OutputPortCenter, WorkflowCanvasViewModel.InputPortCenter,
            WorkflowCanvasViewModel.OutputPortCenter, WorkflowCanvasViewModel.InputPortCenter,
        ];

        Assert.All(offsets.Zip(expected), pair =>
            Assert.True(Math.Abs(pair.First.X - pair.Second.X) <= 0.5 && Math.Abs(pair.First.Y - pair.Second.Y) <= 0.5, $"{pair.First} is not {pair.Second}"));
    }
}
