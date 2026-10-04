using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Workflows;
using Nodify;

namespace IDevelop.Desktop.Tests;

public sealed class MainWindowTests : IDisposable
{
    private static readonly TaskId Design = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    private static readonly TaskId Build = new(Guid.Parse("019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22"));
    private static readonly TaskId Review = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static WorkflowEdit.CreateTask Task(TaskId id, string title, double x, double y) =>
        new(new TaskDefinition(id) { Title = title }, new CanvasPoint(x, y));

    private static ListBoxItem SidebarRow(Shell shell, string title) =>
        shell.Find<ListBox>("SidebarTasks").GetVisualDescendants().OfType<ListBoxItem>()
            .Single(row => ((TaskNodeViewModel)row.DataContext!).Title == title);

    private static string[] VisibleTexts(Visual region) =>
        [.. region.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)).Select(text => text.Text!)];

    // The canvas is 738 px wide, so a 260 px card at this x shows its left 183 px and hides the rest.
    private const double PartlyOffScreen = 555;

    // Unlimited, the status these titles produce would run about 78 lines at the window's minimum width.
    private static readonly string LongTitle = string.Join(" ", Enumerable.Repeat("with every step of the release written out in full", 30));

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
    public void The_main_window_opens_titled_iDevelop()
    {
        var window = new MainWindow();

        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal("iDevelop", window.Title);
    }

    [AvaloniaFact]
    public void Saving_beside_another_workflow_file_shows_why_and_keeps_the_unsaved_changes()
    {
        var folder = _temp.Create("plan");
        var shell = Shell.Open(folder);
        shell.Click(shell.Find<Button>("AddTask"));
        var other = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, ".idp", "workflows")).FullName, "other.json");
        File.WriteAllText(other, "{}");

        shell.Press(Key.S, RawInputModifiers.Control);

        Assert.Equal($"Not saved. {other} is another workflow file, and this version of iDevelop keeps one workflow per project.", shell.Status);
        Assert.Equal("plan* - iDevelop", shell.Window.Title);
        Assert.True(shell.ShowsUnsavedChanges);
        Assert.Equal(["other.json"], Directory.EnumerateFiles(Path.GetDirectoryName(other)!).Select(Path.GetFileName));
    }

    [AvaloniaFact]
    public void The_sidebar_lists_the_open_project_with_its_task_count_and_tasks()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 405, 90), Task(Review, "Review", 405, 300)));

        Assert.Equal("seed", shell.Find<TextBlock>("ProjectName").Text);
        Assert.Equal("3", shell.Find<TextBlock>("TaskCount").Text);
        Assert.Equal(["Design", "Build", "Review"], VisibleTexts(shell.Find<ListBox>("SidebarTasks")));

        shell.Click(shell.Find<Button>("AddTask"));

        Assert.Equal("4", shell.Find<TextBlock>("TaskCount").Text);
        Assert.Equal(["Design", "Build", "Review", "New task"], VisibleTexts(shell.Find<ListBox>("SidebarTasks")));
    }

    [AvaloniaFact]
    public void Choosing_a_task_in_the_sidebar_selects_its_card_and_opens_it_in_the_inspector()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 405, 90)));

        shell.Click(SidebarRow(shell, "Build"));

        Assert.Equal([false, true], new[] { "Design", "Build" }.Select(title => shell.Node(title).IsSelected));
        Assert.Equal("Build", shell.Find<TextBox>("TaskTitle").Text);

        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal("Design", ((TaskNodeViewModel)shell.Find<ListBox>("SidebarTasks").SelectedItem!).Title);
        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);
    }

    [AvaloniaFact]
    public void Choosing_a_task_off_the_screen_in_the_sidebar_brings_its_card_into_view()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 2400, 1600)));
        Rect Card() => new(shell.Node("Build").TranslatePoint(default, shell.Editor)!.Value, shell.Node("Build").Bounds.Size);
        Assert.False(new Rect(shell.Editor.Bounds.Size).Intersects(Card()));

        shell.Click(SidebarRow(shell, "Build"));
        WaitForPan(shell);

        Assert.True(new Rect(shell.Editor.Bounds.Size).Contains(Card()), $"{Card()} is outside {shell.Editor.Bounds.Size}");
        Assert.Equal(new Point(2161, 1272), shell.Editor.ViewportLocation);
        Assert.Equal(new Point(2161, 1272), shell.Find<Minimap>("Minimap").ViewportLocation);
    }

    [AvaloniaFact]
    public void Choosing_a_task_off_the_screen_in_the_sidebar_with_the_keyboard_brings_its_card_into_view()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 2400, 1600)));
        shell.Click(SidebarRow(shell, "Design"));
        WaitForPan(shell);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);

        shell.Press(Key.Down);
        WaitForPan(shell);

        Assert.True(shell.Node("Build").IsSelected);
        Assert.Equal(new Point(2161, 1272), shell.Editor.ViewportLocation);
    }

    private Shell SelectBuildOnTheCanvasAndPanItAway()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 405, 90)));
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
        var design = SidebarRow(shell, "Design");

        // The rows are 4 px apart, so 2 px below Design is between the rows.
        shell.Click(design.TranslatePoint(new Point(design.Bounds.Width / 2, design.Bounds.Height + 2), shell.Window)!.Value);
        Assert.Equal(new Point(600, 300), shell.Editor.ViewportLocation);

        shell.Click(SidebarRow(shell, "Build"));

        Assert.Equal(new Point(166, -238), shell.Editor.ViewportLocation);
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
        Assert.Same(SidebarRow(shell, "Build"), shell.Window.FocusManager!.GetFocusedElement());

        shell.Press(key);

        Assert.Equal(new Point(166, -238), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void Pressing_and_releasing_a_card_partly_off_the_screen_leaves_the_canvas_where_it_is()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", PartlyOffScreen, 90)));
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
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", PartlyOffScreen, 90)));
        var header = shell.Header(shell.Node("Design"));

        shell.Drag(header, header - new Vector(195, 0));
        WaitForPan(shell);

        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);
        Assert.Equal(new Point(360, 90), shell.Node("Design").Location);
    }

    [AvaloniaFact]
    public void Selecting_a_card_partly_off_the_screen_with_a_rubber_band_leaves_the_canvas_where_it_is()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", PartlyOffScreen, 90)));
        var canvas = shell.Editor.TranslatePoint(default, shell.Window)!.Value;

        shell.Drag(canvas + new Vector(450, 300), canvas + new Vector(650, 60));
        WaitForPan(shell);

        Assert.True(shell.Node("Design").IsSelected);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void Selecting_every_card_leaves_the_canvas_where_it_is()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", PartlyOffScreen, 90)));

        // NodifyAvalonia 6.6.0 never matches its commands' key gestures, so Ctrl+A does nothing and the test runs the command.
        EditorCommands.SelectAll.Execute(null, shell.Editor);
        WaitForPan(shell);

        Assert.True(shell.Node("Design").IsSelected);
        Assert.Equal(new Point(0, 0), shell.Editor.ViewportLocation);
    }

    [AvaloniaFact]
    public void Tab_moves_through_the_sidebar_from_top_to_bottom()
    {
        var shell = Shell.Open(_temp.Seed(Task(Design, "Design", 105, 90), Task(Build, "Build", 405, 90)));
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

        Assert.Equal(["OpenFolder", "Design", "ThemeSystem", "ThemeLight", "ThemeDark"], [Tab(), Tab(), Tab(), Tab(), Tab()]);
    }

    [AvaloniaFact]
    public void A_long_status_in_the_smallest_window_covers_neither_the_breadcrumb_nor_the_canvas_controls()
    {
        var (design, build) = ($"Design {LongTitle}", $"Build {LongTitle}");
        var shell = Shell.Open(_temp.Seed(
            Task(Design, design, 105, 90),
            Task(Build, build, 405, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.Drag(shell.Center(shell.Output(build)), shell.Center(shell.Input(design)));
        Assert.Equal($"That would create a cycle: {build} → {design} → {build}.", shell.Status);

        shell.Window.Width = shell.Window.MinWidth;
        shell.Window.Height = shell.Window.MinHeight;
        shell.Render();

        Rect Bounds(Visual visual) => new(visual.TranslatePoint(default, shell.Window)!.Value, visual.Bounds.Size);
        Border Around(Visual visual, string kind) => visual.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains(kind));
        var status = Bounds(Around(shell.Find<TextBlock>("Status"), "breadcrumb"));
        Assert.Equal(new Size(900, 600), shell.Window.ClientSize);
        Assert.All(
            [Bounds(shell.Find<Border>("Breadcrumb")), Bounds(Around(shell.Find<Button>("ZoomIn"), "floating")), Bounds(Around(shell.Find<Minimap>("Minimap"), "floating"))],
            other => Assert.False(status.Intersects(other), $"The status at {status} covers {other}"));
    }

    [AvaloniaFact]
    public void Before_a_folder_is_open_new_task_and_save_are_unavailable_and_the_inspector_is_empty()
    {
        var shell = Shell.Show();

        Assert.Equal([false, false, true], new[] { "AddTask", "Save", "OpenFolder" }.Select(id => shell.Find<Button>(id).IsEffectivelyEnabled));
        Assert.Equal(["Inspector"], VisibleTexts(shell.Find<Control>("Inspector")));

        shell.Window.ViewModel.Open(_temp.Create("plan"));
        shell.Render();

        Assert.Equal([true, true, true], new[] { "AddTask", "Save", "OpenFolder" }.Select(id => shell.Find<Button>(id).IsEffectivelyEnabled));
        Assert.Equal(["Inspector", "Select a task or connection to edit it."], VisibleTexts(shell.Find<Control>("Inspector")));
        Assert.Equal("plan", shell.Find<TextBlock>("ProjectName").Text);
    }
}
