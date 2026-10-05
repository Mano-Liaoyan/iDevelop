using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The menu a right-click on a connection opens.</summary>
public sealed class ConnectionMenuTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private Shell Open(ConnectionKind kind) => Shell.Open(_temp.Seed(
        TaskAt(Design, "Design", 105, 90),
        TaskAt(Build, "Build", 605, 90),
        new WorkflowEdit.Connect(new ConnectionKey(Design, Build), kind)));

    private static bool Checked(MenuItem item) => Assert.IsType<PathIcon>(item.Icon).IsVisible;

    [AvaloniaTheory]
    [InlineData(ConnectionKind.Dependency)]
    [InlineData(ConnectionKind.Context)]
    public void The_current_kind_carries_the_checkmark_and_insert_and_delete_follow(ConnectionKind kind)
    {
        var shell = Open(kind);

        shell.RightClick(shell.ConnectionInto("Build"));

        Assert.Equal(["Dependency", "Context", "Insert Node…", "Delete"], shell.MenuHeaders());
        Assert.Equal(
            (kind == ConnectionKind.Dependency, kind == ConnectionKind.Context),
            (Checked(shell.MenuItem("ConnectionMenuDependency")), Checked(shell.MenuItem("ConnectionMenuContext"))));
        Assert.Contains("destructive", shell.MenuItem("ConnectionMenuDelete").Classes);
    }

    [AvaloniaFact]
    public void Choosing_the_current_kind_changes_nothing_and_the_other_kind_changes_it()
    {
        var shell = Open(ConnectionKind.Dependency);
        var before = shell.Window.ViewModel.Canvas!.Workflow;

        shell.RightClick(shell.ConnectionInto("Build"));
        shell.Click(shell.MenuItem("ConnectionMenuDependency"));
        Assert.Same(before, shell.Window.ViewModel.Canvas!.Workflow);

        shell.RightClick(shell.ConnectionInto("Build"));
        shell.Click(shell.MenuItem("ConnectionMenuContext"));
        Assert.Equal([("Design", "Build", ConnectionKind.Context)], shell.Drawn());
    }

    [AvaloniaFact]
    public void Insert_node_splits_the_connection_keeping_its_kind_and_centers_the_node_on_the_click()
    {
        var shell = Open(ConnectionKind.Context);
        var click = shell.ConnectionInto("Build");

        shell.RightClick(click);
        shell.Click(shell.MenuItem("ConnectionMenuInsert"));

        Assert.True(shell.AddPopoverIsOpen);
        Assert.Contains("Insert after", Shell.Texts(shell.AddPopover));
        Assert.Equal("Design", shell.Find<TextBlock>("AddNodeConnect").Text);
        shell.Click(shell.AddRow("Review"));

        var workflow = shell.Window.ViewModel.Canvas!.Workflow;
        var inserted = workflow.Tasks.Values.Single(task => task.Blueprint.Key == BuiltInBlueprints.Review.Key);
        Assert.Equal(
            [(new ConnectionKey(Design, inserted.Id), ConnectionKind.Context), (new ConnectionKey(inserted.Id, Build), ConnectionKind.Context)],
            workflow.Connections.Select(connection => (connection.Key, connection.Value)).OrderBy(connection => connection.Key.From == Design ? 0 : 1));
        var at = shell.CanvasPointAt(click);
        Assert.Equal(
            new CanvasPoint(at.X - WorkflowCanvasViewModel.TaskCardWidth / 2, at.Y - WorkflowCanvasViewModel.TaskCardHeight / 2),
            workflow.Positions[inserted.Id]);
    }
}
