using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Conversation;
using IDevelop.Engine;
using IDevelop.TestSupport;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>The conversation view model and view over a scripted conversation: paging, new lines, and the person's place.</summary>
public sealed class ConversationViewTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeConversation _conversation = new(TestTasks.Design);
    private readonly List<string> _copied = [];

    public void Dispose() => _temp.Dispose();

    private ConversationViewModel Open(ConversationState? state = null)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it.")));
        var canvas = shell.Window.ViewModel.Canvas!;
        var model = new ConversationViewModel(new ConversationTarget(canvas, TestTasks.Design), state ?? new ConversationState(), _conversation, text =>
        {
            _copied.Add(text);
            return Task.CompletedTask;
        });
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
        Assert.Equal("read src/a.cs", Assert.IsType<ActivityItemViewModel>(model.Items[3]).Line);
        var failed = Assert.IsType<MarkerItemViewModel>(model.Items[4]);
        Assert.Equal(("New session", "Failed", "The model is not available.", true),
            (Assert.IsType<MarkerItemViewModel>(model.Items[0]).Title, failed.Title, failed.Detail, failed.IsProblem));
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
        Assert.Equal((70, 12.0), (view.Anchor!.Value.Entry, Math.Round(view.Anchor.Value.Offset, 1)));

        model.LoadEarlierCommand.Execute(null);
        Render();

        Assert.Equal(0, Ids(model)[0]);
        var read = transcript.ContainerFromIndex(Ids(model).ToList().IndexOf(70))!;
        Assert.Equal(-12.0, Math.Round(read.TranslatePoint(default, (Visual)scroller.Content!)!.Value.Y - scroller.Offset.Y, 1));
        Assert.Equal((70, 12.0), (view.Anchor!.Value.Entry, Math.Round(view.Anchor.Value.Offset, 1)));
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
}
