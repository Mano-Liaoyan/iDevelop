using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using IDevelop.Desktop.Canvas;
using static IDevelop.Desktop.D0.UI;

namespace IDevelop.Desktop.D0;

// Throwaway D0 conversation placement study with a synthetic transcript. Markdown is hand-built, not a renderer choice. Never merge.
internal static class ConversationProto
{
    // variant: dock | main
    public static Control Build(string variant)
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,320"), Background = Br("AppShellBrush") };
        root.Children.Add(Sidebar());

        var center = variant == "dock" ? Docked() : MainArea();
        Grid.SetColumn(center, 1);
        root.Children.Add(center);

        var width = InspectorProto.ProtoWidth;
        InspectorProto.ProtoWidth = 320;
        var inspector = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Br("Border80Brush"), Child = InspectorProto.Build('A', selected: true),
        };
        InspectorProto.ProtoWidth = width;
        Grid.SetColumn(inspector, 2);
        root.Children.Add(inspector);
        return root;
    }

    private static Control Sidebar()
    {
        var s = new StackPanel { Spacing = 2, Margin = new Thickness(12, 10, 12, 0) };
        s.Children.Add(Btn("Add Node", "IconAdd", "ghost").M(-8, 0, 0, 8));
        s.Children.Add(T("Projects", 12, "TextMutedBrush", FontWeight.SemiBold).M(0, 4, 0, 6));
        s.Children.Add(TreeRow(0, "IconChevronDown", "IconFolder", "storage-change", false, "2"));
        s.Children.Add(TreeRow(1, "IconChevronRight", "IconFlowchart", "Storage workflow", true, "6"));
        s.Children.Add(TreeRow(1, "IconChevronRight", "IconFlowchart", "Release workflow", false, "3"));
        s.Children.Add(TreeRow(0, "IconChevronRight", "IconFolder", "docs-site", false, "1"));
        return new Border { Background = Br("AppSidebarBrush"), BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = Br("Border80Brush"), Child = s };
    }

    private static Control TreeRow(int depth, string chevron, string icon, string text, bool selected, string count)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 6, Height = 30, Margin = new Thickness(depth * 16, 0, 0, 0) };
        g.Children.Add(Icon(chevron, 12, "TextFaintBrush"));
        var i = Icon(icon, 16, "TextMutedBrush");
        Grid.SetColumn(i, 1);
        g.Children.Add(i);
        var t = T(text, 13, selected ? "TextStrongBrush" : "TextBrush", selected ? FontWeight.SemiBold : FontWeight.Normal);
        Grid.SetColumn(t, 2);
        g.Children.Add(t);
        var c = T(count, 12, "TextMutedBrush");
        Grid.SetColumn(c, 3);
        g.Children.Add(c);
        return new Border
        {
            Padding = new Thickness(8, 0), CornerRadius = Rad("RadiusMd"), Child = g,
            Background = selected ? Br("StateSelectedSurfaceBrush") : Brushes.Transparent,
            BorderBrush = selected ? Br("StateSelected40Brush") : Brushes.Transparent, BorderThickness = new Thickness(1),
        };
    }

    private static Control Docked()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("*,6,360") };
        var canvas = new Panel { ClipToBounds = true };
        var cards = CardsProto.Build("collapsed");
        cards.Margin = new Thickness(-330, -60, 0, 0);
        canvas.Children.Add(cards);
        canvas.Children.Add(Breadcrumb(false).M(12, 12, 0, 0).HA(HorizontalAlignment.Left).Also(b => b.VerticalAlignment = VerticalAlignment.Top));
        g.Children.Add(canvas);

        var grip = new Border { Background = Br("AppPanelBrush"), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Br("Border80Brush") };
        grip.Child = new Border { Width = 36, Height = 3, CornerRadius = new CornerRadius(2), Background = Br("Border100Brush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(grip, 1);
        g.Children.Add(grip);

        var dock = new DockPanel { Background = Br("AppPanelBrush") };
        var head = Header(docked: true);
        DockPanel.SetDock(head, Avalonia.Controls.Dock.Top);
        dock.Children.Add(head);
        var composer = Composer(narrow: true);
        DockPanel.SetDock(composer, Avalonia.Controls.Dock.Bottom);
        dock.Children.Add(composer);
        dock.Children.Add(Transcript(maxWidth: double.PositiveInfinity, scrollToEnd: true, pill: true));
        Grid.SetRow(dock, 2);
        g.Children.Add(dock);
        return g;
    }

    private static Control MainArea()
    {
        var dock = new DockPanel { Background = Br("AppPanelBrush") };
        var head = Header(docked: false);
        DockPanel.SetDock(head, Avalonia.Controls.Dock.Top);
        dock.Children.Add(head);
        var composer = Composer(narrow: false);
        DockPanel.SetDock(composer, Avalonia.Controls.Dock.Bottom);
        dock.Children.Add(composer);
        dock.Children.Add(Transcript(maxWidth: 680, scrollToEnd: true, pill: false));
        return dock;
    }

    private static Control Breadcrumb(bool withTask)
    {
        var r = Row(6, Icon("IconFolder", 14), T("storage-change", 12, "TextMutedBrush"), Icon("IconChevronRight", 10, "TextFaintBrush"),
            T("Storage workflow", 12, withTask ? "TextMutedBrush" : "TextStrongBrush"));
        if (withTask)
        {
            r.Children.Add(Icon("IconChevronRight", 10, "TextFaintBrush"));
            r.Children.Add(Tile(NodeKind.Implement, 16));
            r.Children.Add(T("Implement atomic save", 12, "TextStrongBrush", FontWeight.SemiBold));
        }

        return new Border
        {
            Background = Br("SurfaceOverlayBrush"), BorderBrush = Br("Border80Brush"), BorderThickness = withTask ? new Thickness(0) : new Thickness(1),
            CornerRadius = Rad("RadiusLg"), Padding = withTask ? new Thickness(0) : new Thickness(10, 7), Child = r,
        };
    }

    private static Control Header(bool docked)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8, Height = 44, Margin = new Thickness(12, 0, 8, 0) };
        var lead = docked
            ? Row(8, Icon("IconStateWaiting", 16, "TextMutedBrush"), T("Conversation", 13, "TextStrongBrush", FontWeight.SemiBold))
            : Row(4, Btn("Canvas", "IconChevronRight", "ghost").Also(b => b.RenderTransform = null));
        if (!docked)
        {
            lead = Row(4, BackButton());
        }

        g.Children.Add(lead);
        var crumb = Breadcrumb(true).M(docked ? 8 : 4, 0, 0, 0);
        crumb.VerticalAlignment = VerticalAlignment.Center;
        crumb.ClipToBounds = true;
        Grid.SetColumn(crumb, 1);
        g.Children.Add(crumb);
        var attempt = new Border
        {
            Height = 26, Padding = new Thickness(8, 0), CornerRadius = Rad("RadiusSm"), BorderBrush = Br("Border100Brush"), BorderThickness = new Thickness(1),
            Child = Row(6, T("Attempt 3 of 3", 12, "TextBrush"), Icon("IconChevronDown", 10, "TextFaintBrush")),
        };
        Grid.SetColumn(attempt, 2);
        g.Children.Add(attempt);
        var actions = Row(2, Glyph(docked ? "IconStateElsewhere" : "IconChevronDown", 26, "TextMutedBrush", 14), Glyph("IconDismiss", 26, "TextMutedBrush", 14));
        ToolTip.SetTip(actions.Children[0], docked ? "Open in main area" : "Dock below the canvas");
        Grid.SetColumn(actions, 3);
        g.Children.Add(actions);
        return new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Br("Border80Brush"), Background = Br("AppTopbarBrush"), Child = g };
    }

    private static Control BackButton()
    {
        var b = new Button { Classes = { "ghost" }, Height = 28, Padding = new Thickness(6, 0) };
        b.Content = Row(4, new PathIcon { Theme = (Avalonia.Styling.ControlTheme)Res("Glyph"), Data = (Geometry)Res("IconChevronRight"), Width = 12, Height = 12, Foreground = Br("TextMutedBrush"), RenderTransform = new ScaleTransform(-1, 1) },
            T("Canvas", 13, "TextBrush"));
        return b;
    }

    private static Control Transcript(double maxWidth, bool scrollToEnd, bool pill)
    {
        var s = new StackPanel { Spacing = 14, Margin = new Thickness(20, 16, 20, 16), MaxWidth = maxWidth };
        s.Children.Add(Marker("Attempt 3 · Codex · gpt-6.1-sol · High · started 12:41"));
        s.Children.Add(UserMsg("Implement atomic save for the workflow file. Keep the previous file intact if the process dies."));
        s.Children.Add(AgentMsg(
            H("Plan"),
            Numbered(1, "Write to a temporary sibling, `workflow.json.tmp`."),
            Numbered(2, "Flush it with `Flush(flushToDisk: true)`."),
            Numbered(3, "Replace the target with `File.Move(tmp, path, overwrite: true)`.")));
        s.Children.Add(Tools("Read 3 files, ran 1 command"));
        s.Children.Add(AgentMsg(
            Md("The save path now looks like this:"),
            Code("csharp",
                "await using (var stream = new FileStream(tmp, FileMode.Create))\n{\n    await JsonSerializer.SerializeAsync(stream, workflow);\n    stream.Flush(flushToDisk: true);\n}\nFile.Move(tmp, path, overwrite: true);")));
        s.Children.Add(Tools("Ran 4 tests, all passed"));
        s.Children.Add(AgentMsg(
            Md("Crash cases I checked:"),
            Table(["Case", "Result"], [["Kill during write", "Previous file intact"], ["Disk full", "Error shown, file intact"], ["Network drive", "**Not atomic**"]]),
            Md("See [WorkflowDocument.cs](x) for the change.")));
        s.Children.Add(Question());

        var scroll = new ScrollViewer { Content = s, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (scrollToEnd)
        {
            scroll.LayoutUpdated += (_, _) =>
            {
                if (scroll.Extent.Height > scroll.Viewport.Height && scroll.Offset.Y < 1)
                {
                    scroll.ScrollToEnd();
                }
            };
        }

        var host = new Panel();
        host.Children.Add(scroll);
        if (pill)
        {
            var older = new Border
            {
                Background = Br("SurfaceOverlayBrush"), BorderBrush = Br("Border80Brush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0),
                Child = T("6 earlier messages", 11, "TextMutedBrush"),
            };
            host.Children.Add(older);
        }
        return host;
    }

    private static Control Marker(string text)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 10 };
        g.Children.Add(new Border { Height = 1, Background = Br("Border80Brush"), VerticalAlignment = VerticalAlignment.Center });
        var t = T(text, 11, "TextMutedBrush");
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        var r = new Border { Height = 1, Background = Br("Border80Brush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(r, 2);
        g.Children.Add(r);
        return g;
    }

    private static Control UserMsg(string text) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 460, Background = Br("SurfaceMutedBrush"), CornerRadius = new CornerRadius(14),
        Padding = new Thickness(12, 8), Child = Md(text),
    };

    private static Control AgentMsg(params Control[] blocks)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*"), ColumnSpacing = 10 };
        g.Children.Add(new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = Br("SurfaceMutedBrush"), VerticalAlignment = VerticalAlignment.Top,
            Child = Icon("IconSparkle", 14, "TextMutedBrush").HA(HorizontalAlignment.Center),
        });
        var col = Col(8, blocks);
        Grid.SetColumn(col, 1);
        g.Children.Add(col);
        return g;
    }

    private static Control H(string text) => T(text, 15, "TextStrongBrush", FontWeight.SemiBold);

    private static Control Numbered(int n, string text)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
        g.Children.Add(T($"{n}.", 13, "TextMutedBrush").Also(t => t.VerticalAlignment = VerticalAlignment.Top));
        var m = Md(text);
        Grid.SetColumn(m, 1);
        g.Children.Add(m);
        return g;
    }

    private static Control Tools(string summary) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(34, -4, 0, -4), Padding = new Thickness(8, 3), CornerRadius = Rad("RadiusSm"),
        BorderBrush = Br("Border80Brush"), BorderThickness = new Thickness(1),
        Child = Row(6, Icon("IconChevronRight", 10, "TextFaintBrush"), Icon("IconStateRunning", 12, "TextMutedBrush"), T(summary, 12, "TextMutedBrush")),
    };

    private static Control Code(string lang, string code)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 4, 4, 0) };
        head.Children.Add(T(lang, 11, "TextMutedBrush"));
        var copy = Row(4, Icon("IconDuplicate", 12, "TextMutedBrush"), T("Copy", 11, "TextMutedBrush"));
        var copyBtn = new Button { Classes = { "ghost" }, Height = 22, Padding = new Thickness(6, 0), Content = copy };
        Grid.SetColumn(copyBtn, 1);
        head.Children.Add(copyBtn);
        return new Border
        {
            Background = Br("SurfaceMutedBrush"), CornerRadius = Rad("RadiusMd"), BorderBrush = Br("Border80Brush"), BorderThickness = new Thickness(1),
            Child = Col(0, head, new TextBlock { Text = code, FontFamily = Mono, FontSize = 12, Foreground = Br("TextStrongBrush"), Margin = new Thickness(10, 2, 10, 10), LineHeight = 18 }),
        };
    }

    private static Control Table(string[] head, string[][] rows)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", rows.Length + 1)));
        for (var r = 0; r <= rows.Length; r++)
        {
            for (var c = 0; c < 2; c++)
            {
                var text = r == 0 ? head[c] : rows[r - 1][c];
                var cell = new Border
                {
                    BorderBrush = Br("Border80Brush"), BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1), Padding = new Thickness(10, 5),
                    Background = r == 0 ? Br("SurfaceMutedBrush") : Brushes.Transparent,
                    Child = r == 0 ? T(text, 12, "TextStrongBrush", FontWeight.SemiBold) : Md(text, 12),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
        }

        return new Border { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320, Child = g };
    }

    private static Control Question()
    {
        var box = new TextBox { Watermark = "Answer the agent…", FontSize = 13, MinHeight = 30 };
        var send = Btn("Answer", null, "primary", 30);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(box);
        Grid.SetColumn(send, 1);
        row.Children.Add(send);
        var quick = Row(6, Chip("Yes, add the fallback"), Chip("No, document the limit"));
        return new Border
        {
            Margin = new Thickness(34, 0, 0, 0), Background = Br("StateWaiting10Brush"), BorderBrush = Br("StateWaitingStrokeBrush"), BorderThickness = new Thickness(1),
            CornerRadius = Rad("RadiusMd"), Padding = new Thickness(12, 10),
            Child = Col(8,
                Row(6, Icon("IconStateWaiting", 14, "StateWaitingStrongBrush"), T("The agent asks", 12, "TextStrongBrush", FontWeight.SemiBold), T("· waiting 3 min", 12, "TextMutedBrush")),
                Md("Rename is not atomic on network drives. Should I add a copy-and-verify fallback?"),
                quick, row),
        };
    }

    private static Control Composer(bool narrow)
    {
        var box = new TextBox { Watermark = "Message Implement atomic save…", FontSize = 13, MinHeight = narrow ? 34 : 56, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var send = Btn("Send", null, "primary", 32);
        var more = new Button { Classes = { "action" }, Height = 32, Width = 30, Padding = new Thickness(0), Content = Icon("IconChevronDown", 12, "TextMutedBrush").HA(HorizontalAlignment.Center) };
        ToolTip.SetTip(more, "Queue, or Stop and send");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(16, 10, 16, 6) };
        g.Children.Add(box);
        Grid.SetColumn(send, 1);
        send.VerticalAlignment = VerticalAlignment.Bottom;
        g.Children.Add(send);
        Grid.SetColumn(more, 2);
        more.VerticalAlignment = VerticalAlignment.Bottom;
        g.Children.Add(more);
        var hint = T("Example capability note: this client offers no tool-approval channel. The D0 client probe supplies the real table.", 11, "TextFaintBrush").M(16, 0, 16, 8);
        return new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Br("Border80Brush"), Child = Col(0, g, hint),
        };
    }

    private static T2 Also<T2>(this T2 c, Action<T2> f)
    {
        f(c);
        return c;
    }
}
