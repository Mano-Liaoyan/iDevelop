using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

public sealed class UndoTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private Shell OpenChain() => Shell.Open(_temp.Seed(
        TaskAt(Design, "Design", 105, 90),
        TaskAt(Build, "Build", 405, 90),
        TaskAt(Review, "Review", 405, 300),
        new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency),
        new Connect(new ConnectionKey(Design, Review), ConnectionKind.Context)));

    private static string[] Titles(Shell shell) => [.. shell.Nodes().Select(node => ((TaskNodeViewModel)node.DataContext!).Title).Order()];

    private static void Delete(Shell shell, string title)
    {
        shell.Click(shell.Header(shell.Node(title)));
        shell.Press(Key.Delete);
    }

    [AvaloniaFact]
    public void Ctrl_Z_brings_a_deleted_task_back_in_its_place_with_its_connections_and_Ctrl_Shift_Z_deletes_it_again()
    {
        var shell = OpenChain();
        Delete(shell, "Build");
        Assert.Equal(["Design", "Review"], Titles(shell));
        Assert.Equal("seed* - iDevelop", shell.Window.Title);

        shell.Press(Key.Z, RawInputModifiers.Control);

        Assert.Equal(["Design", "Build", "Review"], shell.Find<ListBox>("SidebarTasks").Items.Cast<TaskNodeViewModel>().Select(node => node.Title));
        Assert.Equal(
            [("Build", "Review", ConnectionKind.Dependency), ("Design", "Build", ConnectionKind.Dependency), ("Design", "Review", ConnectionKind.Context)],
            shell.Drawn());
        Assert.Equal("seed - iDevelop", shell.Window.Title);
        Assert.False(shell.ShowsUnsavedChanges);

        shell.Press(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.Equal(["Design", "Review"], Titles(shell));
        Assert.Equal([("Design", "Review", ConnectionKind.Context)], shell.Drawn());
        Assert.Equal("seed* - iDevelop", shell.Window.Title);
    }

    [AvaloniaFact]
    public void Ctrl_Y_redoes_too()
    {
        var shell = OpenChain();
        Delete(shell, "Build");
        shell.Press(Key.Z, RawInputModifiers.Control);
        Assert.Equal(["Build", "Design", "Review"], Titles(shell));

        shell.Press(Key.Y, RawInputModifiers.Control);

        Assert.Equal(["Design", "Review"], Titles(shell));
    }

    [AvaloniaFact]
    public void The_Undo_and_Redo_buttons_are_on_only_while_there_is_a_step_to_take()
    {
        var shell = OpenChain();
        var (undo, redo) = (shell.Find<Button>("Undo"), shell.Find<Button>("Redo"));
        Assert.Equal((false, false), (undo.IsEffectivelyEnabled, redo.IsEffectivelyEnabled));

        Delete(shell, "Build");
        Assert.Equal((true, false), (undo.IsEffectivelyEnabled, redo.IsEffectivelyEnabled));

        shell.Click(undo);
        Assert.Equal(["Build", "Design", "Review"], Titles(shell));
        Assert.Equal((false, true), (undo.IsEffectivelyEnabled, redo.IsEffectivelyEnabled));

        shell.Click(redo);
        Assert.Equal(["Design", "Review"], Titles(shell));
        Assert.Equal((true, false), (undo.IsEffectivelyEnabled, redo.IsEffectivelyEnabled));
    }

    [AvaloniaFact]
    public void Ctrl_Z_in_a_field_box_undoes_the_box_text_and_leaves_the_workflow_alone()
    {
        var shell = OpenChain();
        Delete(shell, "Review");
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Draft");

        shell.Press(Key.Z, RawInputModifiers.Control);

        Assert.Equal("Draf", shell.Find<TextBox>("TaskInstructions").Text);
        Assert.Equal("Draf", shell.Window.ViewModel.Canvas!.Document.Current.Tasks[Design].Field("instructions"));
        Assert.Equal(["Build", "Design"], Titles(shell));
    }

    [AvaloniaFact]
    public void A_run_of_typing_in_the_title_box_undoes_as_one_step()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskTitle"));
        shell.Press(Key.End);
        shell.Type(" the API");
        Assert.Equal("Design the API", ((TaskNodeViewModel)shell.Node("Design the API").DataContext!).Title);

        shell.Click(shell.Header(shell.Node("Design the API")));
        shell.Press(Key.Z, RawInputModifiers.Control);

        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);
        Assert.Equal("seed - iDevelop", shell.Window.Title);
        Assert.False(shell.Find<Button>("Undo").IsEffectivelyEnabled);
    }
}
