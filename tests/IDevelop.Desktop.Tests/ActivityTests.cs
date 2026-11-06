using IDevelop.Desktop.Inspector;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.VisualTree;
using IDevelop.Clients;
using IDevelop.Desktop.Sidebar;
using IDevelop.Desktop.Theme;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>The left area's Activity list, Find, and the words that sum up a workflow's run.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ActivityTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, "No nodes yet")]
    [InlineData(0, 0, 0, 0, 0, 1, "1 node")]
    [InlineData(0, 0, 0, 0, 0, 9, "9 nodes")]
    [InlineData(5, 1, 3, 0, 0, 9, "5 of 9 · 1 running · 3 waiting")]
    [InlineData(5, 0, 1, 2, 1, 9, "5 of 9 · 1 waiting · 2 failed · 1 stale")]
    [InlineData(9, 0, 0, 0, 0, 9, "9 of 9")]
    public void A_workflow_s_summary_uses_the_run_bar_s_words(int completed, int running, int waiting, int failed, int stale, int total, string summary) =>
        Assert.Equal(summary, new StateCounts(completed, running, waiting, failed, stale, total).Summary());

    [Fact]
    public void A_client_with_a_problem_changes_the_footer_s_words_and_a_missing_client_does_not()
    {
        ClientStatus ready = new ClientStatus.Ready(null!, []), missing = new ClientStatus.Missing("none"), unready = new ClientStatus.Unready("signed out");

        Assert.Equal("2 agents ready", Execution.RunText.AgentsSummary([ready, ready, missing]));
        Assert.Equal("1 needs setup", Execution.RunText.AgentsSummary([ready, unready]));
        Assert.Equal("2 need setup", Execution.RunText.AgentsSummary([unready, unready]));
        Assert.Equal("Checking…", Execution.RunText.AgentsSummary([new ClientStatus.Checking(), missing]));
    }

    [Fact]
    public void The_short_list_shows_each_need_before_a_second_row_of_any_need()
    {
        static bool Failed(string row) => row[0] == 'f';
        Assert.Equal(["r1", "q1", "f1"], Sidebar.ActivityViewModel.Shortened(["r1", "r2", "q1", "q2", "f1"], 3, row => row[0], Failed));
        Assert.Equal(["r1", "r2", "f1"], Sidebar.ActivityViewModel.Shortened(["r1", "r2", "r3", "f1"], 3, row => row[0], Failed));
        Assert.Equal(["r1", "r2"], Sidebar.ActivityViewModel.Shortened(["r1", "r2", "r3"], 2, _ => 'r', Failed));
        Assert.Equal(["r1", "f1"], Sidebar.ActivityViewModel.Shortened(["r1", "q1", "a1", "f1"], 2, row => row[0], Failed));
    }

    [Fact]
    public void A_running_node_s_line_is_its_last_line_of_text() =>
        Assert.Equal(("Bash dotnet test", null), (Sidebar.ActivityRow.OneLine("Reading\n\nBash dotnet test\n"), Sidebar.ActivityRow.OneLine(null)));

    private Shell OpenWaiting(int waiting)
    {
        var folder = _temp.Create("alpha");
        var document = WorkflowDocument.Create(folder, "Build");
        foreach (var index in Enumerable.Range(1, waiting))
        {
            Assert.IsType<EditResult.Applied>(document.Apply(TaskAt(TaskId.New(), $"Node {index}", 105 + 300 * index, 90)));
        }

        document.Save();
        return Shell.Open(folder);
    }

    private static string[] Find(Shell shell, string query)
    {
        shell.Window.ViewModel.Find.Query = query;
        shell.Render();
        return [.. shell.Window.ViewModel.Find.Results.Select(result => $"{result.Name} | {result.Place}")];
    }

    [AvaloniaFact]
    public void Keyboard_focus_shows_one_accent_ring_on_the_control_s_edge_and_a_placeholder_stays_readable()
    {
        var shell = OpenWaiting(1);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        var open = shell.Find<Button>("OpenFolder");

        open.Focus(NavigationMethod.Tab);
        shell.Render();

        // The ring is 2 px wide, over the button's edge.
        Assert.Equal(Avalonia.Media.Color.Parse("#0088FF"), shell.ColorAt(open, new Avalonia.Point(open.Bounds.Width / 2, -1)));
        var watermark = shell.Find<TextBox>("FindBox").GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Watermark");
        Assert.Equal(1, watermark.Opacity);
    }

    [AvaloniaFact]
    public void Activity_hides_while_nothing_needs_a_person_or_runs()
    {
        var shell = OpenWaiting(2);

        Assert.False(shell.Window.ViewModel.Activity.HasRows);
        Assert.DoesNotContain(Shell.ById<ItemsControl>(shell.Window, "Activity"), list => list.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Find_lists_nodes_and_workflows_of_every_project_and_says_when_nothing_matches()
    {
        var shell = OpenWaiting(3);
        var beta = _temp.Create("beta");
        var other = WorkflowDocument.Create(beta, "Node docs");
        other.Save();
        shell.Window.ViewModel.Open(beta);
        shell.Render();

        Assert.Equal(["Node docs | beta", "Node 1 | alpha › Build", "Node 2 | alpha › Build", "Node 3 | alpha › Build"], Find(shell, "node"));
        Assert.True(shell.Find<ListBox>("FindResults").IsEffectivelyVisible);
        Assert.True(shell.Find<ItemsControl>("Projects").IsEffectivelyVisible);

        Assert.Empty(Find(shell, "nothing like this"));
        Assert.True(shell.Find<TextBlock>("FindNoMatch").IsEffectivelyVisible);

        Assert.Empty(Find(shell, ""));
        Assert.True(shell.Find<ItemsControl>("Projects").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Find_by_keyboard_chooses_a_node_in_another_workflow_and_Esc_returns_the_focus()
    {
        var shell = OpenWaiting(3);
        shell.Window.ViewModel.NewWorkflow(shell.Window.ViewModel.Projects[0]);
        shell.Render();
        var box = shell.Find<TextBox>("FindBox");

        shell.Editor.Focus();
        shell.Press(Key.K, RawInputModifiers.Control);
        Assert.True(box.IsFocused, "Ctrl+K does not focus Find.");
        shell.Press(Key.Escape);
        Assert.True(shell.Editor.IsFocused, "Esc does not return the focus.");

        shell.Press(Key.K, RawInputModifiers.Control);
        box.Text = "node";
        shell.Render();
        shell.Press(Key.Down);
        shell.Press(Key.Enter);

        Assert.Equal("Node 2", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.Equal("", box.Text);
        Assert.True(shell.Editor.IsFocused, "The canvas does not take the keys after a choice.");
    }

    [AvaloniaFact]
    public void Up_and_Down_move_through_the_tree_and_Right_and_Left_open_and_close()
    {
        var shell = OpenWaiting(2);
        var project = shell.ProjectName("alpha").FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>()!;
        var row = shell.WorkflowRow("alpha", "Build");
        var canvas = shell.Window.ViewModel.Canvas!;
        project.Focus();

        shell.Press(Key.Down);
        Assert.True(row.IsFocused, "Down does not reach the workflow.");

        shell.Press(Key.Right);
        Assert.True(canvas.IsExpanded, "Right does not open the workflow.");
        shell.Press(Key.Right);
        shell.Press(Key.Down);
        Assert.Equal("Node 2", shell.Window.FocusManager!.GetFocusedElement() is ListBoxItem { DataContext: Canvas.TaskNodeViewModel node } ? node.Title : null);

        shell.Press(Key.Left);
        Assert.True(row.IsFocused, "Left does not move to the parent workflow.");
        shell.Press(Key.Left);
        Assert.False(canvas.IsExpanded, "Left does not close the workflow.");
        shell.Press(Key.Left);
        Assert.True(project.IsFocused, "Left does not move to the parent project.");
        shell.Press(Key.Left);
        Assert.False(shell.Window.ViewModel.Projects[0].IsExpanded, "Left does not fold the project.");
    }

    [AvaloniaFact]
    public void Activity_lists_who_waits_before_who_runs_and_a_row_opens_the_conversation_with_the_composer_focused()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var view = shell.Window.ViewModel;

        Assert.Equal(["Write docs · Reply · Workflow", "Build the API · Running · Workflow"], view.Activity.Rows.Select(row => $"{row.Node.Title} · {row.Reason} · {row.Place}"));
        Assert.True(shell.Find<ItemsControl>("Activity").IsEffectivelyVisible);
        Assert.Equal("1 of 3 · 1 running · 1 waiting", view.Canvas!.Summary);
        Assert.Equal(1, view.Projects[0].WaitingCount);

        shell.Click(shell.Window.GetVisualDescendants().OfType<Button>().First(row => row.Classes.Contains("activity")));
        shell.Render();

        Assert.Equal("Write docs", view.Conversation?.Node.Title);
        shell.WaitUntil(() => shell.Window.FocusManager!.GetFocusedElement() is TextBox { Name: "Composer" }, "the composer takes the focus");
    }

    [AvaloniaFact]
    public void A_short_panel_folds_Activity_to_its_heading_and_the_heading_opens_it()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var activity = shell.Window.ViewModel.Activity;

        shell.Window.Height = 600;
        shell.Render();
        Assert.Equal((false, "Show Activity", false), (activity.IsOpen, activity.FoldLabel, activity.IsFolded));

        activity.IsOpen = true;
        Assert.Equal((true, false), (activity.IsOpen, activity.IsFolded));

        activity.IsOpen = false;
        shell.Window.Height = 900;
        shell.Render();
        Assert.True(activity.IsOpen, "A tall panel does not take back the person's own choice.");
    }

    [AvaloniaFact]
    public void The_heading_s_counts_add_up_to_the_list()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var activity = shell.Window.ViewModel.Activity;

        Assert.Equal(activity.Count, activity.WaitingCount + activity.FailedCount + activity.RunningCount);
    }

    [AvaloniaFact]
    public void Show_all_expands_a_long_list_in_place()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var activity = shell.Window.ViewModel.Activity;

        activity.Limit = 1;
        Assert.Equal(("Show all 2", true, 1), (activity.ShowAllLabel, activity.HasMore, activity.Rows.Count));

        activity.ShowAllCommand.Execute(null);
        Assert.Equal((false, 2), (activity.HasMore, activity.Rows.Count));
    }

    [AvaloniaFact]
    public void The_window_s_keys_reach_the_next_waiting_node_its_conversation_and_the_inspector()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        shell.Editor.Focus();

        shell.Press(Key.Enter, RawInputModifiers.Control | RawInputModifiers.Shift);
        shell.WaitForState("Write docs", Canvas.NodeState.InputRequired);
        shell.WaitForState("Build the API", Canvas.NodeState.Running);

        var view = shell.Window.ViewModel;
        shell.Press(Key.J, RawInputModifiers.Control);
        Assert.Equal("Write docs", view.Canvas!.SelectedNode?.Title);
        Assert.True(shell.Editor.IsKeyboardFocusWithin, "The canvas does not take the keys after Ctrl+J.");

        shell.Press(Key.M, RawInputModifiers.Alt);
        Assert.Equal("TaskModel", Avalonia.Automation.AutomationProperties.GetAutomationId((Control)shell.Window.FocusManager!.GetFocusedElement()!));

        shell.Press(Key.F, RawInputModifiers.Control);
        Assert.Equal("InspectorFilter", Avalonia.Automation.AutomationProperties.GetAutomationId((Control)shell.Window.FocusManager!.GetFocusedElement()!));

        shell.Editor.Focus();
        shell.Press(Key.Enter);
        Assert.Equal("Write docs", view.Conversation?.Node.Title);
    }

    [AvaloniaFact]
    public void F6_moves_through_the_regions_in_order_and_Shift_F6_moves_back()
    {
        var shell = OpenWaiting(1);
        shell.Editor.Focus();
        string Region()
        {
            var focused = (Control)shell.Window.FocusManager!.GetFocusedElement()!;
            return focused.FindAncestorOfType<SidebarView>() is not null ? "left"
                : shell.Editor.IsKeyboardFocusWithin ? "canvas"
                : focused.FindAncestorOfType<InspectorView>() is not null ? "inspector" : "other";
        }

        List<string> seen = [];
        foreach (var _ in Enumerable.Range(0, 3))
        {
            shell.Press(Key.F6);
            seen.Add(Region());
        }

        shell.Press(Key.F6, RawInputModifiers.Shift);
        seen.Add(Region());

        Assert.Equal(["inspector", "left", "canvas", "left"], seen);
    }

    [Fact]
    public void The_session_keeps_a_folded_Activity_list()
    {
        var file = Path.GetTempFileName();
        try
        {
            (WorkspaceSession.Empty with { ActivityFolded = true }).Write(file);

            Assert.True(WorkspaceSession.Read(file).ActivityFolded);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
