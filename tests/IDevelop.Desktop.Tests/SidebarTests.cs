using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

public sealed class SidebarTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    // The canvas is 738 px wide, so a 260 px card at this x shows its left 183 px and hides the rest.
    private const double PartlyOffScreen = 555;

    // Nodify animates a pan by default, for up to a second of real time, and counts it as panning until it ends.
    private static void WaitForPan(Shell shell)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            shell.Render();
        }
        while (shell.Editor.IsPanning && DateTime.UtcNow < deadline);
    }

    [AvaloniaFact]
    public void The_sidebar_lists_the_open_project_with_its_task_count_and_tasks()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 405, 90), TaskAt(Review, "Review", 405, 300)));

        Assert.Equal("seed", shell.Find<TextBlock>("ProjectName").Text);
        Assert.Equal("3", shell.Find<TextBlock>("TaskCount").Text);
        Assert.Equal(["Design", "Build", "Review"], Shell.Texts(shell.Find<ListBox>("SidebarTasks")));

        shell.Click(shell.Find<Button>("AddTask"));

        Assert.Equal("4", shell.Find<TextBlock>("TaskCount").Text);
        Assert.Equal(["Design", "Build", "Review", "New task"], Shell.Texts(shell.Find<ListBox>("SidebarTasks")));
    }

    [AvaloniaFact]
    public void Choosing_a_task_in_the_sidebar_selects_its_card_and_opens_it_in_the_inspector()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 405, 90)));

        shell.Click(shell.SidebarRow("Build"));

        Assert.Equal([false, true], new[] { "Design", "Build" }.Select(title => shell.Node(title).IsSelected));
        Assert.Equal("Build", shell.Find<TextBox>("TaskTitle").Text);

        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("Design", ((TaskNodeViewModel)shell.Find<ListBox>("SidebarTasks").SelectedItem!).Title);
        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);
    }

    [AvaloniaFact]
    public void Choosing_a_task_off_the_screen_in_the_sidebar_brings_its_card_into_view()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 2400, 1600)));
        Assert.False(new Rect(shell.Editor.Bounds.Size).Intersects(shell.CardRect("Build")));

        shell.Click(shell.SidebarRow("Build"));
        WaitForPan(shell);

        Assert.True(new Rect(shell.Editor.Bounds.Size).Contains(shell.CardRect("Build")), $"{shell.CardRect("Build")} is outside {shell.Editor.Bounds.Size}");
        Assert.Equal(new Point(2171, 1232), shell.Editor.ViewportLocation);
        Assert.Equal(new Point(2171, 1232), shell.Find<Minimap>("Minimap").ViewportLocation);
    }

    [AvaloniaFact]
    public void Choosing_a_task_off_the_screen_in_the_sidebar_with_the_keyboard_brings_its_card_into_view()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 2400, 1600)));
        shell.Click(shell.SidebarRow("Design"));
        WaitForPan(shell);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);

        shell.Press(Key.Down);
        WaitForPan(shell);

        Assert.True(shell.Node("Build").IsSelected);
        Assert.Equal(new Point(2171, 1232), shell.Editor.ViewportLocation);
    }

    private Shell SelectBuildOnTheCanvasAndPanItAway()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 405, 90)));
        shell.Click(shell.Header(shell.Node("Build")));
        shell.Pan(shell.Editor.TranslatePoint(new Point(700, 400), shell.Window)!.Value, new Vector(-600, -300));
        Assert.Equal("Build", ((TaskNodeViewModel)shell.Find<ListBox>("SidebarTasks").SelectedItem!).Title);
        Assert.Equal(new Point(600, 300), shell.Editor.ViewportLocation);
        return shell;
    }

    [AvaloniaFact]
    public void Choosing_the_selected_task_in_the_sidebar_brings_its_card_back_into_view()
    {
        var shell = SelectBuildOnTheCanvasAndPanItAway();
        var design = shell.SidebarRow("Design");

        // The rows are 4 px apart, so 2 px below Design is between the rows.
        shell.Click(design.TranslatePoint(new Point(design.Bounds.Width / 2, design.Bounds.Height + 2), shell.Window)!.Value);
        Assert.Equal(new Point(600, 300), shell.Editor.ViewportLocation);

        shell.Click(shell.SidebarRow("Build"));

        Assert.Equal(new Point(176, -278), shell.Editor.ViewportLocation);
    }

    [AvaloniaTheory]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    public void Choosing_the_selected_task_in_the_sidebar_with_the_keyboard_brings_its_card_back_into_view(Key key)
    {
        var shell = SelectBuildOnTheCanvasAndPanItAway();
        shell.Find<Button>("AddTask").Focus();
        shell.Press(Key.Tab);
        shell.Press(Key.Tab);
        Assert.Same(shell.SidebarRow("Build"), shell.Window.FocusManager!.GetFocusedElement());

        shell.Press(key);

        Assert.Equal(new Point(176, -278), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void Pressing_and_releasing_a_card_partly_off_the_screen_leaves_the_canvas_where_it_is()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", PartlyOffScreen, 90)));
        var header = shell.Header(shell.Node("Design"));

        shell.Window.MouseMove(header);
        shell.Window.MouseDown(header, MouseButton.Left);
        WaitForPan(shell);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);

        shell.Window.MouseUp(header, MouseButton.Left);
        WaitForPan(shell);

        Assert.True(shell.Node("Design").IsSelected);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void Dragging_a_card_partly_off_the_screen_drops_it_where_the_pointer_left_it_and_leaves_the_canvas_where_it_is()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", PartlyOffScreen, 90)));
        var header = shell.Header(shell.Node("Design"));

        shell.Drag(header, header - new Vector(195, 0));
        WaitForPan(shell);

        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);
        Assert.Equal(new Point(360, 90), shell.Node("Design").Location);
    }

    [AvaloniaFact]
    public void Selecting_a_card_partly_off_the_screen_with_a_rubber_band_leaves_the_canvas_where_it_is()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", PartlyOffScreen, 90)));
        var canvas = shell.Editor.TranslatePoint(default, shell.Window)!.Value;

        shell.Drag(canvas + new Vector(450, 300), canvas + new Vector(650, 60));
        WaitForPan(shell);

        Assert.True(shell.Node("Design").IsSelected);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void Selecting_every_card_leaves_the_canvas_where_it_is()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", PartlyOffScreen, 90)));

        // NodifyAvalonia 6.6.0 never matches its commands' key gestures, so Ctrl+A does nothing and the test runs the command.
        EditorCommands.SelectAll.Execute(null, shell.Editor);
        WaitForPan(shell);

        Assert.True(shell.Node("Design").IsSelected);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void Tab_moves_through_the_sidebar_from_top_to_bottom()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 405, 90)));
        shell.Find<Button>("AddTask").Focus();
        string Tab()
        {
            shell.Press(Key.Tab);
            return shell.Window.FocusManager!.GetFocusedElement() switch
            {
                ListBoxItem { DataContext: TaskNodeViewModel task } => task.Title,
                Control control => AutomationProperties.GetAutomationId(control) ?? control.GetType().Name,
                var other => $"{other}",
            };
        }

        Assert.Equal(["OpenFolder", "Design", "RefreshAgents", "ThemeSystem", "ThemeLight", "ThemeDark"], [Tab(), Tab(), Tab(), Tab(), Tab(), Tab()]);
    }
}
