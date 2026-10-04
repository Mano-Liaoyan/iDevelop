using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

public sealed class MainWindowTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    // Unlimited, the status these titles produce would run about 78 lines at the window's minimum width.
    private static readonly string LongTitle = string.Join(" ", Enumerable.Repeat("with every step of the release written out in full", 30));

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
    public void Open_folder_does_nothing_while_an_earlier_open_is_still_in_progress()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        var other = _temp.Create("other");
        var picker = new TaskCompletionSource<string?>();
        var picks = 0;
        shell.Window.PickFolder = () =>
        {
            picks++;
            return picker.Task;
        };
        shell.Click(shell.Find<Button>("OpenFolder"));

        shell.Click(shell.Find<Button>("OpenFolder"));

        Assert.Equal(1, picks);
        picker.SetResult(other);
        shell.WaitUntil(() => shell.Window.Title == "other - iDevelop", "the picked folder opens");
    }

    [AvaloniaFact]
    public void A_long_status_in_the_smallest_window_covers_neither_the_breadcrumb_nor_the_canvas_controls()
    {
        var (design, build) = ($"Design {LongTitle}", $"Build {LongTitle}");
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, design, 105, 90),
            TaskAt(Build, build, 405, 90),
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

        Assert.Equal("iDevelop", shell.Window.Title);
        Assert.Equal([false, false, true], new[] { "AddTask", "Save", "OpenFolder" }.Select(id => shell.Find<Button>(id).IsEffectivelyEnabled));
        Assert.Equal(["Inspector"], Shell.Texts(shell.Find<Control>("Inspector")));

        shell.Window.ViewModel.Open(_temp.Create("plan"));
        shell.Render();

        Assert.Equal([true, true, true], new[] { "AddTask", "Save", "OpenFolder" }.Select(id => shell.Find<Button>(id).IsEffectivelyEnabled));
        Assert.Equal(["Inspector", "Select a task or connection to edit it."], Shell.Texts(shell.Find<Control>("Inspector")));
        Assert.Equal("plan", shell.Find<TextBlock>("ProjectName").Text);
    }
}
