using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Desktop.Theme;
using IDevelop.Clients;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The left area's project tree from its 240 px minimum to its widest: project, workflow, and node names wrap in full
/// instead of ending in an ellipsis, and rows keep one height while their names fit, so the list has one rhythm.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class SidebarLayoutTests : IDisposable
{
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    // The names from the ticket, and two that wrap even in the widest sidebar: a sentence, and a folder name with no place
    // to break.
    private const string Reports = "reports-service";
    private const string ReleaseNotes = "Release notes";
    private const string Quarterly = "Quarterly customer billing reconciliation, audit trail, and the release checklist";
    private const string Onboarding = "CustomerOnboardingAnalyticsPipelineForTheBillingAndOperationsTeams";

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;
    private readonly string _gate;

    public SidebarLayoutTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _gate = Path.Combine(_temp.Create("evidence"), "go");
    }

    // A fake client that a failed test left waiting at the gate ends here.
    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _temp.Dispose();
    }

    /// <summary>reports-service with Release notes, of 2 tasks, and Quarterly, of 11, and Onboarding with one workflow.</summary>
    private Shell OpenBoth(ClientDirectory? clients = null)
    {
        var reports = _temp.Create(Reports);
        AppTempFolder.InitGit(reports);
        Save(reports, ReleaseNotes, 2);
        Save(reports, Quarterly, 11);
        var onboarding = _temp.Create(Onboarding);
        Save(onboarding, "Workflow", 1);
        var shell = Shell.Open(reports, clients);
        shell.Window.ViewModel.Open(onboarding);
        shell.Render();
        return shell;
    }

    private static void Save(string folder, string name, int tasks) =>
        Save(folder, name, [.. Enumerable.Range(1, tasks).Select(task => $"Task {task}")]);

    private static void Save(string folder, string name, params string[] titles)
    {
        var document = WorkflowDocument.Create(folder, name);
        foreach (var (title, index) in titles.Select((title, index) => (title, index)))
        {
            Assert.IsType<EditResult.Applied>(document.Apply(TaskAt(TaskId.New(), title, 105 + 300 * index, 90, Codex, "Say hi.")));
        }

        document.Save();
    }

    [AvaloniaTheory]
    [InlineData(240)]
    [InlineData(320)]
    [InlineData(400)]
    public void A_project_s_and_a_workflow_s_name_show_in_full_at_every_sidebar_width(double width)
    {
        var shell = OpenBoth();
        shell.SizeSidebar(width);

        Assert.All([shell.ProjectName(Reports), shell.ProjectName(Onboarding)], name => AssertWhole(shell, name));
        Assert.True(shell.ProjectName(Onboarding).TextLayout.TextLines.Count > 1, $"The project's name fits on one line at {width} px, so nothing here wraps.");
        AssertWhole(shell, shell.WorkflowName(Reports, Quarterly));
    }

    [AvaloniaFact]
    public void The_sidebar_s_splitter_widens_it_and_stops_at_its_240_px_minimum()
    {
        var shell = OpenBoth();
        var splitter = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Single(control => Grid.GetColumn(control) == 1);
        var sidebar = shell.Window.FindControl<Grid>("Columns")!.ColumnDefinitions[0];

        // The panel's tab sits on the splitter's centre.
        var grip = new Vector(0, 200);
        shell.Drag(shell.Center(splitter) + grip, shell.Center(splitter) + grip + new Vector(100, 0));
        Assert.InRange(sidebar.ActualWidth, 371, 373);

        shell.Drag(shell.Center(splitter) + grip, shell.Center(splitter) + grip - new Vector(200, 0));
        Assert.Equal(240, sidebar.ActualWidth);
    }

    [AvaloniaTheory]
    [InlineData(240)]
    [InlineData(400)]
    public void A_project_that_did_not_open_shows_its_whole_name(double width)
    {
        var folder = _temp.Create(Onboarding);
        Save(folder, "Workflow", 1);
        var first = Shell.Open(folder);
        first.Window.Close();
        first.Render();
        Directory.Delete(folder, recursive: true);

        var shell = Shell.Show();
        shell.Window.ViewModel.Restore(null);
        shell.SizeSidebar(width);
        var name = shell.Find<TextBlock>("UnopenedName");
        var forget = shell.Find<Button>("ForgetProject");

        AssertWhole(shell, name);
        Assert.True(name.TextLayout.TextLines.Count > 1, $"\"{name.Text}\" fits on one line at {width} px, so nothing here wraps.");
        Assert.Equal("Forget ends at 0, centred 0 from the name's first line",
            $"Forget ends at {FromEdge(shell, forget):0.#}, centred {Math.Abs(Math.Round(Centre(forget) - FirstLine(name))):0.#} from the name's first line");
    }

    private const string LongTask = "Draft the release notes from every pull request merged since the last tag, grouped by area";

    [AvaloniaTheory]
    [InlineData(240)]
    [InlineData(400)]
    public void Task_titles_show_in_full_with_their_tile_on_the_first_line(double width)
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, LongTask, "Ship");
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.SizeSidebar(width);
        var rows = new[] { LongTask, "Ship" }.Select(title => shell.TaskRow(Reports, ReleaseNotes, title)).ToArray();
        var titles = rows.Select(row => row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("nodeName"))).ToArray();

        Assert.Equal(LongTask, titles[0].Text);
        Assert.True(titles[0].TextLayout.TextLines.Count > 1, $"The long title fits on one line at {width} px, so nothing here wraps.");
        Assert.Single(titles[1].TextLayout.TextLines);
        // Each row centres its glyph on its title's first line, 12 px into the row.
        Assert.All(rows, row => Assert.StartsWith("first line 12, glyph 0", TaskCentres(row)));
        Assert.Equal(24, rows[1].Bounds.Height);
    }

    private static string TaskCentres(ListBoxItem row)
    {
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("nodeName"));
        var line = FirstLine(title);
        return $"first line {line - Top(row):0.#}, glyph {Centre(row.GetVisualDescendants().OfType<KindTile>().First()) - line:0.#}";
    }

    [AvaloniaFact]
    public void An_agent_s_name_and_summary_wrap_in_full_with_its_dot_on_the_name_s_first_line()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var shell = Shell.Show(_fakes.DiscoverAsync().Result);
        var agents = Shell.ById<ItemsControl>(shell.AgentsPanel(), "Agents").Single();
        // Every row fits the flyout today, so the list narrows to 90 px, where "Antigravity CLI" and "Ready · 3 models" need
        // two lines, as a larger system font would leave them.
        Assert.Equal(4, AgentRows(agents).Length);
        Assert.All(AgentRows(agents), row => Assert.Equal("first line 9, dot 0", AgentCentres(row)));

        agents.Width = 90;
        agents.UpdateLayout();

        Assert.All(AgentRows(agents), row => Assert.All(row.GetVisualDescendants().OfType<TextBlock>(), AssertWhole));
        Assert.All(AgentRows(agents), row => Assert.Equal("first line 9, dot 0", AgentCentres(row)));
        Assert.True(AgentTexts(agents, "Antigravity CLI").Name.TextLayout.TextLines.Count > 1, "\"Antigravity CLI\" does not wrap.");
        Assert.True(AgentTexts(agents, "Codex").Summary.TextLayout.TextLines.Count > 1, "Codex's \"Ready · 3 models\" does not wrap.");
    }

    // Each agent's row: its dot, its name, and under the name its summary.
    private static Grid[] AgentRows(ItemsControl agents) => [.. agents.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Children.OfType<Avalonia.Controls.Shapes.Ellipse>().Any())];

    private static (TextBlock Name, TextBlock Summary) AgentTexts(ItemsControl agents, string name)
    {
        var texts = AgentRows(agents).Select(row => row.Children.OfType<TextBlock>().ToArray()).Single(texts => texts[0].Text == name);
        return (texts[0], texts[1]);
    }

    private static string AgentCentres(Grid row)
    {
        var name = row.Children.OfType<TextBlock>().First();
        var line = FirstLine(name);
        return $"first line {line - Top(row):0.#}, dot {Centre(row.Children.OfType<Avalonia.Controls.Shapes.Ellipse>().Single()) - line:0.#}";
    }

    /// <summary>The client waits at the gate until it is stopped or the test opens the gate.</summary>
    private FakeRule Waits() => On("exec", "--json")
        .Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""")
        .WaitForFile(_gate);

    [AvaloniaTheory]
    [InlineData(240)]
    [InlineData(400)]
    public void Rows_keep_one_height_while_their_names_fit_and_state_shows_as_counts_under_the_name(double width)
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var shell = OpenBoth(_fakes.DiscoverAsync().Result);
        shell.SizeSidebar(width);
        var row = shell.WorkflowRow(Reports, ReleaseNotes);
        Assert.Equal((28.0, 28.0), (shell.ProjectName(Reports).FindAncestorOfType<ToggleButton>()!.Bounds.Height, row.Bounds.Height));
        Assert.Equal("0/2", shell.WorkflowText(Reports, ReleaseNotes, "WorkflowSummary"));

        shell.Click(row);
        shell.RunNode("Task 1");
        shell.AddNode();
        shell.Render();

        // The node rows of an open workflow say its states one by one, so only a folded row says them.
        Assert.False(Of<Control>(row.GetVisualParent()!, "WorkflowAlert").IsEffectivelyVisible);
        ((Canvas.WorkflowCanvasViewModel)row.DataContext!).IsExpanded = false;
        shell.Render();
        Assert.True(row.Bounds.Height > 28, "The state takes no line of its own.");
        Assert.True(Of<Control>(row.GetVisualParent()!, "WorkflowAlert").IsEffectivelyVisible);
        Assert.True(Of<Control>(row.GetVisualParent()!, "WorkflowUnsaved").IsEffectivelyVisible);
        // The progress keeps the row's end in every state, and the state is a badge and a count under the name, with its words
        // in the chip's tooltip and name.
        Assert.True(Of<Control>(row.GetVisualParent()!, "WorkflowSummary").IsEffectivelyVisible);
        var canvas = (Canvas.WorkflowCanvasViewModel)row.DataContext!;
        var alert = Of<Control>(row.GetVisualParent()!, "WorkflowAlert");
        Assert.Equal(canvas.RowChips.Select(chip => chip.Count.ToString()), alert.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text!));
        Assert.Equal(canvas.RowChips.Select(chip => chip.Tip), alert.GetVisualDescendants().OfType<StackPanel>().Select(chip => (string)ToolTip.GetTip(chip)!));
    }

    [Theory]
    [InlineData("Backfill 2023-Q4", "Backfill 2023-\u2060Q4")]
    [InlineData("Plain name", "Plain name")]
    public void A_hyphenated_token_stays_on_one_line(string name, string shown) => Assert.Equal(shown, Theme.NameBreaks.Keep(name));

    [AvaloniaFact]
    public void The_shown_workflow_lists_its_nodes_however_long_the_list_is()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 50).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);

        Assert.True(shell.WorkflowExpand(Reports, ReleaseNotes).IsChecked);
    }

    [AvaloniaFact]
    public void A_shown_workflow_folds_the_others_and_the_tree_shows_it_from_its_row_down()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 30).Select(index => $"Node {index}")]);
        Save(reports, "Second", [.. Enumerable.Range(1, 3).Select(index => $"Step {index}")]);
        var shell = Shell.Open(reports);
        var view = shell.Window.ViewModel;
        view.Select(view.Projects[0].Workflows.Single(canvas => canvas.Name == ReleaseNotes));
        shell.Render();
        var second = view.Projects[0].Workflows.Single(canvas => canvas.Name == "Second");

        view.Select(second);
        shell.Render();

        Assert.Equal([second], view.Projects[0].Workflows.Where(canvas => canvas.IsExpanded));
        var tree = shell.Window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Tree");
        var top = shell.WorkflowRow(Reports, "Second").TranslatePoint(default, tree)!.Value.Y;
        Assert.InRange(top, 0, tree.Viewport.Height - 28);
    }

    [AvaloniaFact]
    public void Showing_a_node_opens_its_folded_workflow_and_brings_its_row_into_the_tree()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.Window.Height = 600;
        var view = shell.Window.ViewModel;
        view.Canvas!.IsExpanded = false;
        shell.Render();

        view.ShowNode(view.Canvas.Outline[^1]);
        shell.Render();

        Assert.True(view.Canvas.IsExpanded);
        var tree = shell.Window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Tree");
        var row = shell.TaskRow(Reports, ReleaseNotes, "Node 40");
        var top = row.TranslatePoint(default, tree)!.Value.Y;
        Assert.InRange(top, 0, tree.Viewport.Height - row.Bounds.Height);
    }

    [AvaloniaFact]
    public void A_node_s_name_shows_in_full_also_while_it_is_selected()
    {
        const string Long = "Backfill the ledger partition for every tenant that opened an account before the migration began and keep it online";
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, Long, "Short");
        var shell = Shell.Open(reports);
        shell.Render();
        var row = shell.TaskRow(Reports, ReleaseNotes, Long);
        var name = row.GetVisualDescendants().OfType<Theme.EllipsisText>().Single();
        var view = shell.Window.ViewModel;
        var node = view.Canvas!.Outline.Single(item => item.Title == Long);

        // Issue #108: every name in the left tree shows in full and wraps.
        Assert.Equal(Long, name.Text);
        Assert.True(name.TextLayout.TextLines.Count > 2, "The long name does not wrap past two lines.");

        view.Canvas.Inspect(node);
        shell.Render();

        Assert.Equal(Long, name.Text);
        Assert.True(name.TextLayout.TextLines.Count > 2, "The selected name does not wrap past two lines.");
    }

    [AvaloniaFact]
    public void A_node_s_name_reads_one_size_under_its_workflow_s()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, "Ship");
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Render();

        var workflow = shell.WorkflowRow(Reports, ReleaseNotes).GetVisualDescendants().OfType<TextBlock>().First();
        var node = shell.TaskRow(Reports, ReleaseNotes, "Ship").GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("nodeName"));
        Assert.True(node.FontSize < workflow.FontSize, $"A node's name is {node.FontSize} and its workflow's is {workflow.FontSize}.");
    }

    [AvaloniaFact]
    public void Opening_a_nodes_conversation_keeps_its_large_tree_group_folded_and_lists_its_row()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 12).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Render();
        var canvas = shell.Window.ViewModel.Canvas!;
        var group = canvas.Groups.Single(group => group.Kind == Canvas.NodeGroupKind.NotStarted);

        canvas.Outline[5].OpenConversationCommand.Execute(null);
        shell.Render();

        Assert.True(group.IsFolded);
        Assert.Equal<object>([group, canvas.Outline[5], group.More], canvas.TreeRows);
        Assert.Equal("+11 more", group.More.Label);
    }

    [AvaloniaFact]
    public void Selecting_a_node_of_a_large_folded_group_lists_only_its_row_selected_and_more_opens_the_group()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 12).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Render();
        var canvas = shell.Window.ViewModel.Canvas!;
        var group = canvas.Groups.Single(group => group.Kind == Canvas.NodeGroupKind.NotStarted);
        var list = shell.Window.GetVisualDescendants().OfType<ListBox>().Single(list => list.DataContext == canvas && list.Classes.Contains("tasks"));
        Assert.True(group.IsFolded);

        canvas.SelectedNode = canvas.Outline[7];
        shell.Render();
        Assert.True(group.IsFolded);
        Assert.Equal<object>([group, canvas.Outline[7], group.More], canvas.TreeRows);
        Assert.Same(canvas.Outline[7], list.SelectedItem);

        // Another selection takes the one row's place.
        canvas.SelectedNode = canvas.Outline[3];
        shell.Render();
        Assert.Equal<object>([group, canvas.Outline[3], group.More], canvas.TreeRows);
        Assert.Same(canvas.Outline[3], list.SelectedItem);

        // The "+N more" row opens the group, and the selection stays.
        var more = list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext == group.More);
        Assert.Equal("NodeGroupMore", Avalonia.Automation.AutomationProperties.GetAutomationId(more));
        group.More.Group.Toggle();
        shell.Render();
        Assert.False(group.IsFolded);
        Assert.Equal(13, canvas.TreeRows.Count);
        Assert.Same(canvas.Outline[3], list.SelectedItem);
        list.SelectedItem = canvas.Outline[2];
        shell.Render();
        Assert.Same(canvas.Outline[2], canvas.SelectedNode);
    }

    [AvaloniaFact]
    public void No_row_shows_cut_at_the_tree_s_bottom_edge_and_the_rows_workflow_stays_pinned()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Window.ViewModel.Canvas!.Groups.Single(group => group.Kind == Canvas.NodeGroupKind.NotStarted).Open();
        // The tree lists every row of the large workflow, so it scrolls.
        shell.Window.Width = 900;
        shell.Window.Height = 600;
        shell.Render();
        T Named<T>(string name) where T : Control => shell.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        var tree = Named<ScrollViewer>("Tree");
        var (foot, pinned) = (Named<Border>("TreeFootCover"), Named<Panel>("StickyWorkflow"));

        foreach (var offset in new[] { 0.0, 53, 171, 182, 299, 310, 433 })
        {
            tree.Offset = tree.Offset.WithY(offset);
            shell.Render();
            var height = tree.Viewport.Height;
            foreach (var row in tree.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible))
            {
                var at = row.TranslatePoint(default, tree)!.Value.Y;
                var bottom = at + row.Bounds.Height;
                Assert.True(!(at < height - 0.5 && bottom > height + 0.5) || (foot.IsVisible && height - foot.Height <= at + 0.5), $"The row at {at} is cut at {height}.");
            }
        }

        Assert.True(pinned.IsVisible);
        Assert.Equal(ReleaseNotes, ((Canvas.WorkflowCanvasViewModel)pinned.DataContext!).Name);
    }

    [AvaloniaFact]
    public void The_tree_rests_on_a_whole_row_under_its_pinned_rows()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Window.ViewModel.Canvas!.Groups.Single(group => group.Kind == Canvas.NodeGroupKind.NotStarted).Open();
        // The tree lists every row of the large workflow, so it scrolls.
        shell.Window.Width = 900;
        shell.Window.Height = 600;
        shell.Render();
        T Named<T>(string name) where T : Control => shell.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        var (tree, head, cover) = (Named<ScrollViewer>("Tree"), Named<Border>("TreeHead"), Named<Border>("TreeHeadCover"));
        var end = tree.Extent.Height - tree.Viewport.Height;

        foreach (var offset in new[] { 53.0, 171, 182, 299, 310, 433, 120, 60, end, end - 7, end })
        {
            tree.Offset = tree.Offset.WithY(offset);
            shell.Render();
            var edge = head.IsVisible ? head.Bounds.Bottom : 0;
            foreach (var row in tree.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible))
            {
                var at = row.TranslatePoint(default, tree)!.Value.Y;
                var bottom = at + row.Bounds.Height;
                Assert.True(!(at < edge - 0.5 && bottom > edge + 0.5) || (cover.IsVisible && edge + cover.Height >= bottom - 0.5), $"The row at {at} is cut at {edge} with the tree at {tree.Offset.Y}.");
            }
        }
    }

    [AvaloniaFact]
    public void A_project_s_buttons_show_on_hover_and_end_at_the_tree_s_edge()
    {
        var shell = OpenBoth();
        var item = shell.ProjectItem(Reports);
        var (close, add) = (Of<Button>(item, "CloseProject"), Of<Button>(item, "NewWorkflow"));
        var actions = close.GetVisualParent<StackPanel>()!;
        Assert.Equal(0, actions.Opacity);

        shell.Hover(shell.Center(shell.ProjectName(Reports)));

        Assert.Equal(1, actions.Opacity);
        Assert.Equal("× 8, + 32", $"× {FromEdge(shell, close):0.#}, + {FromEdge(shell, add):0.#}");
    }

    /// <summary>The name shows whole and ends before the tree's content edge.</summary>
    private static void AssertWhole(Shell shell, TextBlock name)
    {
        AssertWhole(name);
        Assert.True(FromEdge(shell, name) >= 0, $"\"{name.Text}\" runs {-FromEdge(shell, name):0.#} px past the content edge.");
    }

    /// <summary>The text shows whole: no line ends in an ellipsis, and its lines fit inside the box that shows them.</summary>
    private static void AssertWhole(TextBlock text)
    {
        var layout = text.TextLayout;
        var content = text.Bounds.Size.Deflate(text.Padding);
        Assert.False(layout.TextLines.Any(line => line.HasCollapsed), $"\"{text.Text}\" ends in an ellipsis.");
        Assert.True(layout.Width <= content.Width + 0.5 && layout.Height <= content.Height + 0.5,
            $"\"{text.Text}\" needs {layout.Width:0.#}×{layout.Height:0.#} px and shows in {content.Width:0.#}×{content.Height:0.#}.");
    }

    /// <summary>
    /// How far before the tree's content edge the visual's right edge is. The edge is where Open Folder ends, or, while the
    /// tree overflows, where its scroll bar starts.
    /// </summary>
    private static double FromEdge(Shell shell, Visual visual)
    {
        var rows = shell.Window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "TreeRows");
        return Left(rows, shell.Window) + rows.Bounds.Width - Left(visual, shell.Window) - visual.Bounds.Width;
    }

    private static double Left(Visual visual, Visual to) => visual.TranslatePoint(default, to)!.Value.X;

    private static double Top(Visual visual) => visual.TranslatePoint(default, (Visual)visual.GetVisualRoot()!)!.Value.Y;

    private static double Centre(Visual visual) => Top(visual) + visual.Bounds.Height / 2;

    /// <summary>The middle of the text's first line.</summary>
    private static double FirstLine(TextBlock text) => Top(text) + text.Padding.Top + text.TextLayout.TextLines[0].Height / 2;

    private static T Of<T>(Visual within, string automationId) where T : Control => Shell.ById<T>(within, automationId).Single();
}
