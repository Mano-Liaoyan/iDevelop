using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// One message's Markdown as Avalonia controls. HTML stays literal text, an image shows its description and address and
/// is never fetched, and a link goes only to the <see cref="ConversationLinkRouter"/>. A new text rebuilds only the blocks
/// whose source changed, and a block with selected text keeps its old content until the selection clears. A message too
/// long, too deeply nested, or with too many blocks, table cells, or links to lay out quickly shows as its source.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<bool> FitsCodeProperty = AvaloniaProperty.Register<MarkdownView, bool>(nameof(FitsCode));

    // Measured headlessly at each limit, 1,000 table cells or 500 links lay out in about 165 ms, and 20,000 characters of
    // nested brackets in about 530 ms. A code block costs about ten paragraphs, and 1,000 paragraphs or 100 code blocks
    // lay out in about 120 ms.
    private const int MaxLength = 20_000;
    private const int MaxCells = 1_000;
    private const int MaxLinks = 500;
    private const int MaxBlockWeight = 1_000;

    private const string SourceKey = "\0source";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();

    private readonly List<string> _sources = [];
    private SelectableTextBlock? _waitingOn;

    public MarkdownView()
    {
        Spacing = 8;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>
    /// Whether a code block wraps its lines to the view's width instead of scrolling sideways, and shows a block that holds
    /// one JSON value indented, as a card too narrow to scroll in needs, such as what an approval hands on.
    /// </summary>
    public bool FitsCode
    {
        get => GetValue(FitsCodeProperty);
        set => SetValue(FitsCodeProperty, value);
    }

    /// <summary>How many blocks were built since the view was created, which shows that unchanged blocks are reused.</summary>
    internal int BlocksBuilt { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Render();
        }
        else if (change.Property == FitsCodeProperty)
        {
            // Every code block changes its shape, so no block is reused.
            _sources.Clear();
            Children.Clear();
            Render();
        }
    }

    private void Render()
    {
        var text = Markdown ?? "";
        if (Parse(text) is not { } blocks)
        {
            ShowSource(text);
            return;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var source = Source(text, blocks[i]);
            if (i < _sources.Count && _sources[i] == source)
            {
                continue;
            }

            if (i < _sources.Count && Selected(Children[i]) is { } selected)
            {
                WaitForSelectionToClear(selected);
                continue;
            }

            var control = Block(blocks[i], text);
            BlocksBuilt++;
            if (i < _sources.Count)
            {
                Children[i] = control;
                _sources[i] = source;
            }
            else
            {
                Children.Add(control);
                _sources.Add(source);
            }
        }

        while (_sources.Count > blocks.Count)
        {
            _sources.RemoveAt(_sources.Count - 1);
            Children.RemoveAt(Children.Count - 1);
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

    // A message streams as its source once it passes a limit, so its one block takes the new text in place and keeps a
    // selection until it clears.
    private void ShowSource(string text)
    {
        if (_sources is [SourceKey] && Children[0] is SelectableTextBlock shown)
        {
            if (Selected(shown) is { } selected)
            {
                WaitForSelectionToClear(selected);
            }
            else
            {
                shown.Text = text;
            }

            return;
        }

        Children.Clear();
        _sources.Clear();
        Children.Add(Plain(text));
        _sources.Add(SourceKey);
        BlocksBuilt++;
    }

    private static string Source(string text, Block block)
    {
        var start = Math.Clamp(block.Span.Start, 0, text.Length);
        return text.Substring(start, Math.Clamp(block.Span.Length, 0, text.Length - start));
    }

    private static SelectableTextBlock? Selected(Control block) =>
        block.GetSelfAndLogicalDescendants().OfType<SelectableTextBlock>().FirstOrDefault(text => text.SelectionStart != text.SelectionEnd);

    private void WaitForSelectionToClear(SelectableTextBlock selected)
    {
        if (_waitingOn is not null)
        {
            return;
        }

        _waitingOn = selected;
        selected.PropertyChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_waitingOn is { } text && (e.Property == SelectableTextBlock.SelectionStartProperty || e.Property == SelectableTextBlock.SelectionEndProperty)
            && text.SelectionStart == text.SelectionEnd)
        {
            text.PropertyChanged -= OnSelectionChanged;
            _waitingOn = null;
            Render();
        }
    }

    private Control Block(Block block, string text) => block switch
    {
        HeadingBlock heading => Text(heading.Inline, $"h{Math.Min(heading.Level, 4)}"),
        ParagraphBlock paragraph => Text(paragraph.Inline, null),
        FencedCodeBlock fenced => Code(fenced.Lines.ToString(), fenced.Info, FitsCode),
        CodeBlock code => Code(code.Lines.ToString(), null, FitsCode),
        ListBlock list => List(list, text),
        QuoteBlock quote => new Border { Classes = { "mdQuote" }, Child = Container(quote, text) },
        Table table => TableGrid(table),
        ThematicBreakBlock => new Border { Classes = { "mdRule" } },
        ContainerBlock container => Container(container, text),
        _ => Plain(Source(text, block)),
    };

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
            var marker = new TextBlock { Classes = { "mdMarker" }, Text = list.IsOrdered ? $"{number++}." : "•" };
            var body = Container(item, text);
            Grid.SetRow(marker, row);
            Grid.SetRow(body, row);
            Grid.SetColumn(body, 1);
            grid.Children.Add(marker);
            grid.Children.Add(body);
        }

        return grid;
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
            Content = new Border { Classes = { "mdTable" }, Child = grid },
        };
    }

    private static Border Code(string code, string? language, bool fits)
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
        Control body = fits && Fits(source)
            ? new SelectableTextBlock { Classes = { "mdMono", "mdCodeText" }, Text = Indented(source) ?? source, TextWrapping = TextWrapping.Wrap }
            : new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new SelectableTextBlock { Classes = { "mdMono", "mdCodeText" }, Text = source },
            };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(body, 1);
        layout.Children.Add(header);
        layout.Children.Add(body);
        return new Border { Classes = { "mdCode" }, Child = layout };
    }

    // A run with no break opportunity takes time that grows with its square to wrap, so a block with a very long one
    // scrolls sideways even where it should fit.
    private static bool Fits(string source) => source.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).All(run => run.Length <= 1_000);

    /// <summary>The source indented, when it is one JSON object or array, such as a reviewer's verdict, else null.</summary>
    private static string? Indented(string source)
    {
        var trimmed = source.Trim();
        if (trimmed is not ['{', .., '}'] and not ['[', .., ']'])
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(trimmed)?.ToJsonString(IndentedJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // A long run with no break opportunity takes time that grows with its square to wrap, so it runs past the edge instead.
    private static SelectableTextBlock Plain(string source) => new() { Classes = { "md", "mdMono" }, Text = source, TextWrapping = TextWrapping.WrapWithOverflow };

    private SelectableTextBlock Text(ContainerInline? inline, string? style)
    {
        var text = new SelectableTextBlock { Classes = { "md" } };
        if (style is not null)
        {
            text.Classes.Add(style);
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
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case CodeInline code:
                var run = new Run(code.Content) { FontFamily = MonoFont };
                run.Bind(TextElement.BackgroundProperty, owner.GetResourceObservable("SurfaceMutedBrush"));
                target.Add(run);
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

    internal static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private static ControlTheme? Glyph() =>
        Application.Current?.TryGetResource("Glyph", null, out var theme) == true ? theme as ControlTheme : null;

    private static Geometry? Icon(string key) =>
        Application.Current?.TryGetResource(key, null, out var geometry) == true ? geometry as Geometry : null;
}
