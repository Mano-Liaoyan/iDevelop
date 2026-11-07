using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Conversation;
using IDevelop.Engine;
using IDevelop.TestSupport;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

[Collection(ProcessCollection.Name)]
public sealed class PanelToggleTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static ColumnDefinitions Columns(Shell shell) => shell.Window.FindControl<Grid>("Columns")!.ColumnDefinitions;

    [AvaloniaFact]
    public void A_tab_hides_the_left_panel_and_brings_it_back_at_its_width()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var width = Columns(shell)[0].ActualWidth;

        shell.Click(shell.Find<Button>("ToggleSidebar"));
        Assert.Equal(0, Columns(shell)[0].ActualWidth);

        shell.Click(shell.Find<Button>("ToggleSidebar"));
        Assert.Equal(width, Columns(shell)[0].ActualWidth);
    }

    [AvaloniaFact]
    public void A_tab_hides_the_inspector_and_brings_it_back_at_its_width()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var width = Columns(shell)[4].ActualWidth;

        shell.Click(shell.Find<Button>("ToggleInspector"));
        Assert.Equal(0, Columns(shell)[4].ActualWidth);

        shell.Click(shell.Find<Button>("ToggleInspector"));
        Assert.Equal(width, Columns(shell)[4].ActualWidth);
    }

    [AvaloniaFact]
    public void A_narrow_window_closes_the_inspector_until_the_person_opens_it()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var view = shell.Window.ViewModel;

        view.IsNarrow = true;
        Assert.False(view.IsInspectorOpen);

        view.ShowInspector();
        Assert.True(view.IsInspectorOpen);

        view.IsNarrow = false;
        view.IsNarrow = true;
        Assert.True(view.IsInspectorOpen);
    }

    [AvaloniaFact]
    public void A_narrow_window_lays_a_selected_nodes_inspector_over_the_canvas_until_its_tab_closes_it()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90), TaskAt(TestTasks.Build, "Build", 405, 90)));
        var view = shell.Window.ViewModel;
        view.IsNarrow = true;
        Assert.False(view.IsInspectorShown);

        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal((false, true), (view.IsInspectorOpen, view.IsInspectorOverlay));
        Assert.Equal(0, Columns(shell)[4].ActualWidth);
        Assert.True(shell.Find<TextBox>("TaskTitle").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("ToggleInspector"));
        Assert.False(view.IsInspectorShown);

        shell.Click(shell.Header(shell.Node("Build")));
        Assert.True(view.IsInspectorOverlay);
    }

    [Fact]
    public void A_step_splits_its_line_into_the_tool_and_what_it_ran()
    {
        var step = new ActivityItemViewModel(0, new ConversationLine.Activity("Bash dotnet test --filter Unit\nmore", true));

        Assert.Equal(("Bash", "dotnet test --filter Unit"), (step.Tool, step.Command));
    }
}
