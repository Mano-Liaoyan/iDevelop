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
        Assert.InRange(sidebar.ActualWidth, 351, 353);

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
        // Each row centres its glyph on its title's first line, 14 px into the row.
        Assert.All(rows, row => Assert.StartsWith("first line 14, glyph 0", TaskCentres(row)));
        Assert.Equal(28, rows[1].Bounds.Height);
    }

    private static string TaskCentres(Button row)
    {
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("nodeName"));
        var line = FirstLine(title);
        return $"first line {line - Top(row):0.#}, glyph {Centre(row.GetVisualDescendants().OfType<KindTile>().First()) - line:0.#}";
    }

    [AvaloniaFact]
    public void A_client_that_needs_setup_shows_its_name_and_summary_in_full_with_its_dot_on_the_name_s_first_line()
    {
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        var shell = Shell.Show(_fakes.DiscoverAsync().Result);
        var agents = shell.Find<ItemsControl>("AgentsNeedingSetup");
        Assert.Single(AgentRows(agents));

        agents.Width = 72;
        agents.UpdateLayout();

        Assert.All(AgentRows(agents), row => Assert.All(row.GetVisualDescendants().OfType<TextBlock>(), AssertWhole));
        Assert.All(AgentRows(agents), row => Assert.Equal("first line 9, dot 0", AgentCentres(row)));
        Assert.True(AgentTexts(agents, "Claude Code").Summary.TextLayout.TextLines.Count > 1, "\"Not signed in\" does not wrap.");
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
    [InlineData(252)]
    [InlineData(400)]
    public void Index_rows_keep_their_heights_and_right_edges_while_their_names_fit(double width)
    {
        var shell = OpenBoth();
        shell.SizeSidebar(width);
        var row = shell.WorkflowRow(Reports, ReleaseNotes);
        var project = shell.ProjectName(Reports).FindAncestorOfType<ToggleButton>()!;
        Assert.Equal((40.0, 30.0), (project.Bounds.Height, row.Bounds.Height));
        Assert.Equal("0/2", shell.WorkflowText(Reports, ReleaseNotes, "WorkflowSummary"));
        var (count, tile) = (Of<TextBlock>(row, "WorkflowSummary"), shell.ProjectItem(Reports).GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("projectTile")));
        Assert.Equal(20, tile.Bounds.Width);

        shell.Click(row);
        shell.Render();
        var stage = shell.Window.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("stage"));
        var agent = shell.TaskRow(Reports, ReleaseNotes, "Task 1");
        Assert.Equal((22.0, 28.0), (stage.Bounds.Height, agent.Bounds.Height));

        // A workflow's count, a stage's chevron, and an agent's mark share one right edge with the project's mark.
        var mark = Of<Control>(agent, "NodeRowState");
        Assert.Equal(FromEdge(shell, count), FromEdge(shell, mark), 0.5);
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
        Assert.Equal(50, shell.AgentRowTitles().Length);
        Assert.Equal(1, canvas.Stages.Count(stage => stage.Agents.Count == 50));
    }

    [AvaloniaFact]
    public void Showing_a_node_brings_its_row_into_the_Index_s_view()
    {
        var reports = _temp.Create(Reports);
        Save(reports, ReleaseNotes, [.. Enumerable.Range(1, 40).Select(index => $"Node {index}")]);
        var shell = Shell.Open(reports);
        shell.Window.Height = 600;
        var view = shell.Window.ViewModel;
        shell.Render();

        view.ShowNode(view.Canvas!.Outline[^1]);
        shell.Render();

        var tree = shell.Window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Tree");
        shell.WaitUntil(() =>
        {
            var row = shell.TaskRow(Reports, ReleaseNotes, "Node 40");
            var top = row.TranslatePoint(default, tree)!.Value.Y;
            return top >= 0 && top <= tree.Viewport.Height - row.Bounds.Height;
        }, "the agent's row comes into the Index's view");
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
        var name = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("nodeName"));
        var view = shell.Window.ViewModel;
        var node = view.Canvas!.Outline.Single(item => item.Title == Long);

        // Issue #108: every name in the left tree shows in full and wraps.
        Assert.Equal(Long, name.Text);
        Assert.True(name.TextLayout.TextLines.Count > 2, "The long name does not wrap past two lines.");
        Assert.DoesNotContain(name.TextLayout.TextLines, line => line.HasCollapsed);

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
    public void A_project_s_buttons_show_on_hover_in_place_of_its_mark()
    {
        var shell = OpenBoth();
        var item = shell.ProjectItem(Reports);
        var (menu, add) = (Of<Button>(item, "ProjectMenu"), Of<Button>(item, "NewWorkflow"));
        var actions = menu.GetVisualParent<StackPanel>()!;
        Assert.Equal(0, actions.Opacity);

        shell.Hover(shell.Center(shell.ProjectName(Reports)));

        Assert.Equal(1, actions.Opacity);
        Assert.Equal("menu 4, + 30", $"menu {FromEdge(shell, menu):0.#}, + {FromEdge(shell, add):0.#}");
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

    /// <summary>How far before the tree's content edge the visual's right edge is.</summary>
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
