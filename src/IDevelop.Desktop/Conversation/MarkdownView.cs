using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// One message's Markdown as Avalonia controls. HTML stays literal text, an image shows its description and address and
/// is never fetched, and a link goes only to the <see cref="ConversationLinkRouter"/>. A row's text never changes, so a new
/// text means a recycled row, and the view rebuilds its blocks. A message too long, too deeply nested, or with too many
/// blocks, table cells, or links to lay out quickly shows as its source.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    // Measured headlessly at each limit, 1,000 table cells or 500 links lay out in about 165 ms, and 20,000 characters of
    // nested brackets in about 530 ms. A code block costs about ten paragraphs, and 1,000 paragraphs or 100 code blocks
    // lay out in about 120 ms.
    private const int MaxLength = 20_000;
    private const int MaxCells = 1_000;
    private const int MaxLinks = 500;
    private const int MaxBlockWeight = 1_000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().DisableHtml().Build();

    // A long code span breaks after these, at a soft hyphen, and not at a letter in mid-word. The shaper gives a soft hyphen no
    // width and no ink, and a copy leaves it out. A zero-width space would not do: a mono font without its glyph moves it to a
    // run of its own, and the line breaker looks for a break only inside a run.
    private const char BreakHere = '\u00AD';
    private const int LongSpan = 24;

    // A span this long takes a line or more by itself, so it leaves the paragraph.
    private const int BlockSpan = 60;
    private static readonly char[] SpanBreaks = ['/', '\\', '_', '.', ',', ':', '=', '-'];

    public MarkdownView()
    {
        Spacing = 8;
        AddHandler(SelectableTextBlock.CopyingToClipboardEvent, OnCopying);
    }

    private static async void OnCopying(object? sender, RoutedEventArgs e)
    {
        if (e.Source is SelectableTextBlock { SelectedText: { } text } block && text.Contains(BreakHere) && TopLevel.GetTopLevel(block)?.Clipboard is { } clipboard)
        {
            e.Handled = true;
            await clipboard.SetTextAsync(text.Replace(BreakHere.ToString(), ""));
        }
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Render();
        }
    }

    private void Render()
    {
        var text = Markdown ?? "";
        Children.Clear();
        if (Parse(text) is { } blocks)
        {
            Children.AddRange(blocks.Select(block => Block(block, text)));
            // A heading that opens a message has nothing above it to keep clear of.
            if (Children.FirstOrDefault() is SelectableTextBlock { Classes: var classes } && classes.Any(name => name.Length == 2 && name[0] == 'h'))
            {
                classes.Add("first");
            }
        }
        else
        {
            Children.Add(Plain(text));
        }
    }

    private static List<Block>? Parse(string text)
    {
        if (text.Length > MaxLength)
        {
            return null;
        }

        MarkdownDocument document;
        try
        {
            document = Markdig.Markdown.Parse(text, Pipeline);
        }
        catch (ArgumentException)
        {
            // Markdig refuses input nested too deeply.
            return null;
        }

        return document.Descendants<Markdig.Syntax.Inlines.Inline>().Where(inline => inline is LinkInline { IsImage: false } or AutolinkInline).Skip(MaxLinks).Any()
            || document.Descendants<TableCell>().Skip(MaxCells).Any()
            || Weight(document) > MaxBlockWeight
            ? null
            : [.. document.Where(block => block is not LinkReferenceDefinitionGroup)];
    }

    // A table's cells have their own limit.
    private static int Weight(ContainerBlock container) => container.Sum(block => block switch
    {
        CodeBlock => 10,
        Table => 1,
        ContainerBlock inner => 1 + Weight(inner),
        _ => 1,
    });

    private static string Source(string text, Block block)
    {
        var start = Math.Clamp(block.Span.Start, 0, text.Length);
        return text.Substring(start, Math.Clamp(block.Span.Length, 0, text.Length - start));
    }

    private Control Block(Block block, string text) => block switch
    {
        HeadingBlock heading => Text(heading.Inline, $"h{Math.Min(heading.Level, 4)}"),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline),
        FencedCodeBlock fenced => Code(fenced.Lines.ToString(), fenced.Info),
        CodeBlock code => Code(code.Lines.ToString(), null),
        ListBlock list => List(list, text),
        QuoteBlock quote => new Border { Classes = { "mdQuote" }, Child = Container(quote, text) },
        Table table => TableGrid(table),
        ThematicBreakBlock => new Border { Classes = { "mdRule" } },
        ContainerBlock container => Container(container, text),
        _ => Plain(Source(text, block)),
    };

    // A code span too long for a line stands as its own block between the paragraph's text, so it stays whole and the text reads on.
    private Control Paragraph(ContainerInline? inline)
    {
        var parts = inline?.Where(child => child is CodeInline { Content.Length: >= BlockSpan }).ToList() ?? [];
        if (parts.Count == 0)
        {
            return Text(inline, null);
        }

        var panel = new StackPanel { Spacing = 8 };
        List<Markdig.Syntax.Inlines.Inline> run = [];
        void Flush()
        {
            // The full stop after a span would stand alone under the block, so a run with no word of its own leaves with it.
            if (run.Any(child => child is not LiteralInline literal || literal.Content.ToString().Any(char.IsLetterOrDigit)) && !run.All(child => child is LineBreakInline))
            {
                var text = Text(null, null);
                run.ForEach(child => Add(text.Inlines!, child, text));
                panel.Children.Add(text);
            }

            run.Clear();
        }

        foreach (var child in inline!)
        {
            if (child is CodeInline { Content.Length: >= BlockSpan } code)
            {
                Flush();
                panel.Children.Add(Code(code.Content, null));
            }
            else
            {
                run.Add(child);
            }
        }

        Flush();
        return panel;
    }

    private StackPanel Container(ContainerBlock container, string text)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var child in container.Where(child => child is not LinkReferenceDefinitionGroup))
        {
            panel.Children.Add(Block(child, text));
        }

        return panel;
    }

    private Grid List(ListBlock list, string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 4 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Control marker = item.Descendants<TaskList>().FirstOrDefault() is { } task && item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList }
                ? TaskBox(task.Checked)
                : new TextBlock { Classes = { "mdMarker" }, Text = list.IsOrdered ? $"{number++}." : "•" };
            var body = Container(item, text);
            Grid.SetRow(marker, row);
            Grid.SetRow(body, row);
            Grid.SetColumn(body, 1);
            grid.Children.Add(marker);
            grid.Children.Add(body);
        }

        return grid;
    }

    private static Border TaskBox(bool done)
    {
        var box = new Border { Classes = { "mdTask" } };
        box.Classes.Set("done", done);
        AutomationProperties.SetName(box, done ? "Done" : "Not done");
        if (done)
        {
            box.Child = new PathIcon { Theme = Glyph(), Data = Icon("IconCheckmark") };
        }

        return box;
    }

    private ScrollViewer TableGrid(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = (TableCell)rows[r][c];
                var content = cell.FirstOrDefault() is ParagraphBlock paragraph ? Text(paragraph.Inline, rows[r].IsHeader ? "th" : null) : new SelectableTextBlock();
                var border = new Border { Classes = { "mdCell" }, Child = content };
                border.Classes.Set("header", rows[r].IsHeader);
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false,
            Content = new Border { Classes = { "mdTable" }, Child = grid },
        };
    }

    private static Border Code(string code, string? language)
    {
        var source = code.TrimEnd('\n', '\r');
        var copy = new Button { Classes = { "icon", "mdCopy" }, Content = new PathIcon { Theme = Glyph(), Data = Icon("IconCopy") } };
        ToolTip.SetTip(copy, "Copy code");
        AutomationProperties.SetName(copy, "Copy code");
        AutomationProperties.SetAutomationId(copy, "CopyCode");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(source);
            }
        };

        var header = new Grid { Classes = { "mdCodeHeader" }, ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Classes = { "mdCodeLanguage" }, Text = string.IsNullOrWhiteSpace(language) ? "Code" : language.Trim() });
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false,
            Content = new SelectableTextBlock { Classes = { "mdMono", "mdCodeText" }, Text = source },
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(body, 1);
        layout.Children.Add(header);
        layout.Children.Add(body);
        return new Border { Classes = { "mdCode" }, Child = layout };
    }

    // A long run with no break opportunity takes time that grows with its square to wrap, so it runs past the edge instead.
    private static SelectableTextBlock Plain(string source) => new() { Classes = { "md", "mdMono" }, Text = source, TextWrapping = TextWrapping.WrapWithOverflow };

    private SelectableTextBlock Text(ContainerInline? inline, string? style)
    {
        var text = new SelectableTextBlock { Classes = { "md" } };
        if (style is not null)
        {
            text.Classes.Add(style);
        }
        else if (inline?.Descendants<Markdig.Syntax.Inlines.Inline>().Any(child => child is LinkInline { IsImage: false } or AutolinkInline) != true)
        {
            // A line that holds a link control sets its own height, because a fixed one would clip the control's text.
            text.LineHeight = BodyLineHeight;
        }

        if (inline is not null)
        {
            foreach (var child in inline)
            {
                Add(text.Inlines!, child, text);
            }
        }

        return text;
    }

    private void Add(InlineCollection target, Markdig.Syntax.Inlines.Inline inline, Control owner)
    {
        switch (inline)
        {
            case LiteralInline { PreviousSibling: TaskList } literal:
                target.Add(new Run(literal.Content.ToString().TrimStart()));
                break;
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case CodeInline code:
                var run = new Run(Breakable(code.Content)) { FontFamily = MonoFont };
                run.Bind(TextElement.BackgroundProperty, owner.GetResourceObservable("SurfaceMutedBrush"));
                target.Add(run);
                break;
            case TaskList:
                break;
            case LineBreakInline lineBreak:
                target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                break;
            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;
            case LinkInline { IsImage: true } image:
                var description = new Run($"[image: {LabelOf(image)}] {image.Url}");
                description.Bind(TextElement.ForegroundProperty, owner.GetResourceObservable("TextMutedBrush"));
                target.Add(description);
                break;
            case LinkInline link:
                target.Add(Link(LabelOf(link) is { Length: > 0 } label ? label : link.Url ?? "", link.Url ?? ""));
                break;
            case AutolinkInline autolink:
                target.Add(Link(autolink.Url, autolink.IsEmail ? $"mailto:{autolink.Url}" : autolink.Url));
                break;
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in emphasis)
                {
                    Add(span.Inlines, child, owner);
                }

                target.Add(span);
                break;
            case ContainerInline container:
                foreach (var child in container)
                {
                    Add(target, child, owner);
                }

                break;
            default:
                target.Add(new Run(inline.ToString()));
                break;
        }
    }

    // A short span, or one with spaces of its own, wraps as it is.
    private static string Breakable(string code) => code.Length < LongSpan || code.Contains(' ')
        ? code
        : string.Concat(code.Select((letter, at) => SpanBreaks.Contains(letter) && at + 1 < code.Length ? $"{letter}{BreakHere}" : letter.ToString()));

    private const double BodyLineHeight = 22;

    private static string LabelOf(ContainerInline inline) => string.Concat(inline.Descendants<Markdig.Syntax.Inlines.Inline>().Select(child => child switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        _ => "",
    }));

    private static InlineUIContainer Link(string label, string destination)
    {
        var link = new Button { Classes = { "mdLink" }, Content = new TextBlock { Classes = { "mdLinkText" }, Text = label } };
        AutomationProperties.SetName(link, label);
        ToolTip.SetTip(link, destination);
        AutomationProperties.SetAutomationId(link, "MessageLink");
        AutomationProperties.SetHelpText(link, destination);
        link.Click += async (_, _) => await Activate(link, destination);
        var open = new MenuItem { Header = "Open link…" };
        open.Click += async (_, _) => await Activate(link, destination);
        var copy = new MenuItem { Header = "Copy link address" };
        copy.Click += async (_, _) =>
        {
            if (ConversationLinkRouter.GetRouter(link) is { } router)
            {
                await router.CopyAsync(destination);
            }
        };
        link.ContextMenu = new ContextMenu { Items = { open, copy } };
        return new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    private static Task Activate(Control link, string destination) =>
        ConversationLinkRouter.GetRouter(link) is { } router ? router.ActivateAsync(link, destination) : Task.CompletedTask;

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private static ControlTheme? Glyph() =>
        Application.Current?.TryGetResource("Glyph", null, out var theme) == true ? theme as ControlTheme : null;

    private static Geometry? Icon(string key) =>
        Application.Current?.TryGetResource(key, null, out var geometry) == true ? geometry as Geometry : null;
}
