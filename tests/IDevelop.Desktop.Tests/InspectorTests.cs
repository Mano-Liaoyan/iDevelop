using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    [InlineData(ClientId.Codex, "Autonomous", "Edits files. Runs commands in a sandbox.", "Works without waiting for you.")]
    [InlineData(ClientId.ClaudeCode, "May ask", "Edits files. Runs only allowed commands.", "May stop to ask you a question.")]
    [InlineData(ClientId.Pi, "Chat", "Edits files and runs any command.", "Waits for you after every turn.")]
    [InlineData(ClientId.Antigravity, "Autonomous", "Edits files. Runs no commands.", "Works without waiting for you.")]
    public void Each_agent_note_lives_in_its_info_glyph_and_no_line_under_the_pickers_repeats_it(ClientId client, string conversation, string permission, string mode)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, new ExecutionSettings(client))));
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Pick("TaskConversation", conversation);

        foreach (var (id, editor, summary) in new[] { ("PermissionNote", "TaskClient", permission), ("ConversationNote", "TaskConversation", mode) })
        {
            var glyph = shell.Find<Control>(id);
            var peer = ControlAutomationPeer.CreatePeerForElement(glyph);
            var full = Info(shell, editor) as string;
            Assert.Same(InfoGlyph(shell, editor), glyph);
            Assert.True(glyph.IsEffectivelyVisible);
            Assert.True(summary.Length < full!.Length, $"{id}'s tooltip \"{full}\" is no longer than \"{summary}\".");
            Assert.Equal((id, summary, full, true), (peer.GetAutomationId(), peer.GetName(), peer.GetHelpText(), peer.IsControlElement()));
            Assert.DoesNotContain(summary, Shell.Texts(shell.Section("Agent")));
        }
    }

    [AvaloniaFact]
    public void After_a_run_its_agent_and_time_show_as_chips_and_its_tool_calls_wait_behind_a_disclosure()
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Say hi", 105, 90, Codex, "Reply with DONE.")), _fakes.DiscoverAsync().Result);
        shell.Click(shell.Header(shell.Node("Say hi")));
        Assert.False(shell.Section("Activity").IsEffectivelyVisible);

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the run succeeds");

        Assert.Equal("Succeeded", shell.Find<TextBlock>("LastRunStatus").Text);
        var agent = shell.InView<ItemsControl>("LastRunConfiguration");
        Assert.Equal(["Codex", "gpt-5.5", "high"], Shell.Texts(agent));
        Assert.Equal("Requested Codex · gpt-5.5 · high.", ToolTip.GetTip(agent));
        var time = shell.Find<Control>("LastRunTiming");
        Assert.Matches(@"^Just now \d+ s$", Shell.TextOf(time));
        Assert.Matches(@"^Started .+ · took \d+ s$", (string)ToolTip.GetTip(time)!);
        Assert.Equal(2, time.GetVisualDescendants().OfType<Border>().Count(border => border.IsEffectivelyVisible && border.Classes.Contains("chip")));

        var activity = shell.InView<ItemsControl>("LastRunActivity");
        var tools = shell.InView<ToggleButton>("ToolCallsToggle");
        Assert.Equal(["DONE"], Shell.Texts(activity));
        Assert.Equal("1 tool call", Shell.TextOf(tools));

        shell.Click(tools);

        Assert.Equal(["command: pwsh.exe -Command \"Set-Content -LiteralPath .\\\\hello.txt -Value 'hi' -NoNewline\"", "DONE"], Shell.Texts(activity));

        var runs = shell.Window.ViewModel.Canvas!.Runs;
        var first = runs.Latest[Design].Id;
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => runs.Latest[Design] is { Status: AttemptStatus.Succeeded } next && next.Id != first, "the next run succeeds");
        shell.Render();

        Assert.True(shell.Find<ToggleButton>("ToolCallsToggle").IsChecked, "The disclosure stays open for the task's next run.");
        Assert.Equal(2, Shell.Texts(shell.Find<ItemsControl>("LastRunActivity")).Length);
    }

    [Theory]
    [InlineData(0, "Just now")]
    [InlineData(59, "Just now")]
    [InlineData(-30, "Just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(4 * 3600, "Today 10:00")]
    [InlineData(20 * 3600, "Yesterday 18:00")]
    [InlineData(3 * 24 * 3600, "10/02/2026 14:00")]
    public void A_runs_start_reads_relative_to_now(int secondsAgo, string text)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var noon = new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Unspecified);
            var now = new DateTimeOffset(noon, TimeZoneInfo.Local.GetUtcOffset(noon));
            Assert.Equal(text, IDevelop.Desktop.Execution.RunText.Ago(now.AddSeconds(-secondsAgo).ToLocalTime(), now));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("t t a", "t t a")]
    [InlineData("t t t", "t t t")]
    [InlineData("a b c", "a b c")]
    [InlineData("t a t b c t", "t a t b c t")]
    [InlineData("t a b c d", "b c d")]
    [InlineData("a b c d t", "b c d t")]
    [InlineData("a t b t c t d", "b t c t d")]
    [InlineData("t t t t", "t t t")]
    public void The_activity_keeps_the_last_lines_said_and_every_tool_call_since_the_earliest(string lines, string kept)
    {
        // Each word is a line, "t" a tool call and any other word a message; three messages fit.
        static ActivityLine[] Lines(string words) =>
            [.. words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((word, index) => new ActivityLine(default, $"{word}{index}", word == "t"))];

        var shown = IDevelop.Desktop.Execution.AttemptViewModel.Recent(Lines(lines), 3);

        Assert.Equal(kept, string.Join(' ', shown.Select(line => line.Text.TrimEnd("0123456789".ToCharArray()))));
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
        var shell = WaitingArchitect();

        var finish = shell.InView<Button>("ProposalAcceptFinish");
        shell.Window.MouseMove(default);
        shell.Render();
        Assert.True(finish.IsEffectivelyVisible && finish.IsEffectivelyEnabled);
        Assert.Equal(Color.Parse("#1E6EF4"), shell.ColorAt(finish, new Point(4, finish.Bounds.Height / 2)));
        Assert.Equal((true, false), (shell.Find<Button>("AcceptProposal").Classes.Contains("action"), shell.Find<Button>("AcceptProposal").Classes.Contains("primary")));
        Assert.Contains(shell.Find<CheckBox>("ProposalItem").GetVisualDescendants().OfType<KindTile>(), tile => tile.Kind == NodeKind.Implement);

        shell.Click(finish);

        Assert.Single(shell.Window.ViewModel.Canvas!.Workflow.Tasks.Values, task => task.Title == "Wire export");
        Assert.Equal("Added 1 task.", shell.Status);
        shell.WaitUntil(() => shell.CardText("Design export", "CardStatus") == "Succeeded", "the planner is done");
        Assert.False(shell.Has<StackPanel>("Proposal"));
    }

    [AvaloniaFact]
    public void While_a_proposal_is_open_accepting_is_the_only_primary_action_and_the_run_shows_only_its_status()
    {
        var shell = WaitingArchitect();

        Assert.Equal(["ProposalAcceptFinish"], PrimaryButtons(shell));
        Assert.Equal((false, false, false, false), (Shows(shell, "RunTask"), Shows(shell, "CancelRun"), Shows(shell, "StartProblem"), Shows(shell, "LastRunConfiguration")));
        Assert.Equal("Waiting for you", shell.InView<TextBlock>("LastRunStatus").Text);
        Assert.Equal(["Status"], Shell.Texts(shell.Section("Run")).Where(text => text != "Run" && text != "Waiting for you"));

        shell.Click(shell.InView<Button>("ProposalAcceptFinish"));
        shell.WaitUntil(() => shell.CardText("Design export", "CardStatus") == "Succeeded", "the planner is done");

        Assert.Equal(["RunTask"], PrimaryButtons(shell));
        Assert.Equal((true, true, true), (Shows(shell, "RunTask"), Shows(shell, "CancelRun"), Shows(shell, "LastRunConfiguration")));
    }

    [AvaloniaFact]
    public void A_proposal_lists_each_task_by_its_tile_and_title_and_keeps_its_connections_behind_a_disclosure()
    {
        var shell = WaitingArchitect();

        var item = shell.InView<CheckBox>("ProposalItem");
        Assert.Equal(["Wire export"], Shell.Texts(item));
        Assert.Equal(("Add Implement \"Wire export\"", "Add Implement \"Wire export\"\nConnect them."), (AutomationProperties.GetName(item), ToolTip.GetTip(item)));
        var toggle = shell.InView<ToggleButton>("ProposalConnectionsToggle");
        var connections = shell.Find<ItemsControl>("ProposalConnections");
        Assert.Equal(("1 connection", false), (Shell.TextOf(toggle), connections.IsEffectivelyVisible));

        shell.Click(toggle);

        Assert.Equal(["Design export → Wire export"], Shell.Texts(connections));
    }

    [AvaloniaFact]
    public void A_planner_whose_next_turn_runs_under_an_open_proposal_offers_Cancel_but_not_Run()
    {
        var gate = System.IO.Path.Combine(_temp.Create("evidence"), "go");
        var shell = WaitingArchitect(Resuming(ClientId.Codex, Session).Print(SessionLine(ClientId.Codex, Session)).WaitForFile(gate).Print(ReplyLines(ClientId.Codex, "Noted.")));
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Keep the export small.");

        shell.Click(shell.InView<Button>("SendMessage"));
        shell.WaitUntil(() => shell.Find<TextBlock>("LastRunStatus").Text == "Running", "the next turn runs");

        Assert.True(shell.Has<StackPanel>("Proposal"));
        Assert.Equal((false, true), (Shows(shell, "RunTask"), Shows(shell, "CancelRun")));
        Assert.Equal(["AcceptProposal"], PrimaryButtons(shell));
        Assert.True(shell.Find<Button>("CancelRun").IsEffectivelyEnabled);

        File.WriteAllText(gate, "");
        shell.WaitUntil(() => shell.Find<TextBlock>("LastRunStatus").Text == "Waiting for you", "the turn ends waiting");

        Assert.Equal((false, false), (Shows(shell, "RunTask"), Shows(shell, "CancelRun")));
    }

    [AvaloniaFact]
    public void A_long_title_ends_in_an_ellipsis_until_its_box_has_focus()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Add CSV export to the reports page with tests for quoting", 105, 90)));
        shell.Click(shell.Header(shell.Node("Add CSV export to the reports page with tests for quoting")));
        var box = shell.Find<TextBox>("TaskTitle");
        var shown = shell.Find<Control>("InspectorHeader").GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("headerTitle"));
        var typed = box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().Single();

        Assert.True(shown.IsEffectivelyVisible);
        Assert.True(shown.TextLayout.TextLines.Any(line => line.HasCollapsed), "The title ends in an ellipsis.");
        Assert.Equal(0, typed.Opacity);
        Assert.Equal(shell.Bounds(typed).X, shell.Bounds(shown).X, 0.5);
        Assert.Equal(shell.Center(typed).Y, shell.Center(shown).Y, 0.5);

        shell.Click(box);
        shell.Press(Key.End);

        Assert.False(shown.IsEffectivelyVisible);
        Assert.Equal(1, typed.Opacity);
        shell.Type("!");
        Assert.Equal("Add CSV export to the reports page with tests for quoting!", shell.Window.ViewModel.Canvas!.Workflow.Tasks[Design].Title);
    }

    /// <summary>A Chat Architect that ran once and waits with a proposal to add one task after it.</summary>
    private Shell WaitingArchitect(params FakeRule[] resumes)
    {
        Install(_fakes, ClientId.Codex, [.. resumes, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, Session)).Print(ReplyLines(ClientId.Codex, """
            Here is the plan.

            ```idevelop
            {"status": "proposal", "add": [{"id": "wire", "type": "type-1", "title": "Wire export", "fields": {"instructions": "Connect them."}}],
             "connect": [{"from": "planner", "to": "wire"}]}
            ```
            """))]);
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
        shell.WaitUntil(() => shell.Find<TextBlock>("LastRunStatus").Text == "Waiting for you", "the planner waits");
        return shell;
    }

    /// <summary>The automation ids of the primary buttons the inspector shows now.</summary>
    private static string[] PrimaryButtons(Shell shell) =>
        [.. shell.Find<Control>("Inspector").GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("primary") && button.IsEffectivelyVisible)
            .Select(button => AutomationProperties.GetAutomationId(button) ?? "")];

    private static bool Shows(Shell shell, string automationId) => shell.Has<Control>(automationId) && shell.Find<Control>(automationId).IsEffectivelyVisible;

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
    private static object? Info(Shell shell, string editor) => ToolTip.GetTip(InfoGlyph(shell, editor));

    private static Border InfoGlyph(Shell shell, string editor) =>
        shell.Find<Control>(editor).FindAncestorOfType<InspectorRow>()!.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("infoGlyph"));

    private static KindTile HeaderTile(Shell shell) => shell.Find<Control>("InspectorHeader").GetVisualDescendants().OfType<KindTile>().Single();
}
