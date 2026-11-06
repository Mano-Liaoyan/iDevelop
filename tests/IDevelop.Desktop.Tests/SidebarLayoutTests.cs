using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    public void Project_and_workflow_names_show_in_full_at_every_sidebar_width(double width)
    {
        var shell = OpenBoth();
        shell.SizeSidebar(width);

        var names = new[]
        {
            shell.ProjectName(Reports), shell.WorkflowName(Reports, ReleaseNotes), shell.WorkflowName(Reports, Quarterly),
            shell.ProjectName(Onboarding), shell.WorkflowName(Onboarding, "Workflow"),
        };

        Assert.All(names, name => AssertWhole(shell, name));
        Assert.All([shell.WorkflowName(Reports, Quarterly), shell.ProjectName(Onboarding)],
            name => Assert.True(name.TextLayout.TextLines.Count > 1, $"\"{name.Text}\" fits on one line at {width} px, so nothing here wraps."));
    }

    [AvaloniaFact]
    public void The_sidebar_s_splitter_widens_it_and_stops_at_its_240_px_minimum()
    {
        var shell = OpenBoth();
        var splitter = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Single(control => Grid.GetColumn(control) == 1);
        var sidebar = shell.Window.FindControl<Grid>("Columns")!.ColumnDefinitions[0];

        shell.Drag(shell.Center(splitter), shell.Center(splitter) + new Vector(100, 0));
        Assert.InRange(sidebar.ActualWidth, 355, 357);

        shell.Drag(shell.Center(splitter), shell.Center(splitter) - new Vector(200, 0));
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
        shell.Click(shell.WorkflowExpand(Reports, ReleaseNotes));
        shell.SizeSidebar(width);
        var rows = new[] { LongTask, "Ship" }.Select(title => shell.TaskRow(Reports, ReleaseNotes, title)).ToArray();
        var titles = rows.Select(row => row.GetVisualDescendants().OfType<TextBlock>().Single()).ToArray();

        Assert.All(titles, title => AssertWhole(shell, title));
        Assert.True(titles[0].TextLayout.TextLines.Count > 1, $"The long title fits on one line at {width} px, so nothing here wraps.");
        // Each row centres its glyph on its title's first line, 14 px into the row, the middle of a one-line row's 28 px.
        Assert.Equal(["first line 14, glyph 0", "first line 14, glyph 0, row 28"],
            rows.Select((row, index) => TaskCentres(row) + (index == 1 ? $", row {row.Bounds.Height:0.#}" : "")));
    }

    private static string TaskCentres(ListBoxItem row)
    {
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single();
        var line = FirstLine(title);
        return $"first line {line - Top(row):0.#}, glyph {Centre(row.GetVisualDescendants().OfType<PathIcon>().First()) - line:0.#}";
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
    public void Rows_keep_one_height_while_their_names_fit_and_state_never_moves_a_row(double width)
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var shell = OpenBoth(_fakes.DiscoverAsync().Result);
        shell.SizeSidebar(width);
        var row = shell.WorkflowRow(Reports, ReleaseNotes);
        Assert.Equal((28.0, 44.0), (shell.ProjectName(Reports).FindAncestorOfType<ToggleButton>()!.Bounds.Height, row.Bounds.Height));
        Assert.Equal("2 nodes", shell.WorkflowText(Reports, ReleaseNotes, "WorkflowSummary"));

        shell.Click(row);
        shell.RunNode("Task 1");
        shell.AddNode();
        shell.Render();

        Assert.Equal(44, row.Bounds.Height);
        Assert.Equal("0 of 3 · Unsaved", shell.WorkflowText(Reports, ReleaseNotes, "WorkflowSummary"));
        Assert.True(Of<Control>(row.GetVisualParent()!, "WorkflowProgress").IsEffectivelyVisible);
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
        Assert.Equal("× 0, + 24", $"× {FromEdge(shell, close):0.#}, + {FromEdge(shell, add):0.#}");
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
