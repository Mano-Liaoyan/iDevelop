using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using IDevelop.Desktop.Canvas;
using static IDevelop.Desktop.D0.UI;

namespace IDevelop.Desktop.D0;

// Throwaway D0 expanded-card study: a fixed expanded height against a content-sized one, on a demo-shaped diamond. Never merge.
internal static class CardsProto
{
    private enum St { Done, Waiting, Running, Blocked, Idle }

    private sealed record N(string Id, string Title, NodeKind Kind, St State, double X, double Y, string Subtitle, bool HasLinks = false);

    private const double W = 240;
    private const double Head = 64;
    private const double FixedH = 312;
    private const double CapH = 320;

    private static readonly N[] Nodes =
    [
        new("plan", "Plan storage work", NodeKind.Plan, St.Done, 40, 210, "Done · 3 tasks proposed", HasLinks: true),
        new("design", "Design file format", NodeKind.Implement, St.Done, 330, 210, "Done · 4 min ago", HasLinks: true),
        new("atomic", "Implement atomic save", NodeKind.Implement, St.Waiting, 620, 120, "Waiting for you"),
        new("tests", "Write crash tests", NodeKind.Implement, St.Running, 620, 278, "Running · 2:14"),
        new("join", "Integrate storage", NodeKind.Implement, St.Blocked, 910, 210, "Waiting on 2 tasks"),
        new("review", "Review the change", NodeKind.Review, St.Idle, 1200, 210, "Codex · gpt-6.1-sol"),
    ];

    private static readonly (string From, string To, bool Context)[] Wires =
    [
        ("plan", "design", false), ("design", "atomic", false), ("design", "tests", false),
        ("atomic", "join", false), ("tests", "join", false), ("join", "review", false), ("plan", "review", true),
    ];

    // variant: collapsed | fixed-one | fixed-all | content-one | content-all, plus -headers to keep every header above bodies.
    public static Control Build(string variant)
    {
        var fixedH = variant.StartsWith("fixed", StringComparison.Ordinal);
        var headersOnTop = variant.EndsWith("-headers", StringComparison.Ordinal);
        var hybrid = variant.EndsWith("-hybrid", StringComparison.Ordinal);
        variant = variant.Replace("-headers", "", StringComparison.Ordinal).Replace("-hybrid", "", StringComparison.Ordinal);
        var expanded = variant switch
        {
            "collapsed" => [],
            _ when variant.EndsWith("-one", StringComparison.Ordinal) => new HashSet<string> { "atomic" },
            _ => Nodes.Select(n => n.Id).ToHashSet(),
        };

        var canvas = new Avalonia.Controls.Canvas { Background = UI.Canvas(), ClipToBounds = true };
        foreach (var (from, to, context) in Wires)
        {
            canvas.Children.Add(Wire(Nodes.First(n => n.Id == from), Nodes.First(n => n.Id == to), context));
        }

        if (hybrid)
        {
            // Every other card's header stays above every other body; the selected card stays whole on top.
            var others = Nodes.Where(n => n.Id != "atomic").ToList();
            foreach (var n in others)
            {
                canvas.Children.Add(Card(n, expanded.Contains(n.Id), fixedH, selected: false, headerOnly: false).At(n.X - 10, n.Y));
            }

            foreach (var n in others)
            {
                canvas.Children.Add(Card(n, expanded.Contains(n.Id), fixedH, selected: false, headerOnly: true).At(n.X - 10, n.Y));
            }

            var sel = Nodes.First(n => n.Id == "atomic");
            canvas.Children.Add(Card(sel, expanded.Contains(sel.Id), fixedH, selected: true, headerOnly: false).At(sel.X - 10, sel.Y));
            canvas.Children.Add(Toolbar(expanded.Count == Nodes.Length).At(16, 16));
            return canvas;
        }

        // Collapsed cards first, then expanded ones, so an expanded card draws over its neighbors; the selected one last.
        foreach (var n in Nodes.OrderBy(n => expanded.Contains(n.Id)).ThenBy(n => n.Id == "atomic"))
        {
            canvas.Children.Add(Card(n, expanded.Contains(n.Id), fixedH, selected: n.Id == "atomic", headerOnly: false).At(n.X - 10, n.Y));
        }

        if (headersOnTop)
        {
            foreach (var n in Nodes.Where(n => n.Id != "atomic").Append(Nodes.First(n => n.Id == "atomic")))
            {
                canvas.Children.Add(Card(n, expanded.Contains(n.Id), fixedH, selected: n.Id == "atomic", headerOnly: true).At(n.X - 10, n.Y));
            }
        }

        canvas.Children.Add(Toolbar(expanded.Count == Nodes.Length).At(16, 16));
        return canvas;
    }

    private static Control Toolbar(bool allExpanded)
    {
        var b = new Border
        {
            Background = Br("SurfaceOverlayBrush"), BorderBrush = Br("Border80Brush"), BorderThickness = new Thickness(1),
            CornerRadius = Rad("RadiusLg"), Padding = new Thickness(4), BoxShadow = (BoxShadows)Res("SmallShadow"),
        };
        var expandAll = Btn(allExpanded ? "Collapse All" : "Expand All", allExpanded ? "IconChevronRight" : "IconChevronDown", "ghost");
        b.Child = Row(2, Btn("Run Workflow", "IconRun", "ghost"), expandAll);
        return b;
    }

    private static Control Wire(N a, N b, bool context)
    {
        // Ports sit on the header row's centre, so an expanded card leaves every wire where it was.
        var x1 = a.X + W;
        var y1 = a.Y + Head / 2;
        var x2 = b.X;
        var y2 = b.Y + Head / 2;
        var mid = x1 + (x2 - x1) / 2;
        if (context)
        {
            mid = x1 + 25;
        }

        var points = context
            ? new List<Point> { new(x1, y1), new(x1 + 20, y1), new(x1 + 20, 420), new(x2 - 20, 420), new(x2 - 20, y2), new(x2, y2) }
            : new List<Point> { new(x1, y1), new(mid, y1), new(mid, y2), new(x2, y2) };
        var line = new Polyline
        {
            Points = points, Stroke = Br($"Kind{a.Kind}StrokeBrush"), StrokeThickness = 2, StrokeJoin = PenLineJoin.Round,
        };
        if (context)
        {
            line.StrokeDashArray = [3, 2];
            line.Opacity = 0.7;
        }

        return line;
    }

    private static Control Card(N n, bool expanded, bool fixedH, bool selected, bool headerOnly)
    {
        var stateBrush = n.State switch
        {
            St.Done => "StateSuccessStrokeBrush",
            St.Waiting => "StateWaitingStrokeBrush",
            St.Running => "StateRunningBrush",
            St.Blocked => "Border100Brush",
            _ => "Border80Brush",
        };
        var glyph = n.State switch
        {
            St.Done => ("IconStateSucceeded", "StateSuccessStrongBrush"),
            St.Waiting => ("IconStateWaiting", "StateWaitingStrongBrush"),
            St.Running => ("IconStateRunning", "StateRunningBrush"),
            St.Blocked => ("IconStateInterrupted", "TextFaintBrush"),
            _ => ((string?)null, "TextFaintBrush"),
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,Auto,Auto"), ColumnSpacing = 10, Height = Head, Margin = new Thickness(12, 0, 6, 0) };
        header.Children.Add(Tile(n.Kind, 32));
        var titles = Col(0, T(n.Title, 13, "TextStrongBrush", FontWeight.SemiBold), T(n.Subtitle, 11, n.State == St.Waiting ? "TextBrush" : "TextMutedBrush"));
        titles.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var marks = Row(4);
        marks.VerticalAlignment = VerticalAlignment.Center;
        if (n.HasLinks)
        {
            marks.Children.Add(Icon("IconLink", 14, "TextMutedBrush"));
        }

        if (glyph.Item1 is { } g)
        {
            marks.Children.Add(Icon(g, 16, glyph.Item2));
        }

        Grid.SetColumn(marks, 2);
        header.Children.Add(marks);
        var disclose = Glyph(expanded ? "IconChevronDown" : "IconChevronRight", 22, "TextMutedBrush", 12);
        Grid.SetColumn(disclose, 3);
        header.Children.Add(disclose);

        var stack = new StackPanel();
        stack.Children.Add(header);
        if (expanded && !headerOnly)
        {
            stack.Children.Add(Body(n, fixedH));
        }

        var card = new Border
        {
            Width = W, CornerRadius = Rad("RadiusXl"), Background = Br("SurfaceRaisedBrush"),
            BorderBrush = Br(stateBrush), BorderThickness = new Thickness(1.5),
            BoxShadow = (BoxShadows)Res(expanded ? "FloatingShadow" : "NodeShadow"), ClipToBounds = true, Child = stack,
        };
        if (headerOnly || !expanded)
        {
            card.Height = Head;
        }
        else if (fixedH)
        {
            card.Height = FixedH;
        }
        else
        {
            card.MaxHeight = CapH;
        }

        if (headerOnly)
        {
            card.BoxShadow = (BoxShadows)Res("NodeShadow");
            if (expanded)
            {
                card.CornerRadius = new CornerRadius(14, 14, 0, 0);
                card.BorderThickness = new Thickness(1.5, 1.5, 1.5, 1);
            }
        }

        var host = new Panel { Width = W + 20 };
        card.Margin = new Thickness(10, 0, 10, 8);
        card.VerticalAlignment = VerticalAlignment.Top;
        host.Children.Add(card);
        if (selected && !headerOnly)
        {
            host.Children.Add(new Border
            {
                Margin = new Thickness(6, -4, 6, 4), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(2),
                BorderBrush = Br("AccentBrush"),
            });
        }

        foreach (var left in new[] { true, false })
        {
            host.Children.Add(new Ellipse
            {
                Width = 10, Height = 10, Fill = Br("SurfaceRaisedBrush"), Stroke = Br($"Kind{n.Kind}StrokeBrush"), StrokeThickness = 1.5,
                HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(left ? 5 : 0, Head / 2 - 5, left ? 0 : 5, 0),
            });
        }

        return host;
    }

    private static Control Body(N n, bool fixedH)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 12, 12) };
        body.Children.Add(new Border { Height = 1, Background = Br("Border80Brush"), Margin = new Thickness(-12, 0, -12, 0) });

        var running = n.State is St.Running or St.Waiting;
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        var start = SmallBtn(running ? "Stop" : "Start", running ? "IconStop" : "IconRun", running ? "action" : n.State == St.Blocked ? "action" : "primary");
        start.IsEnabled = n.State != St.Blocked;
        controls.Children.Add(start);
        var agent = SmallPicker(n.Kind == NodeKind.Review ? "Claude Code" : "Codex", "IconSparkle");
        Grid.SetColumn(agent, 1);
        controls.Children.Add(agent);
        body.Children.Add(controls);

        var settings = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        settings.Children.Add(SmallPicker(n.Kind == NodeKind.Review ? "opus-5.5" : "gpt-6.1-sol", null));
        var effort = SmallPicker("High", null);
        Grid.SetColumn(effort, 1);
        settings.Children.Add(effort);
        body.Children.Add(settings);
        if (running)
        {
            body.Children.Add(T("Settings apply to the next attempt", 11, "TextFaintBrush"));
        }

        switch (n.State)
        {
            case St.Done:
                body.Children.Add(Preview(n.Kind == NodeKind.Plan
                    ? "Proposed 3 tasks: file format, atomic save, crash tests."
                    : "Chose one JSON file per workflow with stable key order."));
                body.Children.Add(Links());
                break;
            case St.Waiting:
                body.Children.Add(Question());
                break;
            case St.Running:
                body.Children.Add(Row(6, Icon("IconStateRunning", 12, "StateRunningBrush"), T("Running tests · 3 tool calls", 12, "TextMutedBrush")));
                body.Children.Add(OpenConversation());
                break;
            case St.Blocked:
                body.Children.Add(T("Waits for Implement atomic save and Write crash tests.", 12, "TextMutedBrush", trim: false).Also(t => t.TextWrapping = TextWrapping.Wrap));
                break;
            case St.Idle:
                break;
        }

        if (fixedH)
        {
            // A fixed card keeps every slot, so an empty one says so instead of collapsing.
            if (n.State is St.Blocked or St.Idle or St.Running)
            {
                body.Children.Add(Placeholder(n.State == St.Running ? "No result yet" : "No result or links yet"));
            }

            if (n.State is St.Blocked or St.Idle)
            {
                body.Children.Add(Placeholder("No question"));
            }
        }

        return body;
    }

    private static Control Placeholder(string text) => new Border
    {
        Height = 30, CornerRadius = Rad("RadiusSm"), BorderBrush = Br("Border80Brush"), BorderThickness = new Thickness(1),
        Child = T(text, 11, "TextFaintBrush").HA(HorizontalAlignment.Center),
    };

    private static Control Preview(string text) => new Border
    {
        Background = Br("SurfaceMutedBrush"), CornerRadius = Rad("RadiusSm"), Padding = new Thickness(8, 6),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Br("TextBrush"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis },
    };

    private static Control Links()
    {
        var w = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        w.Children.Add(Chip("Report", "IconLink", "Accent10Brush", "AccentTextBrush"));
        w.Children.Add(Chip("Branch", "IconDerive", "Accent10Brush", "AccentTextBrush"));
        return w;
    }

    private static Control Question()
    {
        var box = new TextBox { Watermark = "Answer…", FontSize = 12, MinHeight = 26, Height = 26, Padding = new Thickness(8, 4) };
        var send = SmallBtn("Send", null, "primary");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(box);
        Grid.SetColumn(send, 1);
        row.Children.Add(send);
        return new Border
        {
            Background = Br("StateWaiting10Brush"), BorderBrush = Br("StateWaitingStrokeBrush"), BorderThickness = new Thickness(1),
            CornerRadius = Rad("RadiusSm"), Padding = new Thickness(8),
            Child = Col(6,
                Row(5, Icon("IconStateWaiting", 12, "StateWaitingStrongBrush"), T("Question", 11, "TextStrongBrush", FontWeight.SemiBold)),
                new TextBlock { Text = "Rename is not atomic on network drives. Add a copy-and-verify fallback?", FontSize = 12, Foreground = Br("TextBrush"), TextWrapping = TextWrapping.Wrap },
                row,
                T("Open Conversation", 12, "AccentTextBrush")),
        };
    }

    private static Control OpenConversation() => T("Open Conversation", 12, "AccentTextBrush");

    private static Button SmallBtn(string text, string? icon, string cls)
    {
        var b = Btn(text, icon, cls, 26);
        b.FontSize = 12;
        b.Padding = new Thickness(8, 0);
        return b;
    }

    private static Control SmallPicker(string text, string? icon)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 5, Margin = new Thickness(8, 0) };
        if (icon is not null)
        {
            g.Children.Add(Icon(icon, 12, "TextMutedBrush"));
        }

        var t = T(text, 12, "TextBrush");
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        var c = Icon("IconChevronDown", 10, "TextFaintBrush");
        Grid.SetColumn(c, 2);
        g.Children.Add(c);
        return new Border
        {
            Height = 26, CornerRadius = Rad("RadiusSm"), BorderBrush = Br("Border100Brush"), BorderThickness = new Thickness(1),
            Background = Br("SurfaceRaisedBrush"), Child = g,
        };
    }

    private static T2 Also<T2>(this T2 c, Action<T2> f)
    {
        f(c);
        return c;
    }
}
