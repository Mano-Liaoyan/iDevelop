using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Projects;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Sidebar;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>The Index's header, its Inbox view, the Find palette, and the Rail's flyout.</summary>
[Collection(ProcessCollection.Name)]
public sealed class IndexViewTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private Shell Seed(params string[] titles)
    {
        var folder = _temp.Create("alpha");
        var document = WorkflowDocument.Create(folder, "Build");
        foreach (var (title, index) in titles.Select((title, index) => (title, index)))
        {
            Assert.IsType<EditResult.Applied>(document.Apply(AppTempFolder.TaskAt(TaskId.New(), title, 105 + 300 * index, 90)));
        }

        document.Save();
        return Shell.Open(folder);
    }

    private static string[] Heads(Shell shell) =>
        [.. Shell.ById<TextBlock>(shell.Window, "InboxGroup").Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "")];

    [AvaloniaFact]
    public void The_Inbox_groups_its_rows_as_the_list_orders_them_and_a_row_shows_its_reason_and_place()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var view = shell.Window.ViewModel;

        shell.Click(shell.Find<RadioButton>("IndexInbox"));
        shell.Render();

        Assert.Equal(["Needs you", "Running"], Heads(shell));
        Assert.Equal(view.Activity.Rows.Select(row => row.Node.Title), view.Activity.Groups.SelectMany(group => group.Rows).Select(row => row.Node.Title));
        Assert.Equal("Reply ·", Shell.ById<TextBlock>(shell.Window, "ActivityReason").First().Text);
        Assert.False(shell.Find<ItemsControl>("Projects").IsEffectivelyVisible);
        Assert.Equal(view.Activity.WaitingCount.ToString(), shell.Find<TextBlock>("InboxCount").Text);
    }

    [AvaloniaFact]
    public void The_Inbox_count_adds_what_needs_the_person_and_what_failed()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var activity = shell.Window.ViewModel.Activity;

        Assert.Equal(activity.WaitingCount + activity.FailedCount, activity.InboxCount);
        Assert.Equal(activity.HasInbox, shell.Find<TextBlock>("InboxCount").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Ctrl_Tab_changes_the_Index_s_view_while_the_focus_is_in_the_Index()
    {
        var shell = Seed("One");
        shell.WorkflowRow("alpha", "Build").Focus();

        shell.Press(Key.Tab, RawInputModifiers.Control);
        Assert.True(shell.Window.ViewModel.IsInboxView);

        shell.Render();
        shell.Find<RadioButton>("IndexInbox").Focus();
        shell.Press(Key.Tab, RawInputModifiers.Control);
        Assert.False(shell.Window.ViewModel.IsInboxView);
    }

    [AvaloniaFact]
    public void Find_is_a_palette_560_wide_at_the_window_s_top_that_a_key_opens_and_Esc_closes()
    {
        var shell = Seed("One", "Two");
        shell.Press(Key.K, RawInputModifiers.Control);
        shell.Render();

        Assert.True(shell.Window.ViewModel.Find.IsOpen);
        var palette = shell.Find<Border>("FindPalette");
        Assert.Equal(560, palette.Bounds.Width);
        Assert.Equal(56, palette.TranslatePoint(default, shell.Window)!.Value.Y);
        Assert.Equal(shell.Window.Bounds.Width / 2, palette.TranslatePoint(new Point(palette.Bounds.Width / 2, 0), shell.Window)!.Value.X, 0.5);
        Assert.True(shell.Find<TextBox>("FindBox").IsFocused);

        shell.Press(Key.Escape);
        shell.Render();

        Assert.False(shell.Window.ViewModel.Find.IsOpen);
        Assert.False(palette.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_press_beside_the_Find_palette_closes_it()
    {
        var shell = Seed("One");
        shell.Press(Key.K, RawInputModifiers.Control);
        shell.Render();

        shell.Click(new Point(10, 500));
        shell.Render();

        Assert.False(shell.Window.ViewModel.Find.IsOpen);
    }

    [AvaloniaFact]
    public void A_click_on_the_Index_s_Find_button_opens_the_palette()
    {
        var shell = Seed("One");

        shell.Click(shell.Find<Button>("IndexFind"));
        shell.Render();

        Assert.True(shell.Window.ViewModel.Find.IsOpen);
        Assert.True(shell.Find<TextBox>("FindBox").IsFocused);
    }

    [AvaloniaFact]
    public void The_Rail_s_buttons_change_the_flyout_s_view_and_close_it_on_the_view_that_shows()
    {
        var shell = Seed("One");
        var view = shell.Window.ViewModel;

        view.OpenInbox();
        Assert.Equal((true, true), (view.IsIndexFlyoutOpen, view.IsInboxView));
        view.ToggleIndexFlyout();
        Assert.Equal((true, false), (view.IsIndexFlyoutOpen, view.IsInboxView));
        view.ToggleIndexFlyout();
        Assert.False(view.IsIndexFlyoutOpen);
    }

    [AvaloniaFact]
    public void A_project_row_names_the_last_two_segments_of_its_folder_and_keeps_the_whole_path_in_its_tooltip()
    {
        var shell = Seed("One");
        var project = shell.Window.ViewModel.Projects[0];

        Assert.Equal(string.Join('/', project.Folder.Split('/', '\\').TakeLast(2)), project.FolderTail);
        Assert.Equal(project.Folder, ToolTip.GetTip(shell.ProjectName("alpha").FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>()!));
    }

    [AvaloniaFact]
    public void A_project_with_no_workflows_offers_a_new_workflow_row()
    {
        var shell = Shell.Open(_temp.Create("empty"));
        shell.Render();

        Assert.True(Shell.ById<Button>(shell.Window, "EmptyNewWorkflow").Single().IsEffectivelyVisible || shell.Window.ViewModel.Projects[0].Workflows.Count > 0);
    }
}
