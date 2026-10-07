using System.Net;
using System.Net.Sockets;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Conversation;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>The message renderer: what each block shows, what it never does with untrusted text, and what it rebuilds.</summary>
public sealed class MarkdownViewTests
{
    private readonly List<Uri> _launched = [];
    private readonly List<string> _confirmed = [];
    private readonly List<string> _copied = [];

    private (Window Window, MarkdownView View) Show(string markdown, bool confirm = true)
    {
        var view = new MarkdownView { Markdown = markdown };
        var window = new Window { Width = 700, Height = 900, Content = view };
        ConversationLinkRouter.SetRouter(window, new ConversationLinkRouter(
            uri =>
            {
                _launched.Add(uri);
                return Task.FromResult(true);
            },
            text =>
            {
                _copied.Add(text);
                return Task.CompletedTask;
            },
            (_, web) =>
            {
                _confirmed.Add(web.Uri.AbsoluteUri);
                return Task.FromResult(confirm);
            }));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static Button Link(MarkdownView view, string label) =>
        view.GetVisualDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: var text } && text == label);

    private static void Invoke(Button button)
    {
        ControlAutomationPeer.CreatePeerForElement(button).GetProvider<IInvokeProvider>()!.Invoke();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Headings_lists_quotes_code_and_tables_render_as_their_text()
    {
        var (window, view) = Show("# Plan\n\nSome *emphasis* and **strong** and `code`.\n\n1. First\n2. Second\n\n> Quoted\n\n```csharp\nvar x = 1;\n```\n\n| A | B |\n| - | - |\n| 1 | 2 |\n");

        Assert.Equal(["Plan", "Some emphasis and strong and code.", "First", "Second", "Quoted", "var x = 1;", "A", "B", "1", "2"], Blocks(view));
        Assert.Equal(["1.", "2.", "csharp"], view.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text is not SelectableTextBlock && text.Classes.Contains("mdMarker") || text.Classes.Contains("mdCodeLanguage")).Select(text => text.Text));
        window.Close();
    }

    [AvaloniaFact]
    public void Html_stays_literal_an_image_is_never_fetched_and_a_script_link_never_launches()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var (window, view) = Show($"<script>x()</script>\n\nSee ![diagram](http://127.0.0.1:{port}/diagram.png) and [run it](javascript:alert(1)).");
            Thread.Sleep(300);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["<script>x()</script>", $"See [image: diagram] http://127.0.0.1:{port}/diagram.png and run it."], Blocks(view));
            Assert.Empty(view.GetVisualDescendants().OfType<Image>());
            Assert.False(listener.Pending());
            Invoke(Link(view, "run it"));
            Assert.Empty(_confirmed);
            Assert.Empty(_launched);
            window.Close();
        }
        finally
        {
            listener.Stop();
        }
    }

    [AvaloniaFact]
    public void A_web_link_launches_only_its_confirmed_address_and_its_context_menu_copies_it()
    {
        var (window, view) = Show("Read [the guide](https://example.com/guide?x=1).", confirm: false);
        var link = Link(view, "the guide");

        Invoke(link);
        Assert.Equal(["https://example.com/guide?x=1"], _confirmed);
        Assert.Empty(_launched);

        window.Close();
        (window, view) = Show("Read [the guide](https://example.com/guide?x=1).");
        link = Link(view, "the guide");
        Invoke(link);
        ((MenuItem)link.ContextMenu!.Items[1]!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([new Uri("https://example.com/guide?x=1")], _launched);
        Assert.Equal(["https://example.com/guide?x=1"], _copied);
        window.Close();
    }

    [AvaloniaFact]
    public void Markdown_nested_too_deeply_for_Markdig_shows_as_its_source()
    {
        var deep = new string('>', 200) + " quoted";
        var (window, view) = Show("First message.");

        view.Markdown = deep;

        Assert.Equal([deep], Blocks(view));
        view.Markdown = "Next message.";
        Assert.Equal(["Next message."], Blocks(view));
        window.Close();
    }

    [AvaloniaFact]
    public void A_message_past_the_cell_link_or_length_limit_shows_as_its_source()
    {
        static string Table(int rows) => "| a | b | c | d | e | f | g | h | i | j |\n|-|-|-|-|-|-|-|-|-|-|\n"
            + string.Concat(Enumerable.Range(0, rows).Select(row => "| x | x | x | x | x | x | x | x | x | x |\n"));
        static string Links(int count) => string.Join(" ", Enumerable.Range(0, count).Select(i => $"[l{i}](https://example.com/{i})"));
        var (window, view) = Show("");
        (int Cells, int Links, int Blocks) Shown(string markdown)
        {
            view.Markdown = markdown;
            window.UpdateLayout();
            return (view.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("mdCell")),
                view.GetVisualDescendants().OfType<Button>().Count(button => button.Classes.Contains("mdLink")), Blocks(view).Length);
        }

        Assert.Equal((1000, 0, 1000), Shown(Table(99)));
        Assert.Equal((0, 0, 1), Shown(Table(100)));
        Assert.Equal((0, 500, 1), Shown(Links(500)));
        Assert.Equal((0, 0, 1), Shown(Links(501)));
        Assert.Equal((0, 0, 2), Shown("Short.\n\n" + new string('w', 19_992)));
        var longer = "Short.\n\n" + new string('w', 19_993);
        Assert.Equal((0, 0, 1), Shown(longer));
        Assert.Equal((longer, 57.0), (Blocks(view).Single(), view.Children[0].Bounds.Height));
        window.Close();
    }

    [AvaloniaFact]
    public void A_message_shown_as_its_source_streams_into_the_same_block_and_waits_while_its_text_is_selected()
    {
        var source = "| a |\n|-|\n" + string.Concat(Enumerable.Repeat("| x |\n", 1000));
        var (window, view) = Show(source);
        var shown = (SelectableTextBlock)view.Children[0];
        shown.SelectionStart = 0;
        shown.SelectionEnd = 5;

        view.Markdown = source + "| y |\n";
        Assert.Equal((source, 1), (Blocks(view).Single(), view.BlocksBuilt));
        shown.SelectionEnd = 0;

        Assert.Same(shown, view.Children[0]);
        Assert.Equal((source + "| y |\n", 1), (Blocks(view).Single(), view.BlocksBuilt));
        window.Close();
    }

    [Fact]
    public void A_confirmation_shows_a_look_alike_host_as_the_browser_sends_it() =>
        Assert.Equal("https://аpple.com/login\nHost as sent: xn--pple-43d.com", ConversationLinkRouter.Shown(new Uri("https://аpple.com/login")));

    [AvaloniaFact]
    public void Streaming_rebuilds_only_the_changed_last_block_and_waits_while_its_text_is_selected()
    {
        var (window, view) = Show("First paragraph.\n\nSecond");
        Assert.Equal(2, view.BlocksBuilt);

        view.Markdown = "First paragraph.\n\nSecond line grows";
        Assert.Equal(3, view.BlocksBuilt);
        var last = (SelectableTextBlock)view.Children[1];
        last.SelectionStart = 0;
        last.SelectionEnd = 6;
        view.Markdown = "First paragraph.\n\nSecond line grows more";

        Assert.Same(last, view.Children[1]);
        Assert.Equal(["First paragraph.", "Second line grows"], Blocks(view));
        last.SelectionEnd = 0;
        Assert.Equal(["First paragraph.", "Second line grows more"], Blocks(view));
        Assert.Equal(4, view.BlocksBuilt);
        window.Close();
    }
}
