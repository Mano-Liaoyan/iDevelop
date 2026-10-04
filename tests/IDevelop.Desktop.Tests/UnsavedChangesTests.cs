using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

public sealed class UnsavedChangesTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly string _seed;
    private readonly string _other;
    private int _picks;

    public UnsavedChangesTests()
    {
        _seed = _temp.Seed(TaskAt(TaskId.New(), "Design", 105, 90));
        _other = _temp.Create("other");
    }

    public void Dispose() => _temp.Dispose();

    private string Blocker => Path.Combine(_seed, ".idp", "workflows", "other.json");

    private Shell OpenWithCountingPicker()
    {
        var shell = Shell.Open(_seed);
        shell.Window.PickFolder = () =>
        {
            _picks++;
            return Task.FromResult<string?>(_other);
        };
        return shell;
    }

    private Shell OpenWithUnsavedChanges()
    {
        var shell = OpenWithCountingPicker();
        shell.Click(shell.Find<Button>("AddTask"));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        return shell;
    }

    private string[] SavedTitles() => [.. WorkflowDocument.Open(_seed).Current.Tasks.Values.Select(task => task.Title).Order()];

    [AvaloniaFact]
    public void Open_folder_asks_first_and_cancel_keeps_the_document()
    {
        var shell = OpenWithUnsavedChanges();

        shell.Click(shell.Find<Button>("OpenFolder"));

        Assert.Equal(["Save changes to seed?", "Save", "Don't save", "Cancel"], shell.DialogTexts());
        shell.Choose("CancelChanges");
        Assert.Null(shell.Dialog);
        Assert.Equal(0, _picks);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        Assert.Equal(2, shell.Nodes().Count());
    }

    [AvaloniaFact]
    public void Open_folder_then_dont_save_opens_the_picked_folder_and_saves_nothing()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Click(shell.Find<Button>("OpenFolder"));

        shell.Choose("DiscardChanges");

        Assert.Equal(1, _picks);
        Assert.Equal("other - iDevelop", shell.Window.Title);
        Assert.Empty(shell.Nodes());
        Assert.Equal(["Design"], SavedTitles());
    }

    [AvaloniaFact]
    public void Open_folder_then_save_saves_and_opens_the_picked_folder()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Click(shell.Find<Button>("OpenFolder"));

        shell.Choose("SaveChanges");

        Assert.Equal(1, _picks);
        Assert.Equal("other - iDevelop", shell.Window.Title);
        Assert.Equal(["Design", "New task"], SavedTitles());
    }

    [AvaloniaFact]
    public void Open_folder_then_dont_save_then_cancelling_the_picker_keeps_the_document()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Window.PickFolder = () => Task.FromResult<string?>(null);
        shell.Click(shell.Find<Button>("OpenFolder"));

        shell.Choose("DiscardChanges");

        Assert.Null(shell.Dialog);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        Assert.Equal(2, shell.Nodes().Count());
        Assert.Equal(["Design"], SavedTitles());
    }

    [AvaloniaFact]
    public void Closing_the_prompt_with_its_title_bar_keeps_the_document()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Click(shell.Find<Button>("OpenFolder"));

        shell.Dialog!.Close();
        shell.Render();

        Assert.Null(shell.Dialog);
        Assert.Equal(0, _picks);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Open_folder_then_a_failed_save_keeps_the_document()
    {
        var shell = OpenWithUnsavedChanges();
        File.WriteAllText(Blocker, "{}");
        shell.Click(shell.Find<Button>("OpenFolder"));

        shell.Choose("SaveChanges");

        Assert.Null(shell.Dialog);
        Assert.Equal(0, _picks);
        Assert.Equal($"Not saved. {Blocker} is another workflow file, and this version of iDevelop keeps one workflow per project.", shell.Status);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Closing_asks_first_and_cancel_keeps_the_window_open()
    {
        var shell = OpenWithUnsavedChanges();

        shell.Window.Close();
        shell.Render();

        Assert.Equal(["Save changes to seed?", "Save", "Don't save", "Cancel"], shell.DialogTexts());
        shell.Choose("CancelChanges");
        Assert.Null(shell.Dialog);
        Assert.True(shell.Window.IsVisible);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void The_prompt_asks_its_question_in_its_title_and_focuses_save()
    {
        var shell = OpenWithUnsavedChanges();

        shell.Window.Close();
        shell.Render();

        var dialog = Assert.IsType<UnsavedChangesDialog>(shell.Dialog);
        Assert.Equal("Save changes to seed?", dialog.Title);
        Assert.Equal("SaveChanges", AutomationProperties.GetAutomationId(Assert.IsAssignableFrom<Control>(dialog.FocusManager?.GetFocusedElement())));
        // A test that passes with the prompt still open never finishes, because OnClosing still awaits it.
        shell.Choose("CancelChanges");
    }

    [AvaloniaFact]
    public void Pressing_enter_in_the_prompt_saves_and_closes()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Window.Close();
        shell.Render();

        shell.PressInDialog(Key.Enter);

        Assert.False(shell.Window.IsVisible);
        Assert.Equal(["Design", "New task"], SavedTitles());
    }

    [AvaloniaFact]
    public void Pressing_escape_in_the_prompt_keeps_the_window_open()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Window.Close();
        shell.Render();

        shell.PressInDialog(Key.Escape);

        Assert.Null(shell.Dialog);
        Assert.True(shell.Window.IsVisible);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
        Assert.Equal(["Design"], SavedTitles());
    }

    [AvaloniaFact]
    public void Closing_then_dont_save_closes_without_saving()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Window.Close();
        shell.Render();

        shell.Choose("DiscardChanges");

        Assert.False(shell.Window.IsVisible);
        Assert.Equal(["Design"], SavedTitles());
    }

    [AvaloniaFact]
    public void Closing_then_save_saves_and_closes()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Window.Close();
        shell.Render();

        shell.Choose("SaveChanges");

        Assert.False(shell.Window.IsVisible);
        Assert.Equal(["Design", "New task"], SavedTitles());
    }

    [AvaloniaFact]
    public void Closing_then_a_failed_save_keeps_the_window_open()
    {
        var shell = OpenWithUnsavedChanges();
        File.WriteAllText(Blocker, "{}");
        shell.Window.Close();
        shell.Render();

        shell.Choose("SaveChanges");

        Assert.Null(shell.Dialog);
        Assert.True(shell.Window.IsVisible);
        Assert.Equal($"Not saved. {Blocker} is another workflow file, and this version of iDevelop keeps one workflow per project.", shell.Status);
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void A_second_close_while_the_prompt_is_open_opens_no_second_prompt()
    {
        var shell = OpenWithUnsavedChanges();
        shell.Window.Close();
        shell.Render();

        shell.Window.Close();
        shell.Render();

        Assert.Single(shell.Window.OwnedWindows);
        shell.Choose("DiscardChanges");
        Assert.Empty(shell.Window.OwnedWindows);
        Assert.False(shell.Window.IsVisible);
        Assert.Equal(["Design"], SavedTitles());
    }

    [AvaloniaFact]
    public void Closing_during_open_folder_is_cancelled_until_the_folder_opens()
    {
        var shell = OpenWithUnsavedChanges();
        var picker = new TaskCompletionSource<string?>();
        shell.Window.PickFolder = () => picker.Task;
        shell.Click(shell.Find<Button>("OpenFolder"));

        shell.Window.Close();
        shell.Render();
        Assert.Single(shell.Window.OwnedWindows);
        shell.Choose("DiscardChanges");
        shell.Window.Close();
        shell.Render();

        Assert.Empty(shell.Window.OwnedWindows);
        Assert.True(shell.Window.IsVisible);
        picker.SetResult(_other);
        shell.Render();
        Assert.Equal("other - iDevelop", shell.Window.Title);
        shell.Window.Close();
        shell.Render();
        Assert.False(shell.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Without_unsaved_changes_open_folder_and_closing_do_not_ask()
    {
        var shell = OpenWithCountingPicker();

        shell.Click(shell.Find<Button>("OpenFolder"));

        Assert.Null(shell.Dialog);
        Assert.Equal(1, _picks);
        Assert.Equal("other - iDevelop", shell.Window.Title);
        shell.Window.Close();
        shell.Render();
        Assert.Null(shell.Dialog);
        Assert.False(shell.Window.IsVisible);
    }
}
