using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;
using static IDevelop.TestSupport.FakeRule;
using static IDevelop.Workflows.WorkflowEdit;

namespace IDevelop.Desktop.Tests;

/// <summary>The 240 by 64 card: its kind tile, its title and subtitle, its state's ring and glyph, its wires, and ghosts.</summary>
[Collection(ProcessCollection.Name)]
public sealed class CardTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly TaskId Review = TestTasks.Review;

    private static readonly Blueprint Researcher = new(
        new BlueprintKey("researcher-0a1b2c3d", 1), "Researcher",
        new WorkSpec.Agent(AgentAccess.ReadOnly, Proposes: false, PromptTemplate.Parse("Find out {{question}}.")),
        [new FieldSpec("question", "Question", FieldShape.Text, Required: true, "")],
        new NodeSettings(null, ConversationMode.Autonomous));

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;
    private readonly string _gate;

    public CardTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _gate = System.IO.Path.Combine(_temp.Create("evidence"), "go");
    }

    // A fake client that a failed test left waiting at the gate ends here, at the gate or at the deleted folder.
    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        _temp.Dispose();
    }

    private static PlaceNode Place(TaskId id, Blueprint blueprint, string title, double x, double y, ExecutionSettings? execution = null) =>
        new(id, blueprint, new CanvasPoint(x, y)) { Title = title, Settings = new NodeSettings(execution, blueprint.Defaults.Conversation) };

    /// <summary>A project with one node of each kind, the last from the project's library.</summary>
    private string EachKind()
    {
        var project = _temp.Seed(
            Place(TaskId.New(), BuiltInBlueprints.Implement, "Implement", 45, 90),
            Place(TaskId.New(), BuiltInBlueprints.Plan, "Plan", 345, 90),
            Place(TaskId.New(), BuiltInBlueprints.Architect, "Architect", 645, 90),
            Place(TaskId.New(), BuiltInBlueprints.Review, "Review", 45, 300),
            Place(TaskId.New(), BuiltInBlueprints.Approval, "Approval", 345, 300),
            Place(TaskId.New(), Researcher, "Researcher", 645, 300));
        BlueprintLibrary.Project(project).Save(Researcher);
        return project;
    }

    [AvaloniaTheory]
    [InlineData("ThemeLight", "#564ADE #007EAE #B02FC2 #008575 #956D51 #6C6C70")]
    [InlineData("ThemeDark", "#6D7CFF #1894C2 #DB34F2 #009B89 #B78A66 #8E8E93")]
    public void Each_kind_shows_its_own_tile_on_its_card(string theme, string tiles)
    {
        var shell = Shell.Open(EachKind());
        shell.Click(shell.Find<RadioButton>(theme));
        string[] titles = ["Implement", "Plan", "Architect", "Review", "Approval", "Researcher"];

        // 3 px in from the tile's left edge, at its middle, lies left of the glyph.
        Assert.Equal(tiles.Split(' ').Select(Color.Parse), titles.Select(title => shell.ColorAt(shell.CardKind(title), new Point(3, 16))));
        Assert.Equal(
            ["Implement", "Plan", "Architect", "Review", "Approval", "Read-only agent"],
            titles.Select(title => AutomationProperties.GetName(shell.CardKind(title))));
        Assert.All(titles, title => Assert.Equal(new Size(260, 64), shell.Node(title).Bounds.Size));
    }

    [AvaloniaFact]
    public void Only_a_library_blueprints_tile_carries_the_badge()
    {
        var shell = Shell.Open(EachKind());
        bool Badged(string title) => shell.CardKind(title).GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "PART_Badge").IsEffectivelyVisible;

        Assert.Equal([false, true], new[] { "Implement", "Researcher" }.Select(Badged));
        Assert.Equal("Researcher version 1", AutomationProperties.GetHelpText(shell.CardKind("Researcher")));
    }

    [AvaloniaFact]
    public void An_idle_card_shows_its_agent_and_no_glyph()
    {
        Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex, "Draft it.")), _fakes.DiscoverAsync().Result);

        Assert.Equal(["Design", "Codex · GPT-5.5 · high"], Shell.Texts(shell.Node("Design")));
        Assert.False(shell.CardGlyph("Design").IsEffectivelyVisible);
        Assert.Equal("Design\nImplement · Codex · GPT-5.5 · high\nDraft it.", ToolTip.GetTip(shell.InCard<Panel>("Design", "TaskCard")));
    }

    [AvaloniaFact]
    public void Renaming_on_the_card_shows_the_whole_title_line_where_the_title_was_and_leaves_the_subtitle_whole()
    {
        Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex, "Draft it.")), _fakes.DiscoverAsync().Result);
        var titleDrawnAt = shell.Bounds(shell.InCard<TextBlock>("Design", "CardTitle")).TopLeft;
        var subtitle = shell.Bounds(shell.InCard<TextBlock>("Design", "CardAgent"));
        shell.Click(shell.Header(shell.Node("Design")));
        var subtitleRows = shell.PixelRows(subtitle);
        int[] inked = [.. Enumerable.Range(0, subtitleRows.Length).Where(row => subtitleRows[row].Distinct().Count() > 1)];

        shell.Press(Key.F2);

        var box = shell.InCard<TextBox>("Design", "CardTitleBox");
        var text = box.GetVisualDescendants().OfType<TextPresenter>().Single();
        var shown = shell.Bounds(text.FindAncestorOfType<ScrollContentPresenter>()!).Intersect(shell.Bounds(box).Deflate(box.BorderThickness));
        var subtitleRowsWhileRenaming = shell.PixelRows(subtitle);
        Assert.True(box.IsFocused);
        Assert.True(Point.Distance(titleDrawnAt, shell.Bounds(text).TopLeft) <= 0.5, $"The text moved from {titleDrawnAt} to {shell.Bounds(text).TopLeft}.");
        Assert.True(shown.Contains(shell.Bounds(text)), $"The box shows {shown} of its line at {shell.Bounds(text)}.");
        Assert.NotEmpty(inked);
        Assert.All(inked, row => Assert.True(subtitleRows[row].SequenceEqual(subtitleRowsWhileRenaming[row]), $"The box covers the subtitle's row {subtitle.Top + row}."));
    }

    [AvaloniaFact]
    public void The_card_holds_back_its_tooltip_while_its_title_is_renamed()
    {
        Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex, "Draft it.")), _fakes.DiscoverAsync().Result);
        var card = shell.InCard<Panel>("Design", "TaskCard");
        shell.Click(shell.Header(shell.Node("Design")));
        ToolTip.SetIsOpen(card, true);
        shell.Render();
        Assert.True(ToolTip.GetIsOpen(card));

        shell.Press(Key.F2);

        Assert.Equal((false, null), (ToolTip.GetIsOpen(card), ToolTip.GetTip(card)));
        shell.Type("Parser");
        shell.Press(Key.Enter);
        Assert.Equal("Parser\nImplement · Codex · GPT-5.5 · high\nDraft it.", ToolTip.GetTip(card));
    }

    [AvaloniaFact]
    public void A_card_without_an_agent_asks_for_one_with_the_warning_glyph()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Build, "Build", 405, 90)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));

        Assert.Equal(["Build", "Choose an agent"], Shell.Texts(shell.Node("Build")));
        var glyph = shell.CardGlyph("Build");
        Assert.True(glyph.IsEffectivelyVisible);
        Assert.Same(Application.Current!.FindResource("IconStateNeedsSetup"), glyph.Data);
        Assert.Equal(Color.Parse("#A16A00"), ((ISolidColorBrush)glyph.Foreground!).Color);
    }

    [AvaloniaFact]
    public void A_running_card_rings_in_its_kinds_stroke_and_says_so()
    {
        Install(_fakes, ClientId.Codex, On("exec", "--json").Print("""{"type":"thread.started","thread_id":"01a104d5-d442-71a1-9b08-8938c119e5ae"}""").WaitForFile(_gate).Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex, "Draft it.")), _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.InView<Button>("RunTask"));

        var ring = shell.CardLayer("Design", "stateStroke");
        Assert.Equal(["Design", "Running"], Shell.Texts(shell.Node("Design")));
        Assert.Contains("state-running", ring.Classes);
        Assert.Equal(shell.Resource("KindImplementStrokeBrush"), ((ISolidColorBrush)ring.BorderBrush!).Color);
        Assert.Same(Application.Current!.FindResource("IconStateRunning"), shell.CardGlyph("Design").Data);
        File.WriteAllText(_gate, "");
        shell.WaitUntil(() => shell.CardText("Design", "CardStatus") == "Succeeded", "the run succeeds");
    }

    [AvaloniaFact]
    public void A_waiting_card_rings_in_orange_and_says_it_waits()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, "Which file?")));
        var shell = Shell.Open(
            _temp.Seed(TaskAt(Design, "Design", 105, 90, Codex, "Draft it.", ConversationMode.Chat)), _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Design", "CardStatus") == "Waiting for you", "the task waits");

        var ring = shell.CardLayer("Design", "stateStroke");
        Assert.Equal(["Design", "Waiting for you"], Shell.Texts(shell.Node("Design")));
        Assert.Contains("state-waiting", ring.Classes);
        Assert.Equal(Color.Parse("#C55300"), ((ISolidColorBrush)ring.BorderBrush!).Color);
    }

    [AvaloniaTheory]
    [InlineData("codex-success.jsonl", "Succeeded", "#008932")]
    [InlineData("codex-bad-model.jsonl", "Failed", "#E9152D")]
    public void A_finished_card_shows_its_ending_in_its_ring(string stream, string label, string ring)
    {
        Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path(stream)));
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex, "Draft it.")), _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Design")));

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Design", "CardStatus") == label, "the run ends");
        // The ring is the card's 1.5 px border, so the card's top pixel row is the ring alone. The frame that shows it can
        // come a render tick after the status text, so the test waits for the pixel too.
        shell.WaitUntil(() => shell.ColorAt(shell.Node("Design"), new Point(130, 0)) == Color.Parse(ring), $"the ring turns {ring}");

        Assert.Equal(["Design", label], Shell.Texts(shell.Node("Design")));
    }

    [AvaloniaFact]
    public void A_planner_with_an_open_proposal_shows_it_in_the_accent_without_a_pulse_and_draws_its_tasks_as_ghosts()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, """
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "api", "type": "type-1", "title": "Backend API"}, {"id": "ui", "type": "type-1", "title": "Export button"}],
             "connect": [{"from": "planner", "to": "api"}, {"from": "api", "to": "ui"}]}
            ```
            """)));
        var shell = Shell.Open(
            _temp.Seed(new PlaceNode(Design, BuiltInBlueprints.Plan, new CanvasPoint(105, 90))
            {
                Title = "Plan export",
                Fields = ImmutableDictionary<string, string>.Empty.Add("goal", "Export the report as CSV."),
                Settings = new NodeSettings(Codex, ConversationMode.Chat),
            }),
            _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Plan export")));

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");

        var ring = shell.CardLayer("Plan export", "stateStroke");
        Assert.Equal(["Plan export", "Proposal · 2 tasks"], Shell.Texts(shell.Node("Plan export")));
        Assert.Same(Application.Current!.FindResource("IconSparkle"), shell.CardGlyph("Plan export").Data);
        Assert.Contains("role-proposing", ring.Classes);
        Assert.Equal(Color.Parse("#1E6EF4"), ((ISolidColorBrush)ring.BorderBrush!).Color);
        shell.Render();
        Assert.Equal(1, ring.Opacity);

        var ghost = shell.Window.GetVisualDescendants().OfType<Panel>()
            .First(panel => AutomationProperties.GetAutomationId(panel) == "GhostCard" && AutomationProperties.GetName(panel) == "Backend API");
        var border = ghost.GetVisualDescendants().OfType<Rectangle>().Single();
        Assert.Equal(["Backend API", "New Implement"], Shell.Texts(ghost));
        Assert.Equal((Color.Parse("#564ADE"), true), (((ISolidColorBrush)border.Stroke!).Color, border.StrokeDashArray is { Count: > 0 }));
    }

    [AvaloniaFact]
    public void Selecting_a_node_dims_the_connections_that_do_not_touch_it()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90),
            TaskAt(Build, "Build", 405, 90),
            TaskAt(Review, "Review", 705, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency),
            new Connect(new ConnectionKey(Build, Review), ConnectionKind.Dependency)));
        BaseConnection Wire(string to) => shell.Connections().Single(connection => ((ConnectionViewModel)connection.DataContext!).To.Title == to);
        (double, double) Opacities() => (Wire("Build").Opacity, Wire("Review").Opacity);
        Assert.Equal((1.0, 1.0), Opacities());

        shell.Click(shell.Header(shell.Node("Design")));
        shell.WaitUntil(() => Opacities() == (1.0, 0.35), "the connection away from Design dims");
        Assert.Equal((2.5, 2.0), (Wire("Build").StrokeThickness, Wire("Review").StrokeThickness));

        shell.Click(shell.Editor.TranslatePoint(new Point(400, 500), shell.Window)!.Value);
        shell.WaitUntil(() => Opacities() == (1.0, 1.0), "nothing is dimmed without a selection");
    }

    [AvaloniaFact]
    public void A_context_connection_is_dashed_in_its_source_kinds_stroke()
    {
        var shell = Shell.Open(_temp.Seed(
            Place(Design, BuiltInBlueprints.Plan, "Plan", 105, 90),
            TaskAt(Build, "Build", 405, 90),
            new Connect(new ConnectionKey(Design, Build), ConnectionKind.Context)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));

        var wire = Assert.Single(shell.Connections());

        Assert.Equal((Color.Parse("#007EAE"), true), (((ISolidColorBrush)wire.Stroke!).Color, wire.StrokeDashArray is { Count: > 0 }));
    }
}
