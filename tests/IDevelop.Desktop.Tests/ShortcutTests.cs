using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The canvas's keys, which act only while the canvas has focus.</summary>
public sealed class ShortcutTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private Shell OpenTwo() => Shell.Open(_temp.Seed(
        TaskAt(Design, "Design", 105, 90),
        TaskAt(Build, "Build", 505, 90),
        new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));

    private static string[] Selected(Shell shell) => [.. shell.Window.ViewModel.Canvas!.SelectedNodes.Select(node => node.Title).Order()];

    private static string[] Titles(Shell shell) => [.. shell.Window.ViewModel.Canvas!.Workflow.Tasks.Values.Select(task => task.Title).Order()];

    [AvaloniaFact]
    public void N_opens_the_popover_at_the_pointer()
    {
        var shell = OpenTwo();
        var pointer = shell.InEditor(200, 150);
        shell.Click(pointer);

        shell.Press(Key.N);

        Assert.True(shell.AddPopoverIsOpen);
        Assert.True(Point.Distance(pointer, shell.Bounds(shell.AddPopover).TopLeft) <= 2, $"{shell.Bounds(shell.AddPopover)} is not at {pointer}");
    }

    [AvaloniaFact]
    public void Delete_removes_the_selected_card_with_its_connections()
    {
        var shell = OpenTwo();
        shell.Click(shell.Header(shell.Node("Build")));

        shell.Press(Key.Delete);

        Assert.Equal(["Design"], Titles(shell));
        Assert.Empty(shell.Window.ViewModel.Canvas!.Workflow.Connections);
    }

    [AvaloniaFact]
    public void Ctrl_d_duplicates_and_ctrl_a_selects_every_card()
    {
        var shell = OpenTwo();
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Press(Key.D, RawInputModifiers.Control);
        Assert.Equal(["Build", "Design", "Design copy"], Titles(shell));
        Assert.Equal(["Design copy"], Selected(shell));

        shell.Press(Key.A, RawInputModifiers.Control);
        Assert.Equal(["Build", "Design", "Design copy"], Selected(shell));
    }

    [AvaloniaFact]
    public void F2_focuses_the_title_box_with_its_text_selected()
    {
        var shell = OpenTwo();
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Press(Key.F2);

        var title = shell.Find<TextBox>("TaskTitle");
        Assert.True(title.IsFocused);
        Assert.Equal("Design", title.SelectedText);
        shell.Type("Renamed");
        Assert.Equal(["Build", "Renamed"], Titles(shell));
    }

    [AvaloniaFact]
    public void Escape_closes_the_popover_first_and_then_clears_the_selection()
    {
        var shell = OpenTwo();
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Press(Key.N);
        Assert.True(shell.AddPopoverIsOpen);

        shell.Press(Key.Escape);
        Assert.False(shell.AddPopoverIsOpen);
        Assert.Equal(["Design"], Selected(shell));

        shell.Press(Key.Escape);
        Assert.Empty(Selected(shell));
    }

    [AvaloniaFact]
    public void Zero_zooms_to_100_percent_and_f_fits_the_cards()
    {
        var shell = OpenTwo();
        shell.Click(shell.InEditor(300, 500));
        shell.Press(Key.OemPlus);
        Assert.True(shell.Editor.ViewportZoom > 1);

        shell.Press(Key.D0);
        Assert.Equal(1, shell.Editor.ViewportZoom, 6);

        shell.Press(Key.OemMinus);
        Assert.True(shell.Editor.ViewportZoom < 1);

        shell.Press(Key.F);
        Assert.True(new Rect(shell.Editor.ViewportLocation, shell.Editor.ViewportSize).Contains(new Point(505, 90)));
    }

    [AvaloniaFact]
    public void N_typed_into_the_inspector_or_the_popover_search_adds_nothing()
    {
        var shell = OpenTwo();
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskTitle"));
        shell.Press(Key.End);

        shell.PressWithText(Key.N, "n");
        Assert.False(shell.AddPopoverIsOpen);
        Assert.Equal("Designn", shell.Find<TextBox>("TaskTitle").Text);

        shell.RightClick(shell.InEditor(300, 500));
        shell.PressWithText(Key.N, "n");
        Assert.True(shell.AddPopoverIsOpen);
        Assert.Equal("n", shell.Find<TextBox>("AddNodeSearch").Text);
        Assert.Equal(2, shell.Window.ViewModel.Canvas!.Workflow.Tasks.Count);
    }

    [AvaloniaFact]
    public void Alt_clicking_an_output_port_deletes_its_connections()
    {
        var shell = OpenTwo();

        shell.Click(shell.Thumb(shell.Output("Design")), RawInputModifiers.Alt);

        Assert.Empty(shell.Window.ViewModel.Canvas!.Workflow.Connections);
        Assert.Equal(["Build", "Design"], Titles(shell));
    }
}
