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
        // Each row centres its glyph on its title's first line, 13 px into the row.
        Assert.All(rows, row => Assert.StartsWith("first line 13, glyph 0", TaskCentres(row)));
        Assert.Equal(26, rows[1].Bounds.Height);
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
    public void Rows_keep_one_height_while_their_names_fit_and_the_status_line_says_every_group_under_the_name(double width)
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var shell = OpenBoth(_fakes.DiscoverAsync().Result);
        shell.SizeSidebar(width);
        var row = shell.WorkflowRow(Reports, ReleaseNotes);
        Assert.Equal((26.0, 26.0), (shell.ProjectName(Reports).FindAncestorOfType<ToggleButton>()!.Bounds.Height, row.Bounds.Height));
        Assert.Equal("0/2", shell.WorkflowText(Reports, ReleaseNotes, "WorkflowSummary"));

        shell.Click(row);
        shell.RunNode("Task 1");
        shell.AddNode();
        shell.Render();

        // The progress keeps the end of the name's line, and the status line under the name says every group in the run bar's words.
        var nameTop = Of<Control>(row.GetVisualParent()!, "WorkflowName").TranslatePoint(default, row)!.Value.Y;
        var alert = Of<Control>(row.GetVisualParent()!, "WorkflowAlert");
        Assert.True(alert.TranslatePoint(default, row)!.Value.Y > nameTop + 10, "The status line stands under the name.");
        Assert.True(alert.IsEffectivelyVisible);
        Assert.True(Of<Control>(row.GetVisualParent()!, "WorkflowUnsaved").IsEffectivelyVisible);
        Assert.True(Of<Control>(row.GetVisualParent()!, "WorkflowSummary").IsEffectivelyVisible);
        var canvas = (Canvas.WorkflowCanvasViewModel)row.DataContext!;
        Assert.Equal(StateCounts.KeepCountsWhole(string.Join(" \u00b7 ", canvas.BarChips.Select(chip => chip.Label))), Assert.Single(canvas.RowChips).Label);
        Assert.Equal(canvas.RowChips.Select(chip => chip.Label), alert.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text!));
        Assert.Equal(canvas.RowChips.Select(chip => chip.Tip), alert.GetVisualDescendants().OfType<DockPanel>().Where(chip => chip.DataContext is Canvas.StateChip).Select(chip => (string)ToolTip.GetTip(chip)!));
        Assert.Single(canvas.RowChips);
    }

    [Theory]
    [InlineData("Backfill 2023-Q4", "Backfill 2023-\u2060Q4")]
    [InlineData("Plain name", "Plain name")]
    public void A_hyphenated_token_stays_on_one_line(string name, string shown) => Assert.Equal(shown, Theme.NameBreaks.Keep(name));

    [AvaloniaFact]
    public void The_shown_workflow_lists_every_node_however_long_the_list_is()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 50).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        var canvas = shell.Window.ViewModel.Canvas!;

        Assert.True(shell.ListsNodesOf(Reports, ReleaseNotes));
        Assert.Equal(51, canvas.TreeRows.Count);
    }

    [AvaloniaFact]
    public void The_Nodes_section_lists_the_shown_workflow_and_Projects_shows_its_row()
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

        Assert.True(shell.ListsNodesOf(Reports, "Second"));
        var tree = shell.Window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Tree");
        var top = shell.WorkflowRow(Reports, "Second").TranslatePoint(default, tree)!.Value.Y;
        Assert.InRange(top, 0, tree.Viewport.Height - 28);
    }

    [AvaloniaFact]
    public void Showing_a_node_brings_its_row_into_the_Nodes_section()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.Window.Height = 600;
        var view = shell.Window.ViewModel;
        shell.Render();

        view.ShowNode(view.Canvas!.Outline[^1]);
        shell.Render();

        var list = shell.Find<ListBox>("SidebarTasks");
        var row = shell.TaskRow(Reports, ReleaseNotes, "Node 40");
        var top = row.TranslatePoint(default, list)!.Value.Y;
        Assert.InRange(top, 0, list.Bounds.Height - row.Bounds.Height);
    }

    [AvaloniaFact]
    public void No_node_row_shows_cut_at_the_Nodes_section_s_bottom_edge()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.Window.Height = 600;
        shell.Render();
        var list = shell.Find<ListBox>("SidebarTasks");
        var cover = shell.Window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "NodesFootCover");
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();

        foreach (var offset in new[] { 0.0, 13, 50, 97, 211 })
        {
            scroll.Offset = scroll.Offset.WithY(offset);
            shell.Render();
            var height = list.Bounds.Height;
            foreach (var row in list.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible))
            {
                var at = row.TranslatePoint(default, list)!.Value.Y;
                var cut = at < height - 0.5 && at + row.Bounds.Height > height + 0.5;
                Assert.True(!cut || (cover.IsVisible && height - cover.Height <= at + 0.5), $"The row at {at} is cut at {height}.");
            }
        }
    }

    [AvaloniaFact]
    public void Projects_takes_at_most_half_of_the_room_and_Nodes_takes_the_rest()
    {
        var reports = _temp.Create(Reports);
        foreach (var index in Enumerable.Range(1, 30))
        {
            Save(reports, $"Workflow {index}", "Step");
        }

        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Window.Height = 800;
        shell.Render();
        T Named<T>(string name) where T : Control => shell.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        var (tree, sections, list) = (Named<ScrollViewer>("Tree"), Named<Grid>("Sections"), shell.Find<ListBox>("SidebarTasks"));

        Assert.True(tree.Extent.Height > tree.Viewport.Height, "Projects does not scroll.");
        Assert.InRange(tree.Bounds.Height, sections.Bounds.Height / 2 - 40, sections.Bounds.Height / 2 + 6);
        Assert.True(list.Bounds.Height > sections.Bounds.Height / 2 - 60, $"Nodes has {list.Bounds.Height} px.");
        Assert.True(list.Bounds.Bottom <= sections.Bounds.Height + 0.5, "Nodes goes past the panel.");
    }

    [AvaloniaTheory]
    [InlineData(700)]
    [InlineData(733)]
    [InlineData(800)]
    [InlineData(847)]
    public void Projects_ends_on_a_whole_row_and_every_workflow_is_in_reach(double height)
    {
        var reports = _temp.Create(Reports);
        foreach (var index in Enumerable.Range(1, 30))
        {
            Save(reports, $"Workflow {index}", "Step");
        }

        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Window.Height = height;
        shell.Render();
        T Named<T>(string name) where T : Control => shell.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        var (tree, foot) = (Named<ScrollViewer>("Tree"), Named<Border>("TreeFootCover"));

        Assert.True(tree.Extent.Height > tree.Viewport.Height, "Projects does not scroll.");
        Assert.False(foot.IsVisible && foot.Height > 8, $"A blank band of {foot.Height} px ends Projects at the top of its range.");

        tree.Offset = tree.Offset.WithY(tree.Extent.Height);
        shell.Render();
        var last = tree.GetVisualDescendants().OfType<Button>().Where(row => row.Classes.Contains("workflow") && row.IsEffectivelyVisible).Last();
        var at = last.TranslatePoint(default, tree)!.Value.Y;
        Assert.InRange(at + last.Bounds.Height, 0, tree.Viewport.Height + 0.5);
    }

    [AvaloniaFact]
    public void Nodes_rests_on_a_whole_row_so_no_blank_band_shows_under_its_heading()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Window.Height = 700;
        shell.Render();
        var list = shell.Find<ListBox>("SidebarTasks");
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var head = shell.Window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "NodesHeadCover");

        foreach (var offset in new[] { 13.0, 50, 97, 211, 90, 40, scroll.Extent.Height })
        {
            scroll.Offset = scroll.Offset.WithY(offset);
            shell.Render();
            Assert.False(head.IsVisible, $"A cover hides a row under the heading with the list at {scroll.Offset.Y} of {scroll.Extent.Height}.");
        }
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
    public void A_node_s_name_has_its_workflow_s_size()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, "Ship");
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Render();

        var workflow = shell.WorkflowRow(Reports, ReleaseNotes).GetVisualDescendants().OfType<TextBlock>().First();
        var node = shell.TaskRow(Reports, ReleaseNotes, "Ship").GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("nodeName"));
        Assert.Equal(workflow.FontSize, node.FontSize);
    }

    [AvaloniaFact]
    public void Opening_a_nodes_conversation_opens_its_folded_tree_group_and_lists_every_row()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 12).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Render();
        var canvas = shell.Window.ViewModel.Canvas!;
        var group = canvas.Groups.Single(group => group.Kind == Canvas.NodeGroupKind.NotStarted);
        group.Toggle();
        shell.Render();
        Assert.True(group.IsFolded);

        canvas.Outline[5].OpenConversationCommand.Execute(null);
        shell.Render();

        Assert.False(group.IsFolded);
        Assert.Equal<object>([group, .. canvas.Outline], canvas.TreeRows);
    }

    [AvaloniaFact]
    public void Every_group_starts_open_and_selecting_a_node_of_a_folded_group_opens_it()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 12).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Render();
        var canvas = shell.Window.ViewModel.Canvas!;
        var group = canvas.Groups.Single(group => group.Kind == Canvas.NodeGroupKind.NotStarted);
        var list = shell.Window.GetVisualDescendants().OfType<ListBox>().Single(list => list.DataContext == canvas && list.Classes.Contains("tasks"));
        bool PointsRight() => list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext == group)
            .GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Classes.Contains("groupChevron")).Classes.Contains("folded");
        Assert.False(group.IsFolded, "A large group starts folded.");
        group.Toggle();
        shell.Render();
        Assert.True(PointsRight(), "A folded group that lists no row points its chevron right.");

        canvas.SelectedNode = canvas.Outline[7];
        shell.Render();
        Assert.False(group.IsFolded);
        Assert.Equal(13, canvas.TreeRows.Count);
        Assert.False(PointsRight(), "An open group points its chevron down.");
        Assert.Same(canvas.Outline[7], list.SelectedItem);

        // The header folds the whole group, and the canvas keeps its selection.
        group.Toggle();
        shell.Render();
        Assert.Equal<object>([group], canvas.TreeRows);
        Assert.True(PointsRight(), "A folded group points its chevron right.");
        Assert.Same(canvas.Outline[7], canvas.SelectedNode);

        // Another selection opens the group again.
        canvas.SelectedNode = canvas.Outline[3];
        shell.Render();
        Assert.False(group.IsFolded);
        Assert.Same(canvas.Outline[3], list.SelectedItem);
        list.SelectedItem = canvas.Outline[2];
        shell.Render();
        Assert.Same(canvas.Outline[2], canvas.SelectedNode);
    }

    [AvaloniaFact]
    public void No_row_shows_cut_at_the_tree_s_bottom_edge_and_the_rows_project_stays_pinned()
    {
        var reports = _temp.Create(Reports);
        foreach (var index in Enumerable.Range(1, 40))
        {
            Save(reports, $"Workflow {index}", "Step");
        }

        var shell = Shell.Open(reports);
        // Projects lists forty workflows, so it scrolls.
        shell.Window.Width = 900;
        shell.Window.Height = 600;
        shell.Render();
        T Named<T>(string name) where T : Control => shell.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        var tree = Named<ScrollViewer>("Tree");
        var (foot, pinned) = (Named<Border>("TreeFootCover"), Named<Panel>("StickyProject"));

        foreach (var offset in new[] { 0.0, 53, 171, 182, 299, 310, 433 })
        {
            tree.Offset = tree.Offset.WithY(offset);
            shell.Render();
            var height = tree.Viewport.Height;
            foreach (var row in tree.GetVisualDescendants().OfType<Button>().Where(row => row.Classes.Contains("workflow")).Where(row => row.IsEffectivelyVisible))
            {
                var at = row.TranslatePoint(default, tree)!.Value.Y;
                var bottom = at + row.Bounds.Height;
                Assert.True(!(at < height - 0.5 && bottom > height + 0.5) || (foot.IsVisible && height - foot.Height <= at + 0.5), $"The row at {at} is cut at {height}.");
            }
        }

        Assert.True(pinned.IsVisible);
        Assert.Equal(Reports, ((ProjectViewModel)pinned.DataContext!).Name);
    }

    [AvaloniaFact]
    public void The_group_of_the_rows_under_the_top_edge_stays_pinned_and_the_rows_rest_under_it()
    {
        var reports = _temp.Create(Reports);
        // Each title wraps to several lines, so eight rows scroll.
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 8).Select(task => $"Task {task} {string.Concat(Enumerable.Repeat("with a long name ", 8))}")]);
        var shell = Shell.Open(reports);
        shell.Window.Width = 900;
        shell.Window.Height = 600;
        shell.Render();
        T Named<T>(string name) where T : Control => shell.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        var (list, head, sticky) = (Named<ListBox>("NodeRows"), Named<Border>("NodesHead"), Named<Button>("StickyGroup"));
        Assert.False(head.IsVisible);

        var scroller = (ScrollViewer)list.Scroll!;
        scroller.Offset = scroller.Offset.WithY(60);
        shell.Render();

        Assert.True(head.IsVisible);
        Assert.Equal("Not started", ((Canvas.NodeGroup)sticky.DataContext!).Name);
        var tops = list.GetVisualDescendants().OfType<ListBoxItem>().Select(row => row.TranslatePoint(default, list)!.Value.Y);
        Assert.Contains(tops, top => Math.Abs(top - 26) < 1);
    }

    // The states of the review fixture's scenes, in the order that the nodes are saved.
    [AvaloniaTheory]
    [InlineData("normal", new[] { Canvas.NodeState.Completed, Canvas.NodeState.Completed, Canvas.NodeState.Idle, Canvas.NodeState.Idle, Canvas.NodeState.Idle, Canvas.NodeState.Idle, Canvas.NodeState.Idle, Canvas.NodeState.Idle })]
    [InlineData("running", new[] { Canvas.NodeState.InputRequired, Canvas.NodeState.InputRequired, Canvas.NodeState.Failed, Canvas.NodeState.InputRequired, Canvas.NodeState.Running, Canvas.NodeState.Stale, Canvas.NodeState.Idle, Canvas.NodeState.Completed, Canvas.NodeState.Completed })]
    [InlineData("needs attention", new[] { Canvas.NodeState.InputRequired, Canvas.NodeState.InputRequired, Canvas.NodeState.Failed, Canvas.NodeState.InputRequired, Canvas.NodeState.InputRequired, Canvas.NodeState.Stale, Canvas.NodeState.Idle, Canvas.NodeState.Completed, Canvas.NodeState.Completed })]
    [InlineData("failed", new[] { Canvas.NodeState.Failed, Canvas.NodeState.Failed, Canvas.NodeState.Idle, Canvas.NodeState.Idle, Canvas.NodeState.Idle, Canvas.NodeState.Idle, Canvas.NodeState.Completed, Canvas.NodeState.Completed })]
    public void An_open_group_lists_every_node_and_a_pinned_group_keeps_a_row_under_it(string scene, Canvas.NodeState[] states)
    {
        // Three times each scene's nodes, so the list scrolls in the small window.
        states = [.. states, .. states, .. states];
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. states.Select((_, index) => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.OpenNodes(Reports, ReleaseNotes);
        shell.Window.Width = 900;
        shell.Window.Height = 600;
        var canvas = shell.Window.ViewModel.Canvas!;
        foreach (var (node, state) in canvas.Outline.Zip(states))
        {
            typeof(Canvas.TaskNodeViewModel).GetProperty(nameof(Canvas.TaskNodeViewModel.State))!.SetValue(node, state);
        }

        typeof(Canvas.WorkflowCanvasViewModel).GetMethod("ArrangeTreeRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(canvas, [null]);
        shell.Render();
        T Named<T>(string name) where T : Control => shell.Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        var (list, head, sticky) = (Named<ListBox>("NodeRows"), Named<Border>("NodesHead"), Named<Button>("StickyGroup"));
        var scroller = (ScrollViewer)list.Scroll!;
        List<ListBoxItem> Rows() => [.. list.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible).OrderBy(row => row.TranslatePoint(default, list)!.Value.Y)];

        foreach (var group in canvas.Groups.Where(group => group.Count > 0))
        {
            var rows = Rows().SkipWhile(row => row.DataContext != group).Skip(1).TakeWhile(row => row.DataContext is not Canvas.NodeGroup);
            Assert.Equal(group.Count, rows.Count());
        }

        // Each offset and each way down and up: an open pinned group never stands over the next group's header.
        var end = scroller.Extent.Height - scroller.Viewport.Height;
        Assert.True(end > 0, $"The {scene} list does not scroll.");
        var offsets = Enumerable.Range(0, (int)(end / 7) + 2).Select(step => step * 7.0).ToList();
        foreach (var offset in offsets.Concat(Enumerable.Reverse(offsets)))
        {
            scroller.Offset = scroller.Offset.WithY(offset);
            shell.Render();
            if (head.IsVisible && sticky.DataContext is Canvas.NodeGroup { ShowsFolded: false } pinned)
            {
                var first = Rows().First(row => row.TranslatePoint(default, list)!.Value.Y + row.Bounds.Height > head.Bounds.Height + 0.5);
                Assert.True(first.DataContext is Canvas.TaskNodeViewModel node && Canvas.NodeGroup.Of(node) == pinned.Kind,
                    $"{scene}: {pinned.Name} is pinned with no row under it at offset {scroller.Offset.Y}: the first row is {first.DataContext}.");
            }
        }
    }

    [AvaloniaFact]
    public void A_wrapped_status_line_never_ends_in_a_separator()
    {
        var line = new Theme.PartsLine { Whole = "1\u00a0failed · 2\u00a0waiting · 2\u00a0interrupted · 1\u00a0stale", TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        line.Measure(new Size(120, double.PositiveInfinity));

        Assert.Contains('\n', line.Text!);
        Assert.All(line.Text!.Split('\n'), row => Assert.False(row.EndsWith('·') || row.EndsWith(' ') || row.StartsWith('·'), $"The line \"{row}\" holds a loose separator."));
        Assert.Equal(line.Whole, line.Text.Replace("\n", " · "));

        line.Measure(new Size(1000, double.PositiveInfinity));
        Assert.Equal(line.Whole, line.Text);
    }

    [AvaloniaFact]
    public void The_tree_rests_on_a_whole_row_under_its_pinned_rows()
    {
        var reports = _temp.Create(Reports);
        foreach (var index in Enumerable.Range(1, 40))
        {
            Save(reports, $"Workflow {index}", "Step");
        }

        var shell = Shell.Open(reports);
        // Projects lists forty workflows, so it scrolls.
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
            foreach (var row in tree.GetVisualDescendants().OfType<Button>().Where(row => row.Classes.Contains("workflow")).Where(row => row.IsEffectivelyVisible))
            {
                var at = row.TranslatePoint(default, tree)!.Value.Y;
                var bottom = at + row.Bounds.Height;
                Assert.True(!(at < edge - 0.5 && bottom > edge + 0.5) || (cover.IsVisible && edge + cover.Height >= bottom - 0.5), $"The row at {at} is cut at {edge} with the tree at {tree.Offset.Y}.");
            }
        }

        // The end of the range also rests on a row, so no cut row hides as an empty band under the pinned rows.
        tree.Offset = tree.Offset.WithY(tree.Extent.Height - tree.Viewport.Height);
        shell.Render();
        Assert.False(cover.IsVisible, $"A row hides under the pinned rows with the tree at {tree.Offset.Y} of {tree.Extent.Height}.");
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
