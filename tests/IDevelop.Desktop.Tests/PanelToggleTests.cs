using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Conversation;
using IDevelop.Engine;
using IDevelop.TestSupport;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>Which sheets show at each width tier, and how the Index, the Rail, and the Agent panel trade places.</summary>
[Collection(ProcessCollection.Name)]
public sealed class PanelToggleTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static ColumnDefinitions Columns(Shell shell) => shell.Window.FindControl<Grid>("Columns")!.ColumnDefinitions;

    private static void Resize(Shell shell, double width, double height)
    {
        shell.Window.Width = width;
        shell.Window.Height = height;
        shell.Render();
    }

    private Shell Two() => Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90), TaskAt(TestTasks.Build, "Build", 405, 90)));

    [AvaloniaFact]
    public void The_Index_toggle_makes_the_Index_the_Rail_and_brings_it_back_at_its_width()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var width = Columns(shell)[0].ActualWidth;

        shell.Click(shell.Find<Button>("ToggleSidebar"));
        shell.Render();
        Assert.Equal(PanelWidths.RailWidth, Columns(shell)[0].ActualWidth);
        Assert.True(shell.Find<Button>("RailProjects").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("ToggleSidebar"));
        shell.Render();
        Assert.Equal(width, Columns(shell)[0].ActualWidth);
    }

    [AvaloniaFact]
    public void Ctrl_Alt_B_hides_the_Agent_panel_and_brings_it_back_at_its_width()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        var width = Columns(shell)[4].ActualWidth;

        shell.Window.ViewModel.ToggleInspectorCommand.Execute(null);
        shell.Render();
        Assert.Equal(0, Columns(shell)[4].ActualWidth);

        shell.Window.ViewModel.ToggleInspectorCommand.Execute(null);
        shell.Render();
        Assert.Equal(width, Columns(shell)[4].ActualWidth);
    }

    [AvaloniaTheory]
    [InlineData(1280, 800, 252, 400)]
    [InlineData(1800, 1000, 280, 520)]
    [InlineData(2560, 1440, 300, 1008)]
    public void Each_tier_above_Compact_shows_three_sheets_at_their_tier_widths(double width, double height, double index, double panel)
    {
        var shell = Two();
        Resize(shell, width, height);

        Assert.True(shell.Window.ViewModel.IsSidebarShown);
        Assert.True(shell.Window.ViewModel.IsInspectorShown);
        Assert.Equal((index, panel), (Columns(shell)[0].ActualWidth, Columns(shell)[4].ActualWidth));
    }

    [AvaloniaFact]
    public void A_dragged_width_stays_until_the_window_enters_another_tier()
    {
        var shell = Two();
        Resize(shell, 1280, 800);
        shell.SizeInspector(480);
        Assert.Equal(480, Columns(shell)[4].ActualWidth);

        Resize(shell, 1300, 800);
        Assert.Equal(480, Columns(shell)[4].ActualWidth);

        Resize(shell, 1800, 1000);
        Assert.Equal(520, Columns(shell)[4].ActualWidth);
        Resize(shell, 1300, 800);
        Assert.Equal(400, Columns(shell)[4].ActualWidth);
    }

    [AvaloniaFact]
    public void In_the_Compact_tier_a_selected_node_opens_the_Agent_panel_and_the_Index_becomes_the_Rail()
    {
        var shell = Two();
        var view = shell.Window.ViewModel;
        Resize(shell, 900, 600);

        Assert.Equal((true, false), (view.IsSidebarShown, view.IsInspectorShown));
        Assert.Equal(new double[] { 252, 0 }, new[] { Columns(shell)[0].ActualWidth, Columns(shell)[4].ActualWidth });

        shell.Click(shell.Header(shell.Node("Design")));
        shell.Render();
        Assert.Equal((false, true), (view.IsSidebarShown, view.IsInspectorShown));
        Assert.Equal(new double[] { 44, 372 }, new[] { Columns(shell)[0].ActualWidth, Columns(shell)[4].ActualWidth });
        Assert.True(shell.Find<TextBox>("TaskTitle").IsEffectivelyVisible);
        // The Map fills the rest: 900 - 44 - 4 - 372 - 4 - 4.
        Assert.Equal(472, Columns(shell)[2].ActualWidth, 0.5);
    }

    [AvaloniaFact]
    public void The_Index_toggle_closes_the_Agent_panel_in_the_Compact_tier_until_another_node_is_selected()
    {
        var shell = Two();
        var view = shell.Window.ViewModel;
        Resize(shell, 900, 600);
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.Find<Button>("ToggleSidebar"));
        Assert.Equal((true, false), (view.IsSidebarShown, view.IsInspectorShown));

        shell.Click(shell.Header(shell.Node("Build")));
        Assert.Equal((false, true), (view.IsSidebarShown, view.IsInspectorShown));
    }

    [AvaloniaFact]
    public void The_Rail_opens_the_Index_as_a_flyout_that_does_not_rearrange_the_Map_and_closes_on_Esc_and_a_click_outside()
    {
        var shell = Two();
        var view = shell.Window.ViewModel;
        Resize(shell, 900, 600);
        shell.Click(shell.Header(shell.Node("Design")));
        var map = Columns(shell)[2].ActualWidth;

        shell.Click(shell.Find<Button>("RailProjects"));
        shell.Render();
        Assert.True(view.IsIndexFlyoutOpen);
        Assert.Equal(map, Columns(shell)[2].ActualWidth);
        var sheet = shell.Window.IndexSheet;
        Assert.Equal((252d, 48d), (sheet.Bounds.Width, sheet.Bounds.Left));

        shell.Press(Avalonia.Input.Key.Escape);
        Assert.False(view.IsIndexFlyoutOpen);

        shell.Click(shell.Find<Button>("RailProjects"));
        Assert.True(view.IsIndexFlyoutOpen);
        shell.Click(shell.Header(shell.Node("Build")));
        Assert.False(view.IsIndexFlyoutOpen);
    }

    [AvaloniaFact]
    public void The_Rail_shows_the_Inbox_count_of_what_needs_the_person_and_what_failed()
    {
        var shell = Two();
        Resize(shell, 900, 600);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.False(shell.Find<TextBlock>("RailInboxCount").IsEffectivelyVisible);
    }

    [Fact]
    public void A_step_splits_its_line_into_the_tool_and_what_it_ran()
    {
        var step = new ActivityItemViewModel(0, new ConversationLine.Activity("Bash dotnet test --filter Unit\nmore", true));

        Assert.Equal(("Bash", "dotnet test --filter Unit"), (step.Tool, step.Command));
    }
}
