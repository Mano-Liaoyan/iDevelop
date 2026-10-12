using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The sidebar's project tree from its 240 px default, which is also its narrowest, to its widest: project and workflow
/// names wrap in full instead of ending in an ellipsis, and each row keeps its glyph, its dots, its count, and its buttons on
/// its first line, at the right edges the inspector keeps (ADR 0021). Glyph buttons end at the tree's content edge, and a
/// count ends where their glyphs' drawing ends. Dots end just before a row's count or buttons, so rows of one kind line
/// them up.
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
    private const string Onboarding = "CustomerOnboardingAnalyticsPipelineForTheBillingTeam";

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
        Assert.All(new[] { shell.WorkflowName(Reports, Quarterly), shell.ProjectName(Onboarding) },
            name => Assert.True(name.TextLayout.TextLines.Count > 1, $"\"{name.Text}\" fits on one line at {width} px, so nothing here wraps."));
    }

    [AvaloniaFact]
    public void The_sidebar_s_splitter_widens_it_and_stops_at_its_240_px_default()
    {
        var shell = OpenBoth();
        var splitter = shell.Window.GetVisualDescendants().OfType<GridSplitter>().Single(control => Grid.GetColumn(control) == 1);
        var sidebar = shell.Window.FindControl<Grid>("Columns")!.ColumnDefinitions[0];

        shell.Drag(shell.Center(splitter), shell.Center(splitter) + new Vector(100, 0));
        Assert.InRange(sidebar.ActualWidth, 339, 341);

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
            $"Forget ends at {FromEdge(shell, forget):0.#}, centred {Centre(forget) - FirstLine(name):0.#} from the name's first line");
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
        // Each row centres its tile on its title's first line, 16 px into the row, the middle of a one-line row's 32 px.
        Assert.Equal(["first line 16, tile 0", "first line 16, tile 0, row 32"],
            rows.Select((row, index) => TaskCentres(row) + (index == 1 ? $", row {row.Bounds.Height:0.#}" : "")));
    }

    private static string TaskCentres(ListBoxItem row)
    {
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single();
        var line = FirstLine(title);
        return $"first line {line - Top(row):0.#}, tile {Centre(row.GetVisualDescendants().OfType<Theme.KindTile>().Single()) - line:0.#}";
    }

    [AvaloniaFact]
    public void An_agent_s_name_and_summary_wrap_in_full_with_its_dot_on_the_name_s_first_line()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var shell = Shell.Show(_fakes.DiscoverAsync().Result);
        var agents = shell.Find<ItemsControl>("Agents");
        // Every row fits the sidebar's 240 px today, so the list narrows to 90 px, where "Antigravity CLI" and "Ready · 3
        // models" need two lines, as a larger system font would leave them.
        Assert.Equal(4, AgentRows(agents).Length);
        Assert.All(AgentRows(agents), row => Assert.Equal("first line 10, dot 0", AgentCentres(row)));

        agents.Width = 90;
        shell.Render();

        Assert.All(AgentRows(agents), row => Assert.All(row.GetVisualDescendants().OfType<TextBlock>(), text => AssertWhole(text)));
        Assert.All(AgentRows(agents), row => Assert.Equal("first line 10, dot 0", AgentCentres(row)));
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
    public void Each_row_keeps_its_dots_count_and_buttons_on_its_first_line_at_shared_right_edges(double width)
    {
        FakeAgents.Install(_fakes, ClientId.Codex, Waits());
        var shell = OpenBoth(_fakes.DiscoverAsync().Result);
        // Release notes runs a task, so it and reports-service show the running dot, and Quarterly gets a 12th task and
        // shows the unsaved dot. Release notes counts 2.
        shell.Click(shell.WorkflowRow(Reports, ReleaseNotes));
        shell.RunOnItsOwn("Task 1");
        shell.Click(shell.WorkflowRow(Reports, Quarterly));
        shell.AddNode();
        shell.SizeSidebar(width);
        Assert.True(shell.WorkflowName(Reports, Quarterly).TextLayout.TextLines.Count > 1, "Quarterly's name does not wrap.");
        Assert.True(shell.ProjectName(Onboarding).TextLayout.TextLines.Count > 1, "Onboarding's name does not wrap.");

        // Glyph buttons end at the content edge and a count where their glyphs' drawing ends. Dots end 8 px before the
        // row's buttons, and in a workflow row, 8 px before its count's slot, which is as wide as the widest count, so
        // "2" and "12" leave their dots at one edge. A name ends 8 px before whatever the row shows after it.
        Assert.Equal("× 0, + 36, running 72, name 88", ProjectEdges(shell, Reports));
        Assert.Equal("× 0, + 36, name 72", ProjectEdges(shell, Onboarding));
        var slot = Slot(shell, Reports, Quarterly);
        Assert.Equal(slot, Slot(shell, Reports, ReleaseNotes));
        Assert.Equal($"count 8, running {slot + 8:0.#}, name {slot + 24:0.#}", WorkflowEdges(shell, Reports, ReleaseNotes));
        Assert.Equal($"count 8, unsaved {slot + 8:0.#}, name {slot + 24:0.#}", WorkflowEdges(shell, Reports, Quarterly));
        Assert.Equal($"count 8, name {slot + 8:0.#}", WorkflowEdges(shell, Onboarding, "Workflow"));
        // A count keeps the width of its own digits at the slot's right end, so "2" stays narrower than "12".
        var (two, twelve) = (Count(shell, Reports, ReleaseNotes).Bounds.Width, Count(shell, Reports, Quarterly).Bounds.Width);
        Assert.True(two < twelve, $"\"2\" is {two:0.#} px wide and \"12\" {twelve:0.#} px.");

        // Every item of a row centres on the first line of the row's name: 14 px into a project's row and 16 px into a
        // workflow's, the middle of a one-line row.
        Assert.Equal("first line 14, folder 0, running 0, + 0, × 0", ProjectCentres(shell, Reports));
        Assert.Equal("first line 14, folder 0, + 0, × 0", ProjectCentres(shell, Onboarding));
        Assert.Equal("first line 16, chevron 0, flowchart 0, running 0, count 0", WorkflowCentres(shell, Reports, ReleaseNotes));
        Assert.Equal("first line 16, chevron 0, flowchart 0, unsaved 0, count 0", WorkflowCentres(shell, Reports, Quarterly));
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
        var rows = shell.Window.FindControl<StackPanel>("TreeRows")!;
        return Left(rows, shell.Window) + rows.Bounds.Width - Left(visual, shell.Window) - visual.Bounds.Width;
    }

    private static double Left(Visual visual, Visual to) => visual.TranslatePoint(default, to)!.Value.X;

    private static double Top(Visual visual) => visual.TranslatePoint(default, (Visual)visual.GetVisualRoot()!)!.Value.Y;

    private static double Centre(Visual visual) => Top(visual) + visual.Bounds.Height / 2;

    /// <summary>The middle of the text's first line.</summary>
    private static double FirstLine(TextBlock text) => Top(text) + text.Padding.Top + text.TextLayout.TextLines[0].Height / 2;

    private static T Of<T>(Visual within, string automationId) where T : Control => Shell.ById<T>(within, automationId).Single();

    private static PathIcon Glyph(Visual within, string resource) =>
        within.GetVisualDescendants().OfType<PathIcon>().First(icon => icon.Data == (Geometry)Application.Current!.FindResource(resource)!);

    private static string Shown(params (string Name, Control Item, double Value)[] items) =>
        string.Join(", ", items.Where(item => item.Item.IsEffectivelyVisible).Select(item => $"{item.Name} {item.Value:0.#}"));

    private static string ProjectEdges(Shell shell, string project)
    {
        var item = shell.ProjectItem(project);
        var (close, add) = (Of<Button>(item, "CloseProject"), Of<Button>(item, "NewWorkflow"));
        var (waiting, running) = (Of<Control>(item, "ProjectWaiting"), Of<Control>(item, "ProjectRunning"));
        return Shown(("×", close, FromEdge(shell, close)), ("+", add, FromEdge(shell, add)), ("waiting", waiting, FromEdge(shell, waiting)),
            ("running", running, FromEdge(shell, running)), ("name", shell.ProjectName(project), FromEdge(shell, shell.ProjectName(project))));
    }

    private static string ProjectCentres(Shell shell, string project)
    {
        var item = shell.ProjectItem(project);
        var line = FirstLine(shell.ProjectName(project));
        var items = new (string, Control)[]
        {
            ("folder", Glyph(item, "IconFolder")), ("waiting", Of<Control>(item, "ProjectWaiting")), ("running", Of<Control>(item, "ProjectRunning")),
            ("+", Of<Button>(item, "NewWorkflow")), ("×", Of<Button>(item, "CloseProject")),
        };
        return $"first line {line - Top(item):0.#}, " + Shown([.. items.Select(entry => (entry.Item1, entry.Item2, Centre(entry.Item2) - line))]);
    }

    private static Control Dot(Shell shell, string project, string workflow, string automationId) =>
        Of<Control>(shell.WorkflowRow(project, workflow), automationId);

    private static Control Count(Shell shell, string project, string workflow) =>
        Of<TextBlock>(shell.WorkflowRow(project, workflow), "TaskCount").FindAncestorOfType<Border>()!;

    /// <summary>How far before the content edge the slot that holds the workflow's count starts.</summary>
    private static double Slot(Shell shell, string project, string workflow)
    {
        var slot = Count(shell, project, workflow).GetVisualParent<Control>()!;
        return Math.Round(FromEdge(shell, slot) + slot.Bounds.Width, 1);
    }

    private static string WorkflowEdges(Shell shell, string project, string workflow)
    {
        var name = shell.WorkflowName(project, workflow);
        var count = Count(shell, project, workflow);
        return Shown(("count", count, FromEdge(shell, count)),
            ("unsaved", Dot(shell, project, workflow, "WorkflowUnsaved"), FromEdge(shell, Dot(shell, project, workflow, "WorkflowUnsaved"))),
            ("waiting", Dot(shell, project, workflow, "WorkflowWaiting"), FromEdge(shell, Dot(shell, project, workflow, "WorkflowWaiting"))),
            ("running", Dot(shell, project, workflow, "WorkflowRunning"), FromEdge(shell, Dot(shell, project, workflow, "WorkflowRunning"))),
            ("name", name, FromEdge(shell, name)));
    }

    private static string WorkflowCentres(Shell shell, string project, string workflow)
    {
        var row = shell.WorkflowRow(project, workflow);
        var chevron = Shell.ById<Control>(row.FindAncestorOfType<Grid>()!, "WorkflowExpand").Single();
        var line = FirstLine(shell.WorkflowName(project, workflow));
        var items = new (string, Control)[]
        {
            ("chevron", chevron), ("flowchart", Glyph(row, "IconFlowchart")), ("unsaved", Dot(shell, project, workflow, "WorkflowUnsaved")),
            ("waiting", Dot(shell, project, workflow, "WorkflowWaiting")), ("running", Dot(shell, project, workflow, "WorkflowRunning")),
            ("count", Count(shell, project, workflow)),
        };
        return $"first line {line - Top(row):0.#}, " + Shown([.. items.Select(entry => (entry.Item1, entry.Item2, Centre(entry.Item2) - line))]);
    }
}
