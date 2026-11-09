using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Inspector;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The Room: the panel at the size of the Map and the panel together, its Lineage Strip, and the Enter and Esc distances.</summary>
[Collection(ProcessCollection.Name)]
public sealed class RoomTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private Shell Chain(int width = 1500, int height = 900)
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90, Codex), TaskAt(Review, "Review", 825, 90, Codex),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency)));
        shell.Window.Width = width;
        shell.Window.Height = height;
        shell.Render();
        return shell;
    }

    private static AgentPanelViewModel Panel(Shell shell) => shell.Window.ViewModel.AgentPanel;

    private static WorkflowCanvasViewModel Canvas(Shell shell) => shell.Window.ViewModel.Canvas!;

    private static Visual? Focused(Shell shell) => shell.Window.FocusManager?.GetFocusedElement() as Visual;

    private static bool FocusIn(Shell shell, string id)
    {
        var host = Shell.ById<Control>(shell.Window, id).Single();
        return Focused(shell) is { } focused && (focused == host || host.IsVisualAncestorOf(focused));
    }

    private static void OpenRoomOn(Shell shell, string title)
    {
        shell.Click(shell.Header(shell.Node(title)));
        shell.Editor.Focus();
        shell.Press(Key.Enter, RawInputModifiers.Shift);
        shell.Render();
    }

    [AvaloniaFact]
    public void Shift_Enter_on_a_card_fills_the_Map_and_panel_area_and_puts_the_keys_in_the_composer()
    {
        var shell = Chain();

        OpenRoomOn(shell, "Build");

        Assert.True(Panel(shell).IsRoom);
        Assert.False(shell.Editor.IsEffectivelyVisible);
        var strip = shell.Find<Control>("LineageStrip");
        Assert.Equal(44, strip.Bounds.Height);
        Assert.True(shell.Find<Control>("Inspector").Bounds.Width > 1000);
        Assert.True(FocusIn(shell, "ConversationView"));
    }

    [AvaloniaFact]
    public void A_wide_Room_shows_Chat_beside_a_348_px_page_and_the_strip_has_no_Chat_tab()
    {
        var shell = Chain(1280, 800);

        OpenRoomOn(shell, "Build");

        Assert.True(Panel(shell).IsPinned);
        Assert.True(Panel(shell).IsChat);
        Assert.False(shell.Find<PageTab>("PageChat").IsVisible);
        var chat = shell.Find<Control>("ChatPage");
        var page = shell.Find<Control>("AgentPage").FindAncestorOfType<ScrollViewer>()!;
        Assert.True(chat.IsEffectivelyVisible && page.IsEffectivelyVisible);
        Assert.Equal(348, page.Bounds.Width);
        Assert.True(chat.Bounds.Width > 600, $"Chat is {chat.Bounds.Width} wide.");
        Assert.True(chat.Bounds.Right <= page.Bounds.Left);
    }

    [AvaloniaFact]
    public void A_narrow_Room_shows_one_column_and_a_tab_replaces_Chat_with_its_page()
    {
        var shell = Chain(1000, 700);

        OpenRoomOn(shell, "Build");

        Assert.True(Panel(shell).IsRoom);
        Assert.False(Panel(shell).IsPinned);
        Assert.True(shell.Find<Control>("ChatPage").IsEffectivelyVisible);
        shell.Click(shell.Find<PageTab>("PageTask"));
        Assert.False(shell.Find<Control>("ChatPage").IsEffectivelyVisible);
        Assert.True(shell.Find<Control>("TaskPage").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Lineage_neighbors_navigate_and_the_Room_stays_open()
    {
        var shell = Chain();
        OpenRoomOn(shell, "Build");

        shell.Click(shell.Find<Button>("LineageBefore"));

        Assert.True(Panel(shell).IsRoom);
        Assert.Equal("Design", Canvas(shell).SelectedNode!.Title);
        Assert.False(shell.Find<Button>("LineageBefore").IsVisible);

        shell.Click(shell.Find<Button>("LineageAfter"));

        Assert.Equal("Build", Canvas(shell).SelectedNode!.Title);
        Assert.Equal("Build", shell.Window.ViewModel.Conversation!.Node.Title);
    }

    [AvaloniaFact]
    public void Several_neighbors_are_a_choice_and_the_button_lists_them()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90, Codex), TaskAt(Review, "Review", 465, 400, Codex),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Review), ConnectionKind.Dependency)));
        OpenRoomOn(shell, "Design");
        var after = shell.Find<Button>("LineageAfter");

        shell.Click(after);

        Assert.Equal("Design", Canvas(shell).SelectedNode!.Title);
        Assert.True(FlyoutBase.GetAttachedFlyout(after)!.IsOpen);
        var links = shell.NodeModel("Design").Outputs;
        Assert.Equal(2, links.Count);
        Assert.Equal("+1", shell.NodeModel("Design").MoreSuccessors);
    }

    [AvaloniaFact]
    public void Esc_returns_to_the_panel_with_the_same_node_and_a_second_Esc_returns_to_its_card()
    {
        var shell = Chain();
        OpenRoomOn(shell, "Build");
        shell.Click(shell.Find<Button>("LineageAfter"));
        shell.Find<Control>("TranscriptScroller").Focus();
        shell.Render();

        shell.Press(Key.Escape);
        shell.Render();

        Assert.False(Panel(shell).IsRoom);
        Assert.True(shell.Editor.IsEffectivelyVisible);
        Assert.Equal("Review", Canvas(shell).SelectedNode!.Title);
        Assert.True(shell.Find<Control>("Inspector").Bounds.Width < 500);
        Assert.True(FocusIn(shell, "ConversationView"));

        shell.Press(Key.Escape);
        shell.Render();

        Assert.True(Focused(shell) == shell.Editor || shell.Editor.IsVisualAncestorOf(Focused(shell)!));
        Assert.Equal("Review", Canvas(shell).SelectedNode!.Title);
    }

    [AvaloniaFact]
    public void The_header_button_opens_the_Room_on_the_same_page_and_closes_it_again()
    {
        var shell = Chain();
        shell.Click(shell.Header(shell.Node("Build")));
        shell.Click(shell.Find<PageTab>("PageTask"));

        shell.Click(shell.Find<Button>("RoomToggle"));

        Assert.True(Panel(shell).IsRoom);
        Assert.Equal(AgentPage.Task, Panel(shell).Page);
        Assert.True(shell.Find<Control>("TaskPage").IsEffectivelyVisible);
        Assert.True(shell.Find<Control>("ChatPage").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("RoomToggle"));

        Assert.False(Panel(shell).IsRoom);
        Assert.True(shell.Editor.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_Room_ends_with_its_node_selection()
    {
        var shell = Chain();
        OpenRoomOn(shell, "Build");

        Canvas(shell).ClearSelection();
        shell.Render();

        Assert.False(Panel(shell).IsRoom);
        Assert.True(shell.Editor.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Closing_the_panel_from_the_Room_shows_the_Map()
    {
        var shell = Chain();
        OpenRoomOn(shell, "Build");

        shell.Click(shell.Find<Button>("ClosePanel"));

        Assert.False(Panel(shell).IsRoom);
        Assert.False(shell.Window.ViewModel.IsInspectorShown);
        Assert.True(shell.Editor.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Enter_on_a_row_opens_Chat_and_Shift_Enter_opens_the_Room()
    {
        var shell = Chain();
        Canvas(shell).Groups.First().Open();
        shell.Render();
        shell.Click(shell.SidebarRow("Build"));
        shell.Click(shell.Find<PageTab>("PageTask"));
        shell.SidebarRow("Build").Focus();

        shell.Press(Key.Enter);
        shell.Render();

        Assert.True(Panel(shell).IsChat);
        Assert.False(Panel(shell).IsRoom);

        shell.SidebarRow("Build").Focus();
        shell.Press(Key.Enter, RawInputModifiers.Shift);
        shell.Render();

        Assert.True(Panel(shell).IsRoom);
    }

    [AvaloniaFact]
    public void A_panel_at_880_or_wider_pins_Chat_beside_the_last_page_and_Alt_1_goes_to_the_composer()
    {
        var shell = Chain(2560, 1440);
        shell.Click(shell.Header(shell.Node("Build")));
        shell.Click(shell.Find<PageTab>("PageOutput"));

        Assert.True(Panel(shell).IsPinned);
        Assert.False(Panel(shell).IsRoom);
        Assert.True(shell.Find<Control>("ChatPage").IsEffectivelyVisible);
        Assert.True(shell.Find<Control>("OutputPage").IsEffectivelyVisible);

        shell.Press(Key.D1, RawInputModifiers.Alt);
        shell.Render();

        Assert.Equal(AgentPage.Chat, Panel(shell).Page);
        Assert.Equal(AgentPage.Output, Panel(shell).ShownPage);
        Assert.True(shell.Find<Control>("OutputPage").IsEffectivelyVisible);
        Assert.True(FocusIn(shell, "ConversationView"));
    }

    [Theory]
    [InlineData(1006, false, 353)]
    [InlineData(600, false, 320)]
    [InlineData(2000, false, 480)]
    [InlineData(1400, true, 348)]
    public void The_page_column_has_the_brief_widths(double available, bool isRoom, double expected) =>
        Assert.Equal(expected, PanelWidths.PageColumnWidth(available, isRoom));
}
