using IDevelop.Desktop.Inspector;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    [InlineData(5, 1, 3, 0, 0, 9, "5 of 9 · 1 running · 3 need you")]
    [InlineData(5, 0, 1, 2, 1, 9, "5 of 9 · 1 needs you · 2 failed · 1 stale")]
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
        shell.Window.OpenFind();
        shell.Window.ViewModel.Find.Query = query;
        shell.Render();
        return [.. shell.Window.ViewModel.Find.Results.Select(result => $"{result.Name} | {result.Place}")];
    }

    [AvaloniaFact]
    public void Keyboard_focus_shows_one_accent_ring_on_the_control_s_edge_and_a_placeholder_stays_readable()
    {
        var shell = OpenWaiting(1);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        var open = shell.Find<Button>("IndexAdd");

        open.Focus(NavigationMethod.Tab);
        shell.Render();

        // The ring is 2 px wide, 1 px outside the button's edge.
        Assert.NotEqual(Avalonia.Media.Color.Parse("#1E6EF4"), shell.ColorAt(open, new Avalonia.Point(open.Bounds.Width / 2, -1)));
        Assert.Equal(Avalonia.Media.Color.Parse("#1E6EF4"), shell.ColorAt(open, new Avalonia.Point(open.Bounds.Width / 2, -2)));
        shell.Window.OpenFind();
        shell.Render();
        var watermark = shell.Find<TextBox>("FindBox").GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Watermark");
        Assert.Equal(1, watermark.Opacity);
    }

    [AvaloniaFact]
    public void The_Inbox_says_nothing_needs_you_while_no_agent_waits_failed_or_runs()
    {
        var shell = OpenWaiting(2);
        shell.Click(shell.Find<RadioButton>("IndexInbox"));

        Assert.False(shell.Window.ViewModel.Activity.HasRows);
        Assert.Equal("Nothing needs you.", shell.Find<TextBlock>("InboxEmpty").Text);
        Assert.False(shell.Find<ItemsControl>("Projects").IsEffectivelyVisible);
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
        shell.Editor.Focus();
        shell.Press(Key.K, RawInputModifiers.Control);
        shell.Render();
        var box = shell.Find<TextBox>("FindBox");
        Assert.True(box.IsFocused, "Ctrl+K does not focus Find.");
        shell.Press(Key.Escape);
        Assert.True(shell.Editor.IsFocused, "Esc does not return the focus.");

        shell.Press(Key.K, RawInputModifiers.Control);
        box.Text = "node";
        shell.Render();
        shell.Press(Key.Down);
        shell.Press(Key.Enter);

        Assert.Equal("Node 2", shell.Window.ViewModel.Canvas!.SelectedNode?.Title);
        Assert.Equal("", shell.Window.ViewModel.Find.Query);
        Assert.True(shell.Editor.IsFocused, "The canvas does not take the keys after a choice.");
    }

    [AvaloniaFact]
    public void Up_and_Down_move_through_the_tree_and_Right_and_Left_open_and_close()
    {
        var shell = OpenWaiting(2);
        var project = shell.ProjectName("alpha").FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>()!;
        var row = shell.WorkflowRow("alpha", "Build");
        project.Focus();

        shell.Press(Key.Down);
        Assert.True(row.IsFocused, "Down does not reach the workflow.");

        shell.Press(Key.Down);
        var stage = shell.Window.GetVisualDescendants().OfType<Button>().Single(row => row.Classes.Contains("stage"));
        Assert.True(stage.IsFocused, "Down does not reach the stage.");
        shell.Press(Key.Down);
        shell.Press(Key.Down);
        Assert.Equal("Node 2", shell.Window.FocusManager!.GetFocusedElement() is Button { DataContext: Canvas.TaskNodeViewModel node } ? node.Title : null);

        shell.Press(Key.Left);
        Assert.True(stage.IsFocused, "Left does not move to the parent stage.");
        shell.Press(Key.Left);
        Assert.True(((Canvas.WorkflowStage)stage.DataContext!).IsFolded, "Left does not fold the stage.");
        shell.Press(Key.Right);
        Assert.False(((Canvas.WorkflowStage)stage.DataContext!).IsFolded, "Right does not open the stage.");
        shell.Press(Key.Left);
        shell.Press(Key.Left);
        Assert.True(row.IsFocused, "Left does not move to the parent workflow.");
        shell.Press(Key.Left);
        Assert.False(shell.Window.ViewModel.Canvas!.IsExpanded, "Left does not fold the workflow.");
        shell.Press(Key.Left);
        Assert.True(project.IsFocused, "Left does not move to the parent project.");
        shell.Press(Key.Left);
        Assert.False(shell.Window.ViewModel.Projects[0].IsExpanded, "Left does not fold the project.");
    }

    [AvaloniaFact]
    public void An_Activity_row_names_its_project_and_workflow_while_several_projects_are_open()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var view = shell.Window.ViewModel;
        var other = _temp.Create("beta");
        WorkflowDocument.Create(other, "Docs").Save();
        view.Open(other);
        shell.Render();
        shell.WaitUntil(() => view.Activity.Rows.All(row => row.Place is not null), "the rows name their project");

        view.IsInboxView = true;
        shell.Render();
        var project = view.Projects.Single(item => item.Folder != other).Name;
        Assert.All(view.Activity.Rows, row => Assert.Equal($"{project} › Workflow", row.Place));
        Assert.Contains(Shell.ById<TextBlock>(shell.Window, "ActivityPlace"), text => text.IsEffectivelyVisible && text.Text == $"{project} › Workflow");
    }

    [AvaloniaFact]
    public void Activity_lists_who_waits_before_who_runs_and_a_row_opens_the_conversation_with_the_composer_focused()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var view = shell.Window.ViewModel;

        Assert.Equal(["Write docs · Reply", "Build the API · Running"], view.Activity.Rows.Select(row => $"{row.Node.Title} · {row.Reason}"));
        Assert.All(view.Activity.Rows, row => Assert.EndsWith(" › Workflow", row.Place));
        view.IsInboxView = true;
        shell.Render();
        Assert.True(shell.Find<ItemsControl>("Inbox").IsEffectivelyVisible);
        Assert.Equal("1 of 3 · 1 running · 1 needs you", view.Canvas!.Summary);
        Assert.Equal(1, view.Projects[0].WaitingCount);

        shell.Click(shell.Window.GetVisualDescendants().OfType<Button>().First(row => row.Classes.Contains("activity")));
        shell.Render();

        Assert.Equal("Write docs", view.Conversation?.Node.Title);
        shell.WaitUntil(() => shell.Window.FocusManager!.GetFocusedElement() is TextBox { Name: "Composer" }, "the composer takes the focus");
    }

    [AvaloniaFact]
    public void The_heading_s_counts_add_up_to_the_list()
    {
        using var f = FanOut.Fixture();
        var shell = f.Open();
        FanOut.Run(shell);
        var activity = shell.Window.ViewModel.Activity;

        Assert.Equal(activity.Rows.Count, activity.WaitingCount + activity.FailedCount + activity.RunningCount);
        Assert.Equal([activity.WaitingCount, activity.FailedCount, activity.RunningCount], activity.Groups.Select(group => group.Count));
    }

    [AvaloniaFact]
    public void The_window_s_keys_reach_the_next_waiting_node_on_Chat_and_the_inspector()
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
        shell.Render();
        Assert.True(view.AgentPanel.IsChat);

        shell.Press(Key.M, RawInputModifiers.Alt);
        Assert.Equal("TaskModel", Avalonia.Automation.AutomationProperties.GetAutomationId((Control)shell.Window.FocusManager!.GetFocusedElement()!));

        shell.Press(Key.F, RawInputModifiers.Control);
        Assert.Equal("FindBox", Avalonia.Automation.AutomationProperties.GetAutomationId((Control)shell.Window.FocusManager!.GetFocusedElement()!));

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
                : focused.FindAncestorOfType<AgentPanelView>() is not null ? "inspector" : "other";
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
    public void The_session_keeps_expanded_workflows()
    {
        var file = Path.GetTempFileName();
        try
        {
            var workflow = new WorkflowRef("/work/alpha", WorkflowId.New());
            (WorkspaceSession.Empty with { ExpandedWorkflows = [workflow] }).Write(file);

            Assert.True(WorkspaceSession.Read(file).ExpandedWorkflows.Single().Is(workflow.Folder, workflow.Workflow));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
