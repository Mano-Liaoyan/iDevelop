using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using IDevelop.Desktop.Inspector;
using IDevelop.Desktop.Theme;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// The inspector's shared columns: every view ends its glyph buttons and counts at the panel's 12 px inset and its boxes
/// 64 px from the panel's edge, keeps one label column at every width, centres each row on a 28 px first line, and in an
/// inspector narrower than 440 px, where the longest model names would not fit beside their labels, puts every value under
/// its label. No text is cut short.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class InspectorLayoutTests : IDisposable
{
    private static readonly TaskId Design = TestTasks.Design;
    private static readonly TaskId Build = TestTasks.Build;
    private static readonly ExecutionSettings Codex = new(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" };

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public InspectorLayoutTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(520)]
    public void The_workflow_the_task_and_the_connection_views_share_two_right_edges(double width)
    {
        Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(
            _temp.Seed(
                TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90),
                new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)),
            _fakes.DiscoverAsync().Result);
        shell.SizeInspector(width);
        Assert.Equal(width, shell.Find<Control>("Inspector").Bounds.Width);

        Assert.Equal("actions at 12, values at 64", Edges(shell));

        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Write it.");
        Assert.True(shell.Find<Button>("RevertInstructions").IsEffectivelyVisible);
        Assert.True(shell.Find<Control>("PermissionNote").IsEffectivelyVisible);

        Assert.Equal("actions at 12, values at 64", Edges(shell));

        shell.Click(shell.ConnectionInto("Build"));

        Assert.Equal("actions at 12, values at 64", Edges(shell));
    }

    [AvaloniaTheory]
    [InlineData(280, true)]
    [InlineData(320, true)]
    [InlineData(440, false)]
    [InlineData(520, false)]
    public void A_picker_sits_under_its_label_only_in_an_inspector_narrower_than_440(double width, bool under)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));

        AssertUnder(shell, under, "TaskClient", "TaskModel", "TaskReasoning", "TaskConversation");

        shell.Window.ViewModel.Canvas!.Blueprints.Derive(BuiltInBlueprints.Implement);
        shell.Render();

        AssertUnder(shell, under, "BlueprintName", "BlueprintClient", "BlueprintConversation", "FieldKey", "FieldLabel");
        // A field's boxes and its Remove button keep to the panel's two edges.
        Assert.Equal("actions at 12, values at 64", Edges(shell));
    }

    [AvaloniaFact]
    public void An_empty_library_says_so_in_full_in_the_narrowest_inspector()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.SizeInspector(280);

        var notes = shell.Find<Control>("Palette").GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && text.Text == "None yet. Derive a blueprint to add one.").ToArray();

        Assert.Equal([0, 0], notes.Select(note => note.TextLayout.TextLines.Count(line => line.HasCollapsed)));
    }

    /// <summary>
    /// Each editor sits 4 px under its label from the panel's content edge, where the glyphs and tiles start, or sits
    /// beside it in the value column.
    /// </summary>
    private static void AssertUnder(Shell shell, bool under, params string[] ids)
    {
        foreach (var editor in ids.SelectMany(id => Shell.ById<Control>(shell.Window, id)))
        {
            var id = AutomationProperties.GetAutomationId(editor);
            var label = Label(editor.FindAncestorOfType<InspectorRow>()!);
            var (top, bottom) = (shell.Bounds(editor).Top, shell.Bounds(label).Bottom);
            Assert.True(under ? Math.Abs(top - bottom - 4) < 1 : top < bottom, $"{id} starts at {top}, and its label ends at {bottom}.");
            var content = shell.Bounds(shell.Find<Control>("Inspector")).Left + Inset;
            Assert.Equal(under ? content : shell.Bounds(label).Left + InspectorGrid.LabelWidth, shell.Bounds(editor).Left, 0.5);
        }
    }

    [AvaloniaFact]
    public void Every_labelled_row_of_a_task_leads_with_a_glyph_or_a_tile()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.Click(shell.Header(shell.Node("Design")));

        var rows = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>()
            .Where(row => row.IsEffectivelyVisible && row.Layout is not (RowLayout.Full or RowLayout.Buttons)).ToArray();

        Assert.Equal(
            ["Instructions", "Acceptance criteria", "Client", "Model", "Reasoning", "Conversation", "Type", "Version", "Description"],
            rows.Select(row => row.Label));
        Assert.All(rows, row => Assert.True(row.Icon is not null || row.Lead is not null, $"{row.Label} has no glyph."));
        Assert.Equal(
            ["IconEdit", "IconCheckmark", "IconAgent", "IconModel", "IconReasoning", "IconConversation", null, "IconVersion", "IconDescription"],
            rows.Select(row => IconKey(row.Icon)));
    }

    [AvaloniaFact]
    public void Kinds_and_blueprints_read_as_names_in_strong_text()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.Click(shell.Find<RadioButton>("ThemeLight"));

        var labels = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>()
            .Where(row => row.IsEffectivelyVisible && row.Classes.Contains("entry"))
            .Select(row => (row.Label, ((ISolidColorBrush)row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Label").Foreground!).Color))
            .ToArray();

        var strong = ((ISolidColorBrush)Application.Current!.FindResource(ThemeVariant.Light, "TextStrongBrush")!).Color;
        Assert.Equal(
            [("Implement", strong), ("Implement", strong), ("Plan", strong), ("Architect", strong), ("Review", strong), ("Approval", strong)],
            labels);
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(400)]
    [InlineData(520)]
    public void The_overview_counts_end_where_the_library_buttons_and_the_filter_button_end(double width)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90), TaskAt(Build, "Build", 465, 90)));
        shell.SizeInspector(width);
        var edge = shell.Bounds(shell.Find<Control>("Inspector")).Right - Inset;

        var counts = Shell.ById<TextBlock>(shell.Window, "KindCount").Where(count => count.IsEffectivelyVisible).ToArray();
        var more = Shell.ById<Button>(shell.Window, "BlueprintMore").Where(button => button.IsEffectivelyVisible).ToArray();

        Assert.NotEmpty(counts);
        Assert.Equal(5, more.Length);
        Assert.All(counts.Concat<Control>(more).Append(shell.Find<Button>("InspectorTools")), control => Assert.Equal(edge, shell.Bounds(control).Right, 1));
    }

    [AvaloniaFact]
    public void Kinds_blueprints_and_short_values_share_one_line_height()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.SizeInspector(520);

        var entries = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>()
            .Where(row => row.IsEffectivelyVisible && row.Classes.Contains("entry")).ToArray();
        Assert.Equal(6, entries.Length);
        Assert.All(entries, row => Assert.Equal(InspectorGrid.LineHeight, row.Bounds.Height, 0.5));
        var margins = entries.Select(row => row.Margin).ToList();

        shell.Click(shell.Header(shell.Node("Design")));

        var lines = BesideRows(shell).Where(row => row.Label is "Client" or "Model" or "Type" or "Version").ToArray();
        Assert.Equal(4, lines.Length);
        Assert.All(lines, row => Assert.Equal(InspectorGrid.LineHeight, row.Bounds.Height, 0.5));
        Assert.Single(margins.Concat(lines.Select(row => row.Margin)).Distinct());
    }

    [AvaloniaTheory]
    [InlineData(440)]
    [InlineData(480)]
    [InlineData(520)]
    public void Labels_and_values_keep_one_column_each_at_every_width_from_440(double width)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));

        Assert.Equal(["Client", "Model", "Reasoning", "Conversation", "Type", "Version"], BesideRows(shell).Select(row => row.Label));
        Assert.Equal("labels at 36, values at 124", Columns(shell));

        shell.Click(shell.ConnectionInto("Build"));

        Assert.Equal(["From", "To"], BesideRows(shell).Select(row => row.Label));
        Assert.Equal("labels at 36, values at 124", Columns(shell));
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    public void Under_440_every_labelled_value_sits_under_its_label_from_the_content_edge_to_the_value_edge(double width)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));

        AssertAllUnder(shell, ["Instructions", "Acceptance criteria", "Client", "Model", "Reasoning", "Conversation", "Type", "Version", "Description"]);

        shell.Click(shell.ConnectionInto("Build"));

        AssertAllUnder(shell, ["From", "To", "Kind"]);
    }

    [AvaloniaTheory]
    [InlineData(440)]
    [InlineData(520)]
    public void A_row_centres_its_glyph_label_value_and_buttons_on_its_first_line(double width)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));

        var rows = BesideRows(shell);
        Assert.Equal(["Client", "Model", "Reasoning", "Conversation", "Type", "Version"], rows.Select(row => row.Label));
        Assert.True(shell.Find<Control>("PermissionNote").IsEffectivelyVisible);
        foreach (var row in rows)
        {
            var line = shell.Bounds(Label(row)).Center.Y;
            var parts = row.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && Part(control) is not null).ToArray();
            Assert.Contains(parts, control => Part(control) == "value");
            Assert.All(parts, control => Assert.True(Math.Abs(shell.Bounds(control).Center.Y - line) < 1,
                $"{row.Label}'s {control.GetType().Name} centres at {shell.Bounds(control).Center.Y}, and its label at {line}."));
        }
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(520)]
    public void The_last_run_puts_its_status_agent_and_time_under_their_labels_each_on_one_line(double width)
    {
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Replay(Fixture.Path("codex-success.jsonl")));
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Say hi", 105, 90, Codex, "Reply with DONE.")), _fakes.DiscoverAsync().Result);
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the run succeeds");
        shell.Render();

        var inspector = shell.Bounds(shell.Find<Control>("Inspector"));
        foreach (var id in new[] { "LastRunStatus", "LastRunConfiguration", "LastRunTiming" })
        {
            var row = shell.Find<Control>(id).FindAncestorOfType<InspectorRow>()!;
            var (label, value) = (shell.Bounds(Label(row)), shell.Bounds(Presenter(row)));
            Assert.True(value.Top >= label.Bottom, $"{row.Label}'s value starts at {value.Top}, above its label's end at {label.Bottom}.");
            Assert.Equal(inspector.Left + Inset, value.Left, 0.5);
            var tops = row.GetVisualDescendants().OfType<Border>().Where(border => border.IsEffectivelyVisible && border.Classes.Contains("chip"))
                .Select(chip => shell.Bounds(chip).Top).Distinct().ToArray();
            Assert.True(tops.Length <= 1, $"{row.Label}'s chips take {tops.Length} lines.");
        }
    }

    /// <summary>What a control is to its row's first line: a glyph, the info glyph, or the value.</summary>
    private static string? Part(Control control) => control switch
    {
        KindTile => "glyph",
        PathIcon icon when icon.FindAncestorOfType<KindTile>() is null && icon.FindAncestorOfType<ComboBox>() is null => "glyph",
        Border border when border.Classes.Contains("infoGlyph") => "info",
        ComboBox => "value",
        TextBlock text when text.Classes.Contains("value") => "value",
        _ => null,
    };

    [AvaloniaFact]
    public void The_filter_stays_put_whether_a_task_a_connection_or_nothing_is_selected()
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex), TaskAt(Build, "Build", 465, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        var filter = shell.Find<TextBox>("InspectorFilter");
        var empty = shell.Bounds(filter).Top;

        shell.Click(shell.Header(shell.Node("Design")));
        Assert.Equal(empty, shell.Bounds(filter).Top, 0.5);

        shell.Click(shell.ConnectionInto("Build"));
        Assert.Equal(empty, shell.Bounds(filter).Top, 0.5);
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(520)]
    public void Text_and_boxes_without_a_label_end_at_the_value_edge_and_only_rows_of_buttons_reach_the_action_edge(double width)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));
        var right = shell.Bounds(shell.Find<Control>("Inspector")).Right;

        var rows = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>().Where(row => row.IsEffectivelyVisible).ToArray();
        var full = rows.Where(row => row.Layout == RowLayout.Full).ToArray();
        var buttons = rows.Where(row => row.Layout == RowLayout.Buttons).ToArray();
        Assert.Contains(shell.Find<TextBlock>("SendProblem").FindAncestorOfType<InspectorRow>(), full);
        Assert.Contains(shell.Find<TextBox>("Composer").FindAncestorOfType<InspectorRow>(), full);
        Assert.Contains(shell.Find<Button>("DeriveTaskType").FindAncestorOfType<InspectorRow>(), buttons);
        Assert.All(full, row => Assert.Equal(right - Inset - InspectorGrid.TrailWidth, shell.Bounds(Presenter(row)).Right, 0.5));
        Assert.All(buttons, row => Assert.Equal(right - Inset, shell.Bounds(Presenter(row)).Right, 0.5));
        Assert.All(full.SelectMany(row => row.GetVisualDescendants().OfType<TextBlock>()).Where(text => text.IsEffectivelyVisible),
            text => Assert.True(shell.Bounds(text).Right <= right - Inset - InspectorGrid.TrailWidth + 0.5, $"\"{text.Text}\" ends at {shell.Bounds(text).Right}."));

        // The two blueprint buttons share a line even in the narrowest panel.
        var (derive, save) = (shell.Find<Button>("DeriveTaskType"), shell.Find<Button>("SaveAsBlueprint"));
        Assert.Equal(shell.Bounds(derive).Top, shell.Bounds(save).Top);
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(520)]
    public void A_long_title_wraps_in_the_header_and_nothing_in_the_task_or_its_connection_is_cut_short(double width)
    {
        const string title = "Draft the release notes for the storage adapter and its migration";
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, title, 105, 90, Codex), TaskAt(Build, "Build", 465, 90),
            new WorkflowEdit.Connect(new ConnectionKey(Design, Build), ConnectionKind.Dependency)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node(title)));

        var header = shell.Find<Control>("InspectorHeader");
        var shown = header.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("headerTitle"));
        Assert.Equal(title, shown.Text);
        Assert.True(shown.TextLayout.TextLines.Count > 1, "The title wraps.");
        AssertNothingCutShort(shell);
        // More stays on the title's first line, at the panel's action edge.
        var more = shell.Bounds(shell.Find<Button>("InspectorMore"));
        Assert.Equal(shell.Bounds(shell.Find<Control>("Inspector")).Right - Inset, more.Right, 0.5);
        Assert.True(more.Top < shell.Bounds(shown).Top + 14, $"More starts at {more.Top}, under the title's first line at {shell.Bounds(shown).Top}.");

        shell.Click(shell.ConnectionInto("Build"));

        AssertNothingCutShort(shell);
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(440)]
    [InlineData(520)]
    public void The_longest_model_names_of_every_client_show_whole_in_their_pickers_and_chips(double width)
    {
        TaskId claude = TaskId.New(), codex = TaskId.New(), pi = TaskId.New(), agy = TaskId.New();
        Install(_fakes, ClientId.ClaudeCode);
        Install(_fakes, ClientId.Codex);
        Install(_fakes, ClientId.Pi, Fresh(ClientId.Pi).Print(SessionLine(ClientId.Pi, "s1")).Print(ReplyLines(ClientId.Pi, "Done.")));
        Install(_fakes, ClientId.Antigravity);
        var shell = Shell.Open(_temp.Seed(
            TaskAt(claude, "Claude", 105, 90, new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-sonnet-5-5", Reasoning = "high" }),
            TaskAt(codex, "Codex", 105, 190, new ExecutionSettings(ClientId.Codex) { Model = "gpt-6.1-sol", Reasoning = "high" }),
            TaskAt(pi, "Pi", 105, 290, new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-flash", Reasoning = "high" }, "Reply."),
            TaskAt(agy, "Antigravity", 105, 390, new ExecutionSettings(ClientId.Antigravity) { Model = "claude-opus-4-6-thinking" })), _fakes.DiscoverAsync().Result);
        shell.SizeInspector(width);

        foreach (var (title, model) in new[] { ("Claude", "Claude Sonnet 5.5"), ("Codex", "GPT-6.1-Sol"), ("Pi", "DeepSeek V4.1 Flash (deepseek)"), ("Antigravity", "Claude Opus 4.6 (Thinking)") })
        {
            shell.Click(shell.Header(shell.Node(title)));
            Assert.Equal((title, model), (title, shell.Picked("TaskModel")));
            AssertNothingCutShort(shell);
        }

        shell.Click(shell.Header(shell.Node("Pi")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Pi", "CardStatus") == "Succeeded", "the run succeeds");
        shell.Render();

        Assert.Equal(["Pi", "DeepSeek V4.1 Flash (deepseek)", "high"], Shell.Texts(shell.InView<ItemsControl>("LastRunConfiguration")));
        AssertNothingCutShort(shell);
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(320)]
    public void The_activity_wraps_at_the_value_edge(double width)
    {
        const string said = "I drafted the release notes. They list the new table, the migration step, and the totals that now match the ledger.";
        Install(_fakes, ClientId.Codex, Fresh(ClientId.Codex).Print(SessionLine(ClientId.Codex, "s1")).Print(ReplyLines(ClientId.Codex, said)));
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Say hi", 105, 90, Codex, "Reply.")), _fakes.DiscoverAsync().Result);
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Say hi")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Say hi", "CardStatus") == "Succeeded", "the run succeeds");
        shell.Render();

        var line = shell.InView<ItemsControl>("LastRunActivity").GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.Equal(said, line.Text);
        Assert.DoesNotContain(line.TextLayout.TextLines, text => text.HasCollapsed);
        Assert.True(line.TextLayout.TextLines.Count > 1);
        Assert.True(shell.Bounds(line).Right <= shell.Bounds(shell.Find<Control>("Inspector")).Right - Inset - InspectorGrid.TrailWidth + 0.5);
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(520)]
    public void The_scroll_bar_keeps_to_the_right_inset_so_nothing_sits_under_it(double width)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(Design, "Design", 105, 90, Codex)));
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Click(shell.Find<TextBox>("TaskInstructions"));
        shell.Type("Write it.");
        var right = shell.Bounds(shell.Find<Control>("Inspector")).Right;

        // A scroll bar lays out at its expanded width and only draws thinner while collapsed, so its bounds are where it
        // expands to, and it overlays the content, which never moves for it.
        var bar = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<ScrollBar>()
            .Single(scroll => scroll.IsEffectivelyVisible && scroll.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.Equal(right, shell.Bounds(bar).Right, 0.5);
        Assert.True(shell.Bounds(bar).Left >= right - Inset + 2, $"The bar starts {right - shell.Bounds(bar).Left} px from the edge.");
        Assert.True(shell.Bounds(shell.Find<Button>("RevertInstructions")).Right <= shell.Bounds(bar).Left - 2);
    }

    private const double Inset = 12;

    private static TextBlock Label(InspectorRow row) => row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Label");

    /// <summary>No text the inspector shows ends in an ellipsis or runs past its box, a picker's choice and a chip included.</summary>
    private static void AssertNothingCutShort(Shell shell)
    {
        var cut = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && text.FindAncestorOfType<TextBox>() is null
                && (text.TextLayout.TextLines.Any(line => line.HasCollapsed) || text.TextLayout.Width > text.Bounds.Width + 0.5))
            .Select(text => text.Text).ToArray();
        Assert.Empty(cut);
    }

    private static ContentPresenter Presenter(InspectorRow row) =>
        row.GetVisualDescendants().OfType<ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter");

    /// <summary>The rows the inspector shows now whose label sits beside their value.</summary>
    private static InspectorRow[] BesideRows(Shell shell) =>
    [
        .. shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>()
            .Where(row => row.IsEffectivelyVisible && row.Layout == RowLayout.Columns),
    ];

    /// <summary>The distinct left insets, in px from the inspector's edge, of the labels and the values that sit beside them.</summary>
    private static string Columns(Shell shell)
    {
        var left = shell.Bounds(shell.Find<Control>("Inspector")).Left;
        var rows = BesideRows(shell);
        string Lefts(IEnumerable<Visual> visuals) => string.Join(" and ", visuals.Select(visual => Math.Round(shell.Bounds(visual).Left - left, 1)).Distinct().Order());
        return $"labels at {Lefts(rows.Select(Label))}, values at {Lefts(rows.Select(Presenter))}";
    }

    /// <summary>Each labelled row puts its label on a line of its own, and its value under it, from the content edge to the value edge.</summary>
    private static void AssertAllUnder(Shell shell, string[] labels)
    {
        var inspector = shell.Bounds(shell.Find<Control>("Inspector"));
        var rows = shell.Find<Control>("Inspector").GetVisualDescendants().OfType<InspectorRow>()
            .Where(row => row.IsEffectivelyVisible && row.Layout is not (RowLayout.Full or RowLayout.Buttons or RowLayout.Title)).ToArray();
        Assert.Equal(labels, rows.Select(row => row.Label));
        foreach (var row in rows)
        {
            var (label, value) = (shell.Bounds(Label(row)), shell.Bounds(Presenter(row)));
            Assert.True(value.Top >= label.Bottom, $"{row.Label}'s value starts at {value.Top}, above its label's end at {label.Bottom}.");
            Assert.Equal((inspector.Left + Inset, inspector.Right - Inset - InspectorGrid.TrailWidth), (Math.Round(value.Left, 1), Math.Round(value.Right, 1)));
        }
    }

    /// <summary>
    /// The distinct right insets, in px from the inspector's edge, of the actions and of the values it shows now. A row's
    /// actions are its trailing glyphs, such as a revert arrow and an info glyph, Place and More, or a kind's count, which
    /// end together.
    /// </summary>
    private static string Edges(Shell shell)
    {
        var inspector = shell.Find<Control>("Inspector");
        var right = shell.Bounds(inspector).Right;
        var shown = inspector.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0).ToArray();
        bool InRow(Control control) => control.FindAncestorOfType<InspectorRow>() is not null;
        string? Id(Control control) => AutomationProperties.GetAutomationId(control);

        var actions = shown.Where(control => Id(control) is "InspectorMore" or "InspectorTools" or "KindCount" || control.Name == "PART_Trail");
        var values = shown.Where(control =>
            control is ComboBox
            || control is TextBox && (Id(control) == "InspectorFilter" || InRow(control))
            || control is Button { Classes: var classes } && classes.Contains("rowLink"));

        string Insets(IEnumerable<Control> controls) =>
            string.Join(" and ", controls.Select(control => Math.Round(right - shell.Bounds(control).Right, 1)).Distinct().Order());
        return $"actions at {Insets(actions)}, values at {Insets(values)}";
    }

    private static string? IconKey(Geometry? icon) =>
        icon is null ? null
        : new[] { "IconEdit", "IconCheckmark", "IconAgent", "IconModel", "IconReasoning", "IconConversation", "IconVersion", "IconDescription", "IconField" }
            .SingleOrDefault(key => Application.Current!.TryGetResource(key, null, out var resource) && ReferenceEquals(resource, icon));
}
