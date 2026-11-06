using System.Collections.Immutable;
using Avalonia.Controls.Shapes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using Nodify;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>The minimap draws each card in its kind with its state's ring, and an open proposal's ghost cards.</summary>
[Collection(ProcessCollection.Name)]
public sealed class MinimapTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public MinimapTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    private static IEnumerable<MinimapItem> Items(Shell shell) =>
        shell.Find<Minimap>("Minimap").GetVisualDescendants().OfType<MinimapItem>();

    private static string[] Titles(Shell shell) =>
        [.. Items(shell).Select(item => item.DataContext switch
        {
            TaskNodeViewModel node => node.Title,
            GhostCardViewModel ghost => $"Ghost {ghost.Title}",
            var other => $"{other}",
        })];

    private static MinimapItem Item(Shell shell, string title) =>
        Items(shell).Single(item => item.DataContext is TaskNodeViewModel node && node.Title == title);

    private static Color Fill(MinimapItem item) =>
        ((ISolidColorBrush)item.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("minimapItem")).Background!).Color;

    [AvaloniaFact]
    public void A_failed_tasks_item_fills_in_red_while_an_idle_one_stays_quiet()
    {
        Install(_fakes, ClientId.Codex, On("exec", "--json").Replay(Fixture.Path("codex-bad-model.jsonl")));
        var shell = Shell.Open(
            _temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, Codex, "Draft it."), TaskAt(TestTasks.Build, "Build", 405, 90, Codex, "Build it.")),
            _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.ShowMinimap();
        shell.Click(shell.Header(shell.Node("Design")));

        shell.RunNode();
        shell.WaitUntil(() => shell.CardText("Design", "CardStatus") == "Failed", "the run fails");

        shell.WaitUntil(() => Fill(Item(shell, "Design")) == shell.Resource("StateFailedBrush"), "the minimap item turns red");
        Assert.Equal(shell.Resource("SurfaceMutedBrush"), Fill(Item(shell, "Build")));
    }

    [AvaloniaFact]
    public void An_open_proposals_ghost_cards_follow_the_cards_in_a_dashed_ring_and_leave_on_accept()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, """
            ```idevelop
            {"status": "proposal",
             "add": [{"id": "api", "type": "type-1", "title": "Backend API"}, {"id": "check", "type": "type-4", "title": "Review the API"}],
             "connect": [{"from": "planner", "to": "api"}, {"from": "api", "to": "check"}]}
            ```
            """)));
        var shell = Shell.Open(
            _temp.Seed(new WorkflowEdit.PlaceNode(TestTasks.Design, BuiltInBlueprints.Plan, new CanvasPoint(105, 90))
            {
                Title = "Plan export",
                Fields = ImmutableDictionary<string, string>.Empty.Add("goal", "Export the report as CSV."),
                Settings = new NodeSettings(Codex, ConversationMode.Chat),
            }),
            _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Plan export")));
        shell.RunNode();
        shell.WaitForProposal();
        shell.ShowMinimap();

        Assert.Equal(["Plan export", "Ghost Backend API", "Ghost Review the API"], Titles(shell));
        Assert.Equal(Color.Parse("#1E6EF4"), Fill(Item(shell, "Plan export")));
        var review = Items(shell).Single(item => item.DataContext is GhostCardViewModel { Title: "Review the API" });
        var dashed = review.GetVisualDescendants().OfType<Rectangle>().Single();
        Assert.Equal((Color.Parse("#008575"), true), (((ISolidColorBrush)dashed.Stroke!).Color, dashed.StrokeDashArray is { Count: > 0 }));
        var onCanvas = shell.Editor.GetVisualDescendants().OfType<DecoratorContainer>().Single(container => ReferenceEquals(container.DataContext, review.DataContext));
        Assert.Equal(onCanvas.Location, review.Location);
        Assert.NotEqual(Item(shell, "Plan export").Location, review.Location);

        shell.AddNode();
        Assert.Equal(["Plan export", "New task", "Ghost Backend API", "Ghost Review the API"], Titles(shell));

        shell.Click(shell.Header(shell.Node("Plan export")));
        shell.Click(shell.InView<Button>("AcceptProposal"));

        var titles = Titles(shell);
        Assert.Equal(["Plan export", "New task"], titles.Take(2));
        Assert.Equal(["Backend API", "Review the API"], titles.Skip(2).Order());
    }
}
