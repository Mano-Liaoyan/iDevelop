using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Conversation;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>The conversation view model and view over a scripted session: paging, refreshes, sending, and answering.</summary>
public sealed class ConversationViewTests : IDisposable
{
    private static readonly AttemptRecord Latest = Record(A);
    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly ListPager _pager = new();
    private readonly FakeConversationSession _session = new(TestTasks.Design);
    private readonly List<string> _copied = [];
    private Shell? _shell;

    public ConversationViewTests()
    {
        _session.ReadPage = _pager.Read;
        _session.Attempts = [new AttemptSummary(A, null, T0, AttemptStatus.Succeeded, CodexHigh)];
        _session.Snapshot = Snapshot(Latest, Actions());
    }

    public void Dispose() => _temp.Dispose();

    private ConversationViewModel Open(ConversationState? state = null)
    {
        _shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it.")));
        var canvas = _shell.Window.ViewModel.Canvas!;
        var model = new ConversationViewModel(new ConversationTarget(canvas, TestTasks.Design), state ?? new ConversationState(), _session, text =>
        {
            _copied.Add(text);
            return Task.CompletedTask;
        });
        Settle(model);
        return model;
    }

    private static void Settle(ConversationViewModel model)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        for (var pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            while (!model.Idle.IsCompleted)
            {
                Assert.True(DateTime.UtcNow < deadline, "the conversation finished reading");
                Thread.Sleep(5);
                Dispatcher.UIThread.RunJobs();
            }
        }

        Dispatcher.UIThread.RunJobs();
    }

    private void Change(ConversationViewModel model, bool logged = true)
    {
        var snapshot = _session.Snapshot;
        _session.Snapshot = snapshot with { Revision = snapshot.Revision + 1, LogRevision = snapshot.LogRevision + (logged ? 1 : 0) };
        _session.RaiseChanged();
        Settle(model);
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
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string[] Ids(ConversationViewModel model) => [.. model.Items.Select(item => item.Id.Value)];

    [AvaloniaFact]
    public void A_500_message_history_opens_at_the_latest_message_and_pages_back_through_each_entry_once_in_a_virtualizing_list()
    {
        _pager.Rows.AddRange(Enumerable.Range(0, 500).Select(i => Said($"m{i}", i % 2 == 0 ? MessageAuthor.Person : MessageAuthor.Agent, $"Message {i}")));
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));

        Assert.Equal("m499", Ids(model)[^1]);
        Assert.Equal(50, model.Items.Count);
        Assert.True(view.IsFollowing);
        var transcript = view.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "Transcript");
        Assert.IsType<VirtualizingStackPanel>(transcript.ItemsPanelRoot);
        while (model.HasEarlier)
        {
            model.LoadEarlierCommand.Execute(null);
            Settle(model);
        }

        Assert.Equal(Enumerable.Range(0, 500).Select(i => $"m{i}"), Ids(model));
        Render();
        Assert.InRange(transcript.GetRealizedContainers().Count(), 1, 60);
    }

    [AvaloniaFact]
    public void A_streaming_message_grows_in_place_and_completes_with_the_same_row()
    {
        _pager.Rows.AddRange([Said("p1", MessageAuthor.Person, "Write the plan."), Said("m1", MessageAuthor.Agent, "Hel", MessageState.Streaming)]);
        var model = Open();
        var row = Assert.IsType<MessageItemViewModel>(model.Items[1]);
        Assert.Equal(("Hel", "Writing…"), (row.Text, row.StateLabel));

        _pager.Rows[1] = Said("m1", MessageAuthor.Agent, "Hello", MessageState.Streaming);
        Change(model, logged: false);
        Assert.Same(row, model.Items[1]);
        Assert.Equal(("Hello", "Writing…"), (row.Text, row.StateLabel));

        _pager.Rows[1] = Said("m1", MessageAuthor.Agent, "Hello", MessageState.Complete);
        Change(model);
        Assert.Same(row, model.Items[1]);
        Assert.Equal(("Hello", (string?)null), (row.Text, row.StateLabel));
    }

    [AvaloniaFact]
    public void A_queued_message_keeps_its_row_when_the_next_turn_submits_it_and_new_entries_follow()
    {
        _pager.Rows.AddRange([Said("p1", MessageAuthor.Person, "banana", MessageState.Queued)]);
        var model = Open();
        var queued = Assert.IsType<MessageItemViewModel>(Assert.Single(model.Items));
        Assert.Equal("Queued for the next turn", queued.StateLabel);

        _pager.Rows[0] = Said("p1", MessageAuthor.Person, "banana", MessageState.Submitted);
        _pager.Rows.Add(Said("m2", MessageAuthor.Agent, "Wrote banana."));
        Change(model);

        Assert.Same(queued, model.Items[0]);
        Assert.Equal(("banana", (string?)null), (queued.Text, queued.StateLabel));
        Assert.Equal(["p1", "m2"], Ids(model));
    }

    [AvaloniaFact]
    public void Stop_and_send_sends_the_draft_to_the_shown_turn_and_clears_it_only_once_accepted()
    {
        _session.Snapshot = Snapshot(Latest, Actions(stopAndSend: true));
        var model = Open();
        model.Draft = "Use the fixture";
        _session.Send = _ => new SendResult.Refused(new SendProblem.StaleTarget());

        model.StopAndSendCommand.Execute(null);
        Settle(model);
        Assert.Equal("Use the fixture", model.Draft);
        Assert.Equal("The conversation has moved to another turn.", model.Notice);

        _session.Send = _ => new SendResult.Queued();
        model.StopAndSendCommand.Execute(null);
        Settle(model);

        Assert.Equal([new FakeConversationSession.Call.Send(new TurnKey(A, 1), "Use the fixture", true, default),
            new FakeConversationSession.Call.Send(new TurnKey(A, 1), "Use the fixture", true, default)],
            _session.Calls.OfType<FakeConversationSession.Call.Send>().Select(call => call with { Ct = default }));
        Assert.Equal("", model.Draft);
        Assert.Null(model.Notice);
    }

    [AvaloniaFact]
    public void A_stale_answer_stays_as_a_draft_and_moves_into_the_composer_once_when_the_question_is_deferred()
    {
        var key = new RequestKey(new TurnKey(A, 1), "s:q7");
        var asked = new AskedQuestion("q1", "Tests", "Fixture?", [new QuestionOption("remote", "Remote", null)], false, true);
        _session.Requests[key] = new RequestRecord.Question(key, [asked], new QuestionState.Open(new RequestDeadline(T0.AddSeconds(55), T0.AddSeconds(60))));
        _pager.Rows.Add(Entry("q7", new ConversationContent.Request(key)));
        var model = Open();
        model.Draft = "Check tests";
        var request = Assert.IsType<RequestItemViewModel>(Assert.Single(model.Items));
        Assert.True(request.CanAnswer);
        request.Questions[0].Other.Text = "local";
        _session.Answer = _ => new AnswerResult(AnswerOutcome.Stale, "The request closed.");

        request.SubmitCommand.Execute(null);
        Settle(model);
        Assert.Equal("This question closed before your answer arrived. Your answer stays here and moves to your next message.", request.Status);
        Assert.Equal("Check tests", model.Draft);

        _session.Requests[key] = new RequestRecord.Question(key, [asked], new QuestionState.Closed(RequestCloseReason.Deferred, null));
        Change(model);
        Assert.Equal("Check tests\n\nFixture?\nlocal", model.Draft);
        Assert.False(request.CanAnswer);
        Change(model);
        Assert.Equal("Check tests\n\nFixture?\nlocal", model.Draft);
        var answer = Assert.Single(_session.Calls.OfType<FakeConversationSession.Call.Answer>());
        Assert.Equal((key, "q1", "local"), (answer.Key, answer.Reply.Answers.Single().QuestionId, answer.Reply.Answers.Single().Text));
    }

    [AvaloniaFact]
    public void An_earlier_attempt_reads_its_own_history_and_its_composer_points_back_to_the_current_conversation()
    {
        var latest = Record(C);
        _session.Snapshot = Snapshot(latest, Actions());
        _session.Attempts = [new AttemptSummary(A, null, T0, AttemptStatus.Succeeded, CodexHigh),
            new AttemptSummary(B, null, T0.AddMinutes(1), AttemptStatus.Cancelled, CodexHigh),
            new AttemptSummary(C, A, T0.AddMinutes(2), AttemptStatus.Succeeded, CodexHigh)];
        _session.ReadPage = call => call.Head == B
            ? new HistoryResult.Page(1, [Said("b1", MessageAuthor.Agent, "From B")], new HistoryWindow("b"), new HistoryCursor("b1"), new HistoryCursor("b1"), false, false)
            : _pager.Read(call);
        _pager.Rows.AddRange([Said("a1", MessageAuthor.Agent, "From A"), Said("c1", MessageAuthor.Agent, "From C")]);
        var model = Open();

        Assert.Equal(["Attempt 1 · Succeeded · Codex", "Attempt 2 · Cancelled · Codex", "Attempt 3 (current) · Succeeded · Codex, continues 1"],
            model.Attempts.Select(choice => choice.Label));
        Assert.Equal(["a1", "c1"], Ids(model));
        model.SelectedAttempt = model.Attempts[1];
        Settle(model);

        Assert.Equal(["b1"], Ids(model));
        Assert.True(model.IsHistorical);
        Assert.Equal("Return to current conversation to send.", model.ComposerHint);
        model.Draft = "Hello";
        Assert.False(model.SendCommand.CanExecute(null));
        model.ReturnToCurrentCommand.Execute(null);
        Settle(model);
        Assert.Equal(["a1", "c1"], Ids(model));
        Assert.True(model.SendCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void A_click_in_the_scroll_track_while_output_streams_stops_following_the_end_among_rows_of_different_heights()
    {
        _pager.Rows.AddRange(Enumerable.Range(0, 40).Select(i => Said($"m{i}", MessageAuthor.Agent,
            string.Join("\n\n", Enumerable.Repeat($"Message {i}", i % 2 == 0 ? 1 : 12)))));
        _pager.Rows.Add(Said("m40", MessageAuthor.Agent, "Streaming", MessageState.Streaming));
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
        model.Items[^1].Update(Said("m40", MessageAuthor.Agent, "Streaming\n\nmore output", MessageState.Streaming));
        Render();

        Assert.Equal((false, true), (view.IsFollowing, view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "JumpToLatest").IsVisible));
    }

    [AvaloniaFact]
    public void The_attempt_picker_keeps_showing_the_attempt_when_its_status_changes()
    {
        _session.Attempts = [new AttemptSummary(A, null, T0, AttemptStatus.Running, CodexHigh)];
        _pager.Rows.Add(Said("m1", MessageAuthor.Agent, "Hello"));
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var picker = view.GetVisualDescendants().OfType<ComboBox>().Single(box => Avalonia.Automation.AutomationProperties.GetAutomationId(box) == "AttemptPicker");
        Assert.Equal("Attempt 1 (current) · Running · Codex", Shell.TextOf(picker));

        _session.Attempts = [new AttemptSummary(A, null, T0, AttemptStatus.Succeeded, CodexHigh)];
        Change(model);
        Render();

        Assert.Equal(("Attempt 1 (current) · Succeeded · Codex", "Attempt 1 (current) · Succeeded · Codex"),
            ((picker.SelectedItem as AttemptChoice)?.Label, Shell.TextOf(picker)));
    }

    [AvaloniaFact]
    public void Prepending_an_earlier_page_keeps_the_entry_being_read_and_its_selected_text_in_place()
    {
        _pager.Rows.AddRange(Enumerable.Range(0, 70).Select(i => Said($"m{i}", MessageAuthor.Agent,
            i == 20 ? "Read the edge cases first.\n\nThen the rest." : $"Message {i} with enough words to wrap across the transcript column.")));
        var model = Open();
        using var window = new DisposableWindow(Host(model, out var view));
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "Scroller");
        var transcript = view.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "Transcript");
        var index = Ids(model).ToList().IndexOf("m20");
        var center = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2), window.Window)!.Value;
        for (var turn = 0; turn < 200 && scroller.Offset.Y > 0; turn++)
        {
            window.Window.MouseWheel(center, new Vector(0, 5));
            Render();
        }

        Assert.False(view.IsFollowing);
        var container = transcript.ContainerFromIndex(index)!;
        // A key in the transcript makes the next scroll the person's, as an arrow key does.
        scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        scroller.Offset = scroller.Offset.WithY(container.TranslatePoint(default, (Avalonia.Visual)scroller.Content!)!.Value.Y + 12);
        Render();

        var text = container.GetVisualDescendants().OfType<SelectableTextBlock>().First(block => Shown(block).StartsWith("Read the edge", StringComparison.Ordinal));
        text.SelectionStart = 9;
        text.SelectionEnd = 19;
        Assert.Equal(("m20", 12.0), (view.Anchor!.Value.Entry.Value, Math.Round(view.Anchor.Value.Offset, 1)));

        model.LoadEarlierCommand.Execute(null);
        Settle(model);
        Render();

        Assert.Equal("m0", Ids(model)[0]);
        var read = transcript.ContainerFromIndex(Ids(model).ToList().IndexOf("m20"))!;
        Assert.Equal(-12.0, Math.Round(read.TranslatePoint(default, (Avalonia.Visual)scroller.Content!)!.Value.Y - scroller.Offset.Y, 1));
        Assert.Equal(("m20", 12.0), (view.Anchor!.Value.Entry.Value, Math.Round(view.Anchor.Value.Offset, 1)));
        Assert.Equal("edge cases", text.SelectedText);
    }

    [AvaloniaFact]
    public void Card_attention_seeks_an_old_request_and_asks_the_view_to_reveal_it()
    {
        var key = new RequestKey(new TurnKey(A, 1), "s:q7");
        _session.Requests[key] = new RequestRecord.Question(key, [new AskedQuestion("q1", "", "Fixture?", [], false, true)],
            new QuestionState.Closed(RequestCloseReason.TurnEnded, null));
        _pager.Rows.AddRange(Enumerable.Range(0, 200).Select(i => i == 10 ? Entry("q7", new ConversationContent.Request(key)) : Said($"m{i}", MessageAuthor.Agent, $"Message {i}")));
        var model = Open();
        var revealed = new List<string>();
        model.RevealRequested += entry => revealed.Add(entry.Value);
        Assert.DoesNotContain("q7", Ids(model));

        var seek = model.SeekAsync(key);
        Settle(model);

        Assert.True(seek.IsCompleted);
        Assert.Equal(["q7"], revealed);
        Assert.Equal("m0", Ids(model)[0]);
        Assert.Contains("q7", Ids(model));
        Assert.Equal("The turn ended before an answer.", Assert.IsType<RequestItemViewModel>(model.Items.Single(item => item.Id.Value == "q7")).Status);
    }

    [AvaloniaFact]
    public void Reopening_pages_back_to_the_entry_the_person_last_read()
    {
        _pager.Rows.AddRange(Enumerable.Range(0, 200).Select(i => Said($"m{i}", MessageAuthor.Agent, $"Message {i}")));
        var state = new ConversationState { Anchor = (new EntryId("m120"), 30) };

        var model = Open(state);

        Assert.Equal("m100", Ids(model)[0]);
        Assert.Equal("m199", Ids(model)[^1]);
        Assert.True(model.HasEarlier);
    }

    private sealed class DisposableWindow(Window window) : IDisposable
    {
        public Window Window => window;

        public void Dispose() => window.Close();
    }
}
