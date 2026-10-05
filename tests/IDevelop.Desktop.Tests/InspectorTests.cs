using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Inspector;
using IDevelop.Desktop.Theme;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>The Godot-style inspector: its header, filter, folds, reverts, and the connection, workflow, and proposal views.</summary>
[Collection(ProcessCollection.Name)]
public sealed class InspectorTests : IDisposable
{
    private const string Session = "01a108d7-464d-77a3-8908-a36f38ce6c14";
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public InspectorTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("acc", "Acceptance criteria", true)]
    [InlineData("rsn", "Reasoning", true)]
    [InlineData("RSN", "reasoning", true)]
    [InlineData("criteria", "Acceptance criteria", true)]
    [InlineData("acc", "Agent", false)]
    [InlineData("nsr", "Reasoning", false)]
    [InlineData("", "Agent", true)]
    [InlineData("a", null, false)]
    public void The_filter_matches_a_substring_or_the_letters_in_order(string query, string? text, bool matches) =>
        Assert.Equal(matches, InspectorFilter.Matches(query, text));

    [AvaloniaFact]
    public void The_header_shows_the_kind_tile_and_keeps_the_type_and_its_version()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal(["Implement", "Built-in, version 1"], new[] { "TaskType", "TaskTypeVersion" }.Select(id => shell.Find<TextBlock>(id).Text));
        Assert.Equal(Color.Parse("#564ADE"), shell.ColorAt(HeaderTile(shell), new Point(3, 16)));
        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);

        shell.Click(shell.Find<RadioButton>("ThemeDark"));
        Assert.Equal(Color.Parse("#6D7CFF"), shell.ColorAt(HeaderTile(shell), new Point(3, 16)));
    }

    [AvaloniaFact]
    public void Filtering_by_acc_leaves_only_acceptance_criteria_and_Escape_brings_everything_back()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Contains("Client", shell.ShownRows());

        shell.FilterInspector("acc");

        Assert.Equal(["Acceptance criteria"], shell.ShownRows());
        Assert.Equal((true, false, false), (shell.Shows("Task"), shell.Shows("Agent"), shell.Shows("Blueprint")));
        Assert.False(shell.Find<TextBox>("TaskInstructions").IsEffectivelyVisible);
        Assert.True(shell.Find<TextBox>("TaskAcceptanceCriteria").IsEffectivelyVisible);

        shell.Press(Key.Escape);

        Assert.Equal("", shell.Find<TextBox>("InspectorFilter").Text);
        Assert.True(shell.Shows("Agent"));
        Assert.Equal(["Instructions", "Acceptance criteria", "Client", "Model", "Reasoning", "Conversation"], shell.ShownRows().Take(6));
    }

    [AvaloniaFact]
    public void The_filter_keeps_its_text_when_another_task_is_selected()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 465, 90)));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.FilterInspector("acc");

        shell.Click(shell.Header(shell.Node("Build")));

        Assert.Equal("Build", shell.Find<TextBox>("TaskTitle").Text);
        Assert.Equal("acc", shell.Find<TextBox>("InspectorFilter").Text);
        Assert.Equal(["Acceptance criteria"], shell.ShownRows());
    }

    [AvaloniaFact]
    public void Folding_the_agent_counts_a_changed_client_and_the_fold_holds_for_the_next_task()
    {
        Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 465, 90)), _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Pick("TaskClient", "Codex");

        shell.Fold("Agent");

        Assert.Equal("(1)", shell.Section("Agent").Badge);
        Assert.Contains("(1)", Shell.Texts(shell.Section("Agent")));
        Assert.False(shell.Find<ComboBox>("TaskClient").IsEffectivelyVisible);

        shell.Click(shell.Header(shell.Node("Build")));

        Assert.True(shell.Section("Agent").IsFolded);
        Assert.Equal("", shell.Section("Agent").Badge);

        shell.Fold("Agent");

        Assert.True(shell.Find<ComboBox>("TaskClient").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Collapse_All_folds_every_section_and_Expand_All_opens_them_again()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.Click(shell.Header(shell.Node("Design")));
        string[] keys = ["Task", "Agent", "Run", "Conversation", "Blueprint"];

        Choose(shell, "Collapse All");

        Assert.All(keys, key => Assert.True(shell.Section(key).IsFolded, key));
        Assert.Empty(shell.ShownRows());

        Choose(shell, "Expand All");

        Assert.All(keys, key => Assert.False(shell.Section(key).IsFolded, key));
        Assert.Contains("Client", shell.ShownRows());
    }

    [AvaloniaFact]
    public void A_field_revert_shows_after_an_edit_and_puts_the_default_back_in_one_edit()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.False(shell.Find<Button>("RevertInstructions").IsEffectivelyVisible);

        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Write it.");
        Assert.True(shell.Find<Button>("RevertInstructions").IsEffectivelyVisible);
        var document = shell.Window.ViewModel.Canvas!.Document;
        var edits = 0;
        document.Changed += (_, _) => edits++;

        shell.Click(shell.Find<Button>("RevertInstructions"));

        Assert.Equal(("", 1), (shell.Find<TextBox>("TaskInstructions").Text, edits));
        Assert.Equal("", document.Current.Tasks[Design].Field("instructions"));
        Assert.False(shell.Find<Button>("RevertInstructions").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_conversation_revert_shows_only_while_the_mode_differs_from_the_default()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Header(shell.Node("Design")));
        Assert.False(shell.Find<Button>("RevertConversation").IsEffectivelyVisible);

        shell.Pick("TaskConversation", "Chat");
        Assert.True(shell.Find<Button>("RevertConversation").IsEffectivelyVisible);

        shell.Click(shell.Find<Button>("RevertConversation"));

        Assert.Equal("Autonomous", shell.Picked("TaskConversation"));
        Assert.Equal(ConversationMode.Autonomous, shell.Window.ViewModel.Canvas!.Workflow.Tasks[Design].Conversation);
        Assert.False(shell.Find<Button>("RevertConversation").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData(ClientId.Codex, "Autonomous")]
    [InlineData(ClientId.ClaudeCode, "May ask")]
    [InlineData(ClientId.Pi, "Chat")]
    [InlineData(ClientId.Antigravity, "Autonomous")]
    public void Each_note_shows_a_short_line_whole_and_its_tooltip_and_info_glyph_hold_the_full_note(ClientId client, string conversation)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(client))));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Pick("TaskConversation", conversation);

        foreach (var (id, info) in new[] { ("PermissionNote", "TaskClient"), ("ConversationNote", "TaskConversation") })
        {
            var note = shell.Find<TextBlock>(id);
            var full = ToolTip.GetTip(note) as string;
            Assert.True(note.IsEffectivelyVisible);
            Assert.True(note.Text!.Length < full!.Length, $"{id} shows \"{note.Text}\", no shorter than \"{full}\".");
            Assert.False(note.TextLayout.TextLines.Any(line => line.HasCollapsed), $"{id} cuts \"{note.Text}\" short.");
            Assert.Equal(full, Avalonia.Automation.AutomationProperties.GetHelpText(note));
            Assert.Equal(full, Info(shell, info));
        }
    }

    [AvaloniaFact]
    public void After_a_run_the_activity_section_shows_the_runs_activity()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Say hi", 105, 90, Codex, "Reply with DONE.")), _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Say hi")));
        Assert.False(shell.Section("Activity").IsEffectivelyVisible);

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the run succeeds");

        var activity = shell.InView<ItemsControl>("LastRunActivity");
        Assert.True(activity.IsEffectivelyVisible);
        Assert.Contains("DONE", activity.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.Equal("Succeeded", shell.Find<TextBlock>("LastRunStatus").Text);
    }

    [AvaloniaFact]
    public void The_connection_inspector_draws_the_source_kinds_swatch_and_selects_its_ends()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 465, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));

        shell.Click(shell.ConnectionInto("Build"));

        Assert.Equal(["Dependency", "Connection"], Shell.Texts(shell.Find<Control>("InspectorHeader")));
        var swatch = shell.Find<Button>("KindDependency").GetVisualDescendants().OfType<Line>().Single();
        Assert.Equal(Color.Parse("#564ADE"), ((ISolidColorBrush)swatch.Stroke!).Color);
        Assert.Equal("Build", Shell.TextOf(shell.Find<Button>("ConnectionTo")));

        shell.Click(shell.Find<Button>("ConnectionFrom"));

        Assert.Equal("Design", shell.Find<TextBox>("TaskTitle").Text);
        Assert.True(shell.Node("Design").IsSelected);
    }

    [AvaloniaFact]
    public void With_nothing_selected_the_inspector_counts_the_workflows_kinds()
    {
        var shell = Shell.Open(_temp.Seed(
            TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 465, 90),
            new WorkflowEdit.PlaceNode(TaskId.New(), BuiltInBlueprints.Review, new CanvasPoint(825, 90)) { Title = "Check" },
            new WorkflowEdit.PlaceNode(TaskId.New(), BuiltInBlueprints.Plan, new CanvasPoint(105, 330)) { Title = "Plan it" }));

        Assert.Equal(["seed", "4 tasks"], Shell.Texts(shell.Find<Control>("InspectorHeader")));
        Assert.Equal(["Overview", "Implement", "2", "Plan", "1", "Review", "1"], Shell.Texts(shell.Section("Overview")));
        Assert.True(shell.Section("Library").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Accept_and_Finish_adds_the_proposed_tasks_and_ends_the_planners_conversation()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, """
            Here is the plan.

            ```idevelop
            {"status": "proposal", "add": [{"id": "wire", "type": "type-1", "title": "Wire export", "fields": {"instructions": "Connect them."}}]}
            ```
            """)));
        var shell = Shell.Open(
            _temp.Seed(new WorkflowEdit.PlaceNode(Design, BuiltInBlueprints.Architect, new CanvasPoint(105, 90))
            {
                Title = "Design export",
                Fields = ImmutableDictionary<string, string>.Empty.Add("brief", "Export the report as CSV."),
                Settings = new NodeSettings(Codex, ConversationMode.Chat),
            }),
            _fakes.DiscoverAsync().Result);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        shell.Click(shell.Header(shell.Node("Design export")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.Has<StackPanel>("Proposal"), "the proposal shows");

        var finish = shell.InView<Button>("ProposalAcceptFinish");
        shell.Window.MouseMove(default);
        shell.Render();
        Assert.True(finish.IsEffectivelyVisible && finish.IsEffectivelyEnabled);
        Assert.Equal(Color.Parse("#1E6EF4"), shell.ColorAt(finish, new Point(4, finish.Bounds.Height / 2)));
        Assert.Equal((true, false), (shell.Find<Button>("AcceptProposal").Classes.Contains("action"), shell.Find<Button>("AcceptProposal").Classes.Contains("primary")));
        Assert.Contains(shell.Find<CheckBox>("ProposalItem").GetVisualDescendants().OfType<KindTile>(), tile => tile.Kind == NodeKind.Implement);

        shell.Click(finish);

        Assert.Single(shell.Window.ViewModel.Canvas!.Workflow.Tasks.Values, task => task.Title == "Wire export");
        Assert.Equal("Added 1 task and filled 0 tasks.", shell.Status);
        shell.WaitUntil(() => shell.CardText("Design export", "CardStatus") == "Succeeded", "the planner is done");
        Assert.False(shell.Has<StackPanel>("Proposal"));
    }

    /// <summary>Opens the sections menu and chooses the entry. A headless popup takes no clicks, so the entry raises its own.</summary>
    private static void Choose(Shell shell, string entry)
    {
        var tools = shell.Find<Button>("InspectorTools");
        shell.Click(tools);
        var item = shell.Window.GetVisualDescendants().OfType<MenuItem>().Single(menu => (string?)menu.Header == entry);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        tools.Flyout!.Hide();
        shell.Render();
    }

    /// <summary>The tooltip of the info glyph in the row that holds the editor.</summary>
    private static object? Info(Shell shell, string editor) =>
        ToolTip.GetTip(shell.Find<Control>(editor).FindAncestorOfType<InspectorRow>()!.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("infoGlyph")));

    private static KindTile HeaderTile(Shell shell) => shell.Find<Control>("InspectorHeader").GetVisualDescendants().OfType<KindTile>().Single();
}
