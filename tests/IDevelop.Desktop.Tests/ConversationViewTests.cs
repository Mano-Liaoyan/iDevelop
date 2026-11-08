using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Theme;
using IDevelop.Engine;
using IDevelop.TestSupport;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>The conversation view model and view over a scripted conversation: paging, new lines, and the person's place.</summary>
public sealed class ConversationViewTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeConversation _conversation = new();

    public void Dispose() => _temp.Dispose();

    private ConversationViewModel Open(ConversationState? state = null)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it.")));
        var canvas = shell.Window.ViewModel.Canvas!;
        var model = new ConversationViewModel(new ConversationTarget(canvas, TestTasks.Design), state ?? new ConversationState(), _conversation, _ => Task.CompletedTask);
        Render();
        return model;
    }

    private static Window Host(ConversationViewModel model, out ConversationView view)
    {
        view = new ConversationView { DataContext = model };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Render();
        return window;
    }

    private static void Render()
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static int[] Ids(ConversationViewModel model) => [.. model.Items.Select(item => item.Id)];

    private static ConversationLine Said(int i) => i % 2 == 0 ? new ConversationLine.Person($"Message {i}") : new ConversationLine.Agent($"Message {i}");

    [AvaloniaFact]
    public void Prose_keeps_one_reading_measure_in_lists_and_quotes_and_a_table_keeps_its_width()
    {
        _conversation.Add(new ConversationLine.Agent("Plain.\n\n- A list item.\n\n> A quote.\n\n| A |\n| --- |\n| Cell |"));
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));

        var texts = view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(text => text.Classes.Contains("md")).ToList();
        var cells = texts.Where(text => text.GetVisualParent() is Border { Classes: var classes } && classes.Contains("mdCell")).ToList();
        Assert.Equal(2, cells.Count);
        Assert.All(cells, cell => Assert.True(double.IsPositiveInfinity(cell.MaxWidth)));
        var prose = texts.Except(cells).ToList();
        Assert.Equal(3, prose.Count);
        Assert.All(prose, text => Assert.Equal(520, text.MaxWidth));
    }

    [AvaloniaFact]
    public void A_500_line_history_opens_at_the_latest_line_and_pages_back_through_each_line_once_in_a_virtualizing_list()
    {
        _conversation.Add([.. Enumerable.Range(0, 500).Select(Said)]);
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));

        Assert.Equal(499, Ids(model)[^1]);
        Assert.Equal(ConversationViewModel.PageSize, model.Items.Count);
        Assert.True(view.IsFollowing);
        var transcript = view.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "Transcript");
        Assert.IsType<VirtualizingStackPanel>(transcript.ItemsPanelRoot);
        while (model.HasEarlier)
        {
            model.LoadEarlierCommand.Execute(null);
            Render();
        }

        Assert.Equal(Enumerable.Range(0, 500), Ids(model));
        Render();
        Assert.InRange(transcript.GetRealizedContainers().Count(), 1, 60);
    }

    [AvaloniaFact]
    public void New_lines_join_at_the_end_and_the_rows_shown_keep_their_items()
    {
        _conversation.Add(new ConversationLine.Marker(MarkerKind.Fresh, null), new ConversationLine.Agent("Which file?\n\n```idevelop\n{\"status\": \"asking\", \"question\": \"Which file?\"}\n```"));
        var model = Open();
        var first = model.Items.ToArray();

        _conversation.Add(new ConversationLine.Person("The fixture."), new ConversationLine.Activity("read src/a.cs", IsTool: true),
            new ConversationLine.Marker(MarkerKind.Failed, "The model is not available."));
        Render();

        Assert.Equal([0, 1, 2, 3, 4], Ids(model));
        Assert.Same(first[1], model.Items[1]);
        var agent = Assert.IsType<MessageItemViewModel>(model.Items[1]);
        Assert.Equal(("Codex", "Which file?", "Asks: Which file?"), (agent.AuthorLabel, agent.Text, agent.ResultNote));
        Assert.Equal("You", Assert.IsType<MessageItemViewModel>(model.Items[2]).AuthorLabel);
        Assert.Equal("read src/a.cs", Assert.IsType<StepGroupItemViewModel>(model.Items[3]).LastLine);
        var failed = Assert.IsType<MarkerItemViewModel>(model.Items[4]);
        Assert.Equal(("New session", "Failed", "The model is not available.", true),
            (Assert.IsType<MarkerItemViewModel>(model.Items[0]).Title, failed.Title, failed.Detail, failed.IsProblem));
    }

    [AvaloniaFact]
    public void Tool_lines_fold_into_one_group_that_takes_the_next_tool_line_and_a_notice_stands_alone()
    {
        _conversation.Add(new ConversationLine.Agent("Reading."), new ConversationLine.Activity("Read a.cs\nall of it", IsTool: true), new ConversationLine.Activity("Bash ls", IsTool: true));
        var model = Open();
        var group = Assert.IsType<StepGroupItemViewModel>(model.Items[1]);
        Assert.True(model.Items[0].IsBeforeSteps);

        _conversation.Add(new ConversationLine.Activity("Bash test", IsTool: true), new ConversationLine.Activity("Rate limit reached.", IsTool: false), new ConversationLine.Activity("Bash again", IsTool: true));
        Render();

        Assert.Same(group, model.Items[1]);
        Assert.False(group.IsOpen);
        Assert.Equal(("3 steps", "Bash test"), (group.CountLabel, group.LastLine));
        Assert.Equal(["Read a.cs", "Bash ls", "Bash test"], group.Steps.Select(step => step.Line));
        Assert.Equal("Rate limit reached.", Assert.IsType<ActivityItemViewModel>(model.Items[2]).Line);
        Assert.Equal("1 step", Assert.IsType<StepGroupItemViewModel>(model.Items[3]).CountLabel);
    }

    [AvaloniaFact]
    public void Left_and_Right_fold_and_open_a_group_of_steps()
    {
        _conversation.Add(new ConversationLine.Activity("Read a.cs", IsTool: true));
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var group = Assert.IsType<StepGroupItemViewModel>(model.Items[0]);
        var row = view.GetVisualDescendants().OfType<ToggleButton>().Last(button => button.Classes.Contains("steps"));

        row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = row });
        Assert.True(group.IsOpen);
        row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left, Source = row });
        Assert.False(group.IsOpen);
    }

    [AvaloniaFact]
    public void An_author_names_only_the_first_agent_line_after_a_person_or_a_marker_and_every_subagent_line()
    {
        _conversation.Add(
            new ConversationLine.Marker(MarkerKind.Fresh, null),
            new ConversationLine.Agent("One."),
            new ConversationLine.Activity("Read a.cs", IsTool: true),
            new ConversationLine.Agent("Two."),
            new ConversationLine.Agent("Inner.") { Subagent = "explore" },
            new ConversationLine.Agent("Three."),
            new ConversationLine.Person("Go on."),
            new ConversationLine.Agent("Four."));
        var model = Open();

        Assert.Equal(
            [("Codex", true), ("Codex", false), ("Subagent: explore", true), ("Codex", true), ("You", true), ("Codex", true)],
            model.Items.OfType<MessageItemViewModel>().Select(message => (message.AuthorLabel, message.ShowsAuthor)));
    }

    [AvaloniaFact]
    public void A_session_start_counts_its_attempt_and_a_wait_leaves_out_what_the_message_above_said()
    {
        _conversation.Add(
            new ConversationLine.Marker(MarkerKind.Fresh, null),
            new ConversationLine.Agent("Which file?"),
            new ConversationLine.Marker(MarkerKind.Waiting, "Which file?"),
            new ConversationLine.Marker(MarkerKind.Fresh, null),
            new ConversationLine.Marker(MarkerKind.Waiting, NodeState.Reply));
        var model = Open();
        var markers = model.Items.OfType<MarkerItemViewModel>().ToArray();

        Assert.Equal(["Attempt 1 · New session", "Waiting for you", "Attempt 2 · New session", "Waiting for you"], markers.Select(marker => marker.Heading));
        Assert.Equal(["", ""], new[] { markers[1], markers[3] }.Select(marker => marker.Detail));
    }

    [AvaloniaFact]
    public void A_node_that_has_not_run_offers_its_Run_until_a_line_arrives()
    {
        var model = Open();
        Assert.True(model.ShowsEmpty);

        _conversation.Add(new ConversationLine.Marker(MarkerKind.Fresh, null));
        Render();

        Assert.False(model.ShowsEmpty);
    }

    [Theory]
    [InlineData(new double[] { 40, 300, 300 }, 340, 150)]
    [InlineData(new double[] { 40, 100 }, 400, double.PositiveInfinity)]
    public void A_row_takes_width_from_its_widest_children_first(double[] widths, double room, double cap) =>
        Assert.Equal(cap, FairRow.Cap(widths, room));

    [AvaloniaFact]
    public void The_task_opens_the_transcript_and_a_finished_turn_keeps_no_closing_line()
    {
        _conversation.Add([new ConversationLine.Marker(MarkerKind.Fresh, null), new ConversationLine.Agent("Done.")]);
        var model = Open();
        var fields = model.Node.Fields.Where(field => field.Text.Length > 0).ToList();

        Assert.Equal(fields.Count > 0, model.ShowsTask);
        Assert.Equal(fields.FirstOrDefault()?.Text.Split('\n')[0], model.TaskPreview);
    }

    [AvaloniaTheory]
    [InlineData(678, 2)]
    [InlineData(356, 1)]
    public void The_strip_shows_the_chips_that_fit_and_the_rest_behind_its_more_chip(double width, int shown)
    {
        var other = IDevelop.Workflows.TaskId.New();
        var shell = Shell.Open(_temp.Seed(
            TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it."),
            TaskAt(TestTasks.Build, "A", 405, 90), TaskAt(TestTasks.Review, "B", 405, 190), TaskAt(other, "C", 405, 290),
            NodeFixture.Dependency(TestTasks.Design, TestTasks.Build), NodeFixture.Dependency(TestTasks.Design, TestTasks.Review), NodeFixture.Dependency(TestTasks.Design, other)));
        var canvas = shell.Window.ViewModel.Canvas!;
        var model = new ConversationViewModel(new ConversationTarget(canvas, TestTasks.Design), new ConversationState(), _conversation, _ => Task.CompletedTask);
        model.FitChips(width);

        Assert.Equal((shown, 3 - shown, $"+{3 - shown}"), (model.ShownOutputs.Count, model.MoreOutputs.Count, model.MoreOutputsLabel));
    }

    [AvaloniaFact]
    public void Text_selected_in_a_row_stays_selected_while_new_lines_refresh_the_view()
    {
        _conversation.Add([.. Enumerable.Range(0, 250).Select(Said)]);
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var row = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(text => Shown(text) == "Message 248");
        row.SelectionStart = 0;
        row.SelectionEnd = 7;

        _conversation.Add(new ConversationLine.Person("Later."));
        Render();

        Assert.True(row.IsEffectivelyVisible);
        Assert.Same(row, view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(text => Shown(text) == "Message 248"));
        Assert.Equal("Message", row.SelectedText);
    }

    [AvaloniaFact]
    public void Closing_the_conversation_lets_go_of_its_log()
    {
        var model = Open();
        model.Dispose();
        _conversation.Add(new ConversationLine.Person("Later."));
        Render();

        Assert.True(_conversation.Disposed);
        Assert.Empty(model.Items);
    }

    [AvaloniaFact]
    public void A_click_in_the_scroll_track_while_output_streams_stops_following_the_end_among_rows_of_different_heights()
    {
        _conversation.Add([.. Enumerable.Range(0, 40).Select(i => new ConversationLine.Agent(string.Join("\n\n", Enumerable.Repeat($"Message {i}", i % 2 == 0 ? 1 : 12))))]);
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Scroller");
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
        var track = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, 24), window.Window)!.Value;
        window.Window.MouseMove(track);
        Render();
        Assert.True(view.IsFollowing);

        // Rows of different heights come into view, so the scroll that the click makes also changes the extent.
        window.Window.MouseDown(track, MouseButton.Left);
        window.Window.MouseUp(track, MouseButton.Left);
        _conversation.Add(new ConversationLine.Agent("More output."));
        Render();

        Assert.Equal((false, true), (view.IsFollowing, view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "JumpToLatest").IsVisible));
    }

    [AvaloniaFact]
    public void Prepending_an_earlier_page_keeps_the_line_being_read_and_its_selected_text_in_place()
    {
        _conversation.Add([.. Enumerable.Range(0, 270).Select(i => new ConversationLine.Agent(
            i == 70 ? "Read the edge cases first.\n\nThen the rest." : $"Message {i} with enough words to wrap across the transcript column."))]);
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Scroller");
        var transcript = view.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "Transcript");
        var index = Ids(model).ToList().IndexOf(70);
        var center = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2), window.Window)!.Value;
        for (var turn = 0; turn < 400 && scroller.Offset.Y > 0; turn++)
        {
            window.Window.MouseWheel(center, new Vector(0, 5));
            Render();
        }

        Assert.False(view.IsFollowing);
        var container = transcript.ContainerFromIndex(index)!;
        // A key in the transcript makes the next scroll the person's, as an arrow key does.
        scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        scroller.Offset = scroller.Offset.WithY(container.TranslatePoint(default, (Visual)scroller.Content!)!.Value.Y + 12);
        Render();

        var text = container.GetVisualDescendants().OfType<SelectableTextBlock>().First(block => Shown(block).StartsWith("Read the edge", StringComparison.Ordinal));
        text.SelectionStart = 9;
        text.SelectionEnd = 19;
        // The view rests on a whole line, so the place is a line's top in the entry.
        var place = Math.Round(view.Anchor!.Value.Offset, 1);
        Assert.Equal(70, view.Anchor.Value.Entry);

        model.LoadEarlierCommand.Execute(null);
        Render();

        Assert.Equal(0, Ids(model)[0]);
        var read = transcript.ContainerFromIndex(Ids(model).ToList().IndexOf(70))!;
        // The entry stays in place to a line, because a page before it can change the author line it starts with.
        Assert.InRange(Math.Round(scroller.Offset.Y - read.TranslatePoint(default, (Visual)scroller.Content!)!.Value.Y, 1), place - 24, place);
        Assert.Equal(70, view.Anchor!.Value.Entry);
        Assert.Equal("edge cases", text.SelectedText);
    }

    [AvaloniaFact]
    public void Reopening_starts_at_the_line_the_person_last_read()
    {
        _conversation.Add([.. Enumerable.Range(0, 400).Select(Said)]);
        var state = new ConversationState { Anchor = (120, 30) };

        var model = Open(state);

        Assert.Equal(120, Ids(model)[0]);
        Assert.Equal(399, Ids(model)[^1]);
        Assert.True(model.HasEarlier);
    }

    private sealed class DisposableWindow(Window window) : IDisposable
    {
        public Window Window => window;

        public void Dispose() => window.Close();
    }

    [AvaloniaFact]
    public void A_view_at_rest_never_cuts_a_line_at_its_top_edge()
    {
        _conversation.Add([.. Enumerable.Range(0, 60).Select(i => new ConversationLine.Agent($"Message {i} with enough words to wrap across the transcript column, and then a few more words so that it runs onto its second line and a third."))]);
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Scroller");
        Render();
        void Check()
        {
            var y = scroller.Offset.Y;
            Assert.Equal(y, WholeLine.Rest((Control)scroller.Content!, y, down: false), 1);
        }

        Check();
        foreach (var step in new[] { 37.0, 113.0, 251.0, 9.0 })
        {
            scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
            scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y - step);
            Render();
            Check();
        }
    }

    [AvaloniaFact]
    public void A_small_step_down_moves_past_the_line_so_arrows_keep_moving()
    {
        _conversation.Add([.. Enumerable.Range(0, 60).Select(i => new ConversationLine.Agent($"Message {i} with enough words to wrap across the transcript column, and then a few more words so that it runs onto its second line and a third."))]);
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Scroller");
        scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
        scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y - 400);
        Render();
        var start = scroller.Offset.Y;

        scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        scroller.Offset = scroller.Offset.WithY(start + 4);
        Render();

        Assert.True(scroller.Offset.Y > start, $"The view stayed at {start}.");
    }

    [AvaloniaFact]
    public void A_message_without_a_result_shows_no_empty_card()
    {
        _conversation.Add([new ConversationLine.Agent("A plain message.")]);
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        Render();

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("resultCard") && border.IsEffectivelyVisible);
    }
}
