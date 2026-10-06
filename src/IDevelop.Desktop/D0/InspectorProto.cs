using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using IDevelop.Desktop.Canvas;
using static IDevelop.Desktop.D0.UI;

namespace IDevelop.Desktop.D0;

// Throwaway D0 inspector layouts over one fake data set. Never merge.
internal static class InspectorProto
{
    private abstract record Val;
    private sealed record PickerV(string Text) : Val;
    private sealed record TextV(string Text) : Val;
    private sealed record PlainV(string Text, string Brush = "TextBrush") : Val;
    private sealed record LinkV(string Text) : Val;
    private sealed record StatusV(string Icon, string Brush, string Text) : Val;
    private sealed record ChipsV(string[] Items) : Val;
    private sealed record CountV(int N) : Val;
    private sealed record ActionsV((string Text, string? Icon, string Cls)[] Buttons) : Val;
    private sealed record NoneV : Val;

    private sealed record F(string? Icon, string Label, Val Value, bool Changed = false, string? Info = null,
        NodeKind? Kind = null, bool Library = false, string? Trail = null, bool Heading = false);

    private sealed record S(string Title, string Icon, string? Badge, F[] Rows);

    // A: proportional grid, two-slot trailing column. B: fixed label column, tinted bands, one slot. C: inset groups, values right.
    private sealed record Spec(char Id, double TrailW, double LabelFixed, bool Bands, bool Grouped, double RowH, bool StackNarrow);

    private static readonly Spec A = new('A', 52, 0, false, false, 30, true);
    private static readonly Spec B = new('B', 24, 112, true, false, 26, false);
    private static readonly Spec C = new('C', 24, 0, false, true, 34, false);
    private const double Inset = 12;

    private static readonly S[] Selected =
    [
        new("Task", "IconEdit", null,
        [
            new("IconEdit", "Instructions", new TextV("Write the workflow to a temporary sibling, flush it to disk, then rename it over the target. Keep the previous file if the process dies."), Changed: true),
            new("IconCheckmark", "Acceptance criteria", new TextV("Killing the process during save leaves the previous file intact.")),
        ]),
        new("Agent", "IconSparkle", null,
        [
            new("IconSparkle", "Client", new PickerV("Codex"), Info: "Runs in Codex's workspace sandbox"),
            new(null, "Model", new PickerV("gpt-6.1-sol")),
            new(null, "Reasoning", new PickerV("High"), Changed: true),
            new("IconStateWaiting", "Conversation", new PickerV("May ask")),
        ]),
        new("Inputs", "IconWireDependency", "2",
        [
            new("IconWireDependency", "Design file format", new PlainV("Report, used"), Trail: "IconChevronRight"),
            new("IconWireContext", "Plan storage work", new PlainV("Context, 12:40", "TextMutedBrush"), Trail: "IconChevronRight"),
        ]),
        new("Outputs", "IconLink", "2",
        [
            new(null, "Report", new LinkV("Open report")),
            new("IconDerive", "Branch", new PlainV("idp/atomic-save 4f2c1e9"), Trail: "IconDuplicate"),
            new("IconLink", "Published", new PlainV("Nothing yet", "TextFaintBrush")),
        ]),
        new("Run", "IconRun", null,
        [
            new("IconStateWaiting", "Status", new StatusV("IconStateWaiting", "StateWaitingStrongBrush", "Waiting for your answer")),
            new(null, "Attempt", new PlainV("3, started 12:41")),
            new("IconSparkle", "Effective", new ChipsV(["Codex", "gpt-6.1-sol", "High"])),
            new(null, "", new ActionsV([("Stop", "IconStop", "action"), ("Open Conversation", "IconStateWaiting", "primary")])),
        ]),
        new("Blueprint", "IconDerive", null,
        [
            new(null, "Blueprint", new PlainV("Implement, built-in v1"), Kind: NodeKind.Implement),
            new("IconDerive", "Derived from", new PlainV("None", "TextFaintBrush")),
            new(null, "", new ActionsV([("Derive…", "IconDerive", "ghost"), ("Save as Blueprint…", "IconSaveAs", "ghost")])),
        ]),
    ];

    private static readonly S[] Unselected =
    [
        new("Overview", "IconFlowchart", "7",
        [
            new(null, "Plan", new CountV(1), Kind: NodeKind.Plan),
            new(null, "Implement", new CountV(4), Kind: NodeKind.Implement),
            new(null, "Review", new CountV(1), Kind: NodeKind.Review),
            new(null, "Approval", new CountV(1), Kind: NodeKind.Approval),
            new("IconStateWaiting", "Needs you", new CountV(1)),
        ]),
        new("Library", "IconFolder", null,
        [
            new(null, "Built-in", new NoneV(), Heading: true),
            new(null, "Implement", new NoneV(), Kind: NodeKind.Implement, Trail: "lib"),
            new(null, "Plan", new NoneV(), Kind: NodeKind.Plan, Trail: "lib"),
            new(null, "Architect", new NoneV(), Kind: NodeKind.Architect, Trail: "lib"),
            new(null, "Review", new NoneV(), Kind: NodeKind.Review, Trail: "lib"),
            new(null, "Approval", new NoneV(), Kind: NodeKind.Approval, Trail: "lib"),
            new(null, "Project", new NoneV(), Heading: true),
            new(null, "Storage implementer", new NoneV(), Kind: NodeKind.Implement, Library: true, Trail: "libedit"),
            new(null, "Personal", new NoneV(), Heading: true),
            new(null, "None yet", new PlainV("", "TextFaintBrush"), Trail: null),
        ]),
    ];

    public static Control Build(char layout, bool selected)
    {
        var spec = layout switch { 'A' => A, 'B' => B, _ => C };
        var root = new DockPanel { Background = Br("AppPanelBrush") };
        var band = new Border
        {
            Height = 44, Padding = new Thickness(Inset, 0), Background = Br("AppTopbarBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Br("Border80Brush"), Child = T("Inspector", 14, "TextStrongBrush", FontWeight.SemiBold),
        };
        DockPanel.SetDock(band, Dock.Top);
        root.Children.Add(band);
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body });
        root.AttachedToVisualTree += (_, _) => { };
        body.Children.Add(Header(spec, selected));
        body.Children.Add(Filter(spec));
        // A group's outer edge sits 5 px into the inset, so its border and padding put inner columns on the shared edges.
        var content = new StackPanel { Margin = spec.Grouped ? new Thickness(Inset - 5, 4, Inset - 5, 16) : new Thickness(Inset, 0, Inset, 16) };
        foreach (var s in selected ? Selected : Unselected)
        {
            content.Children.Add(Section(spec, s));
        }

        body.Children.Add(content);
        return root;
    }

    private static ColumnDefinitions Cols(Spec spec)
    {
        var label = spec.LabelFixed > 0 ? $"{spec.LabelFixed}" : spec.Grouped ? "*" : "2*";
        var value = spec.LabelFixed > 0 ? "*" : spec.Grouped ? "Auto" : "3*";
        return new ColumnDefinitions($"16,8,{label},{value},{spec.TrailW}");
    }

    private static Grid G(Spec spec) => new() { ColumnDefinitions = Cols(spec) };

    private static T2 Put<T2>(Grid g, T2 c, int col, int span = 1, int row = 0) where T2 : Control
    {
        Grid.SetColumn(c, col);
        Grid.SetColumnSpan(c, span);
        Grid.SetRow(c, row);
        g.Children.Add(c);
        return c;
    }

    private static Control Header(Spec spec, bool selected)
    {
        var g = G(spec);
        g.RowDefinitions = new RowDefinitions("Auto");
        g.Margin = new Thickness(Inset, 12, Inset, 8);
        Control tile = selected ? Tile(NodeKind.Implement, 32) : new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = Br("Border100Brush"),
            Background = Br("SurfaceMutedBrush"), Child = Icon("IconFlowchart", 18),
        };
        var title = selected ? "Implement atomic save" : "storage-change";
        Control sub = selected
            ? Row(5, Icon("IconStateWaiting", 12, "StateWaitingStrongBrush"), T("Waiting for you · attempt 3", 12, "TextMutedBrush"))
            : T("Storage workflow · 7 tasks", 12, "TextMutedBrush");
        var text = Col(1, T(title, 15, "TextStrongBrush", FontWeight.SemiBold), sub);
        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        Put(inner, tile, 0);
        Put(inner, text, 1);
        Put(g, inner, 0, 4);
        var more = Glyph("IconMore", 24, "TextMutedBrush", 16).HA(HorizontalAlignment.Right);
        more.Classes.Add("p-headermore");
        Put(g, more, 4);
        return g;
    }

    private static Control Filter(Spec spec)
    {
        var g = G(spec);
        var outer = new Border
        {
            Padding = new Thickness(Inset, 0, Inset, 8), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Br("Border80Brush"), Child = g,
        };
        var box = new TextBox { Watermark = "Filter properties", MinHeight = 28, FontSize = 13 };
        box.Classes.Add("search");
        box.InnerLeftContent = Icon("IconSearch", 14).M(8, 0, 0, 0);
        box.Classes.Add("p-filter");
        Put(g, box, 0, 4);
        Put(g, Glyph("IconFilter").HA(HorizontalAlignment.Right), 4).Classes.Add("p-filtermenu");
        return outer;
    }

    private static Control Section(Spec spec, S s)
    {
        var rows = new StackPanel();
        foreach (var f in s.Rows)
        {
            rows.Children.Add(RowFor(spec, f));
            if (spec.Grouped && f != s.Rows[^1] && !f.Heading)
            {
                rows.Children.Add(new Border { Height = 1, Background = Br("Border80Brush"), Margin = new Thickness(24, 0, 0, 0) });
            }
        }

        var head = G(spec);
        head.MinHeight = spec.Bands ? 28 : 32;
        Put(head, Icon("IconChevronDown", 12, "TextFaintBrush"), 0);
        Control title = spec.Grouped
            ? (Control)T(s.Title.ToUpperInvariant(), 11, "TextMutedBrush", FontWeight.SemiBold)
            : spec.Bands ? (Control)Row(6, Icon(s.Icon, 14, "TextMutedBrush"), T(s.Title, 12, "TextStrongBrush", FontWeight.SemiBold))
            : T(s.Title, 12, "TextStrongBrush", FontWeight.SemiBold);
        Put(head, title, 2, 2);
        if (s.Badge is { } badge)
        {
            Put(head, T(badge, 12, "TextMutedBrush").HA(HorizontalAlignment.Right), 3).Classes.Add("p-badge");
        }

        var panel = new StackPanel();
        if (spec.Bands)
        {
            panel.Children.Add(new Border
            {
                Background = Br("SurfaceMutedBrush"), Margin = new Thickness(-Inset, 0, -Inset, 4), Padding = new Thickness(Inset, 0),
                CornerRadius = new CornerRadius(0), Child = head,
            });
            rows.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(rows);
        }
        else if (spec.Grouped)
        {
            head.Margin = new Thickness(0, 12, 5, 2);
            panel.Children.Add(head);
            panel.Children.Add(new Border
            {
                Background = Br("SurfaceRaisedBrush"), BorderBrush = Br("Border80Brush"), BorderThickness = new Thickness(1),
                CornerRadius = Rad("RadiusLg"), Padding = new Thickness(4, 2, 0, 2), Child = rows,
            });
        }
        else
        {
            panel.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Br("Border80Brush"), Padding = new Thickness(0, 4, 0, 2), Child = head });
            rows.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(rows);
        }

        return panel;
    }

    private static Control RowFor(Spec spec, F f)
    {
        var g = G(spec);
        g.MinHeight = f.Heading ? 22 : spec.RowH;
        if (spec.Grouped)
        {
            // Inside a group, the trailing column ends at the group's inner edge.
            g.Margin = new Thickness(0, 0, 4, 0);
        }

        if (f.Heading)
        {
            Put(g, T(f.Label, 11, "TextFaintBrush", FontWeight.SemiBold), 0, 4).M(0, 6, 0, 0);
            return g;
        }

        Control? lead = f.Kind is { } k ? Tile(k, 16, f.Library) : f.Value is NoneV or ActionsV || f.Label.Length == 0 ? null : Icon(f.Icon, 16);
        if (f.Label == "None yet")
        {
            Put(g, T("None yet. Derive a blueprint to add one.", 12, "TextMutedBrush"), 0, 4);
            return g;
        }

        if (lead is not null)
        {
            Put(g, lead, 0);
        }

        var labelBrush = f.Value is NoneV || f.Kind is not null ? "TextStrongBrush" : "TextMutedBrush";
        var labelSize = f.Value is NoneV || f.Kind is not null ? 13 : 12;
        Control label = T(f.Label, labelSize, labelBrush);
        if (spec.Id != 'A' && f.Info is not null)
        {
            label = Row(4, label, Icon("IconInfo", 12, "TextFaintBrush"));
        }

        var trail = Trail(spec, f);
        if (trail is not null)
        {
            Put(g, trail, 4);
        }

        var narrow = spec.StackNarrow && Width < 320;
        switch (f.Value)
        {
            case NoneV:
                Put(g, label, 2, 2);
                return g;
            case ActionsV a:
                var bar = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(0, 6, 0, 2) };
                foreach (var (text, icon, cls) in a.Buttons)
                {
                    bar.Children.Add(Btn(text, icon, cls));
                }

                Put(g, bar, 0, 4);
                return g;
            case TextV t:
                g.RowDefinitions = new RowDefinitions("Auto,Auto");
                Put(g, label, 2, 2).M(0, 4, 0, 4);
                var box = Box(t.Text, multiline: true);
                box.Classes.Add("p-multiline");
                Put(g, box, 0, 4, row: 1).M(0, 0, 0, 8);
                return g;
        }

        var value = Value(spec, f.Value);
        value.Classes.Add(f.Value is CountV ? "p-count" : f.Value is PickerV ? "p-picker" : "p-value");
        if ((narrow && f.Value is PickerV or ChipsV) || (spec.Grouped && Width < 320 && f.Value is ChipsV))
        {
            g.RowDefinitions = new RowDefinitions("Auto,Auto");
            Put(g, label, 2, 2).M(0, 2, 0, 2);
            Put(g, value, 2, 2, row: 1).M(0, 0, 0, 6);
            return g;
        }

        Put(g, label, 2).M(0, 0, 8, 0);
        Put(g, value, 3);
        if (spec.Grouped)
        {
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.MaxWidth = Math.Max(90, (Width - 2 * Inset - 70) * 0.55);
        }

        return g;
    }

    private static double Width => ProtoWidth;

    public static double ProtoWidth { get; set; } = 440;

    private static Control Value(Spec spec, Val v) => v switch
    {
        PickerV p => Picker(p.Text, borderless: spec.Grouped),
        PlainV p => T(p.Text, 13, p.Brush),
        LinkV l => T(l.Text, 13, "AccentTextBrush"),
        StatusV s => Status(s),
        ChipsV c => Chips(c.Items, spec),
        CountV n => T(n.N.ToString(System.Globalization.CultureInfo.InvariantCulture), 13, "TextBrush")
            .HA(spec.LabelFixed > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right),
        _ => new Panel(),
    };

    private static Control Status(StatusV s)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        Put(g, Icon(s.Icon, 14, s.Brush), 0);
        Put(g, T(s.Text, 13, "TextBrush"), 1);
        return g;
    }

    private static Control Chips(string[] items, Spec spec)
    {
        var w = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, Margin = new Thickness(0, 3) };
        if (spec.Grouped)
        {
            w.HorizontalAlignment = HorizontalAlignment.Right;
        }

        foreach (var i in items)
        {
            w.Children.Add(Chip(i));
        }

        return w;
    }

    private static Control? Trail(Spec spec, F f)
    {
        var slots = new List<Control>();
        if (f.Changed)
        {
            slots.Add(Glyph("IconRevert", 22, "AccentStrongBrush", 14));
        }

        if (spec.Id == 'A' && f.Info is not null)
        {
            slots.Add(new Border { Width = 22, Height = 22, Child = Icon("IconInfo", 14, "TextFaintBrush").HA(HorizontalAlignment.Center) });
        }

        switch (f.Trail)
        {
            case "lib" or "libedit" when spec.Id == 'A':
                slots.Add(Glyph("IconAdd", 22));
                slots.Add(Glyph("IconMore", 22));
                break;
            case "lib" or "libedit" when spec.Id == 'B':
                slots.Add(Glyph("IconMore", 22));
                break;
            case "lib" or "libedit":
                slots.Add(Glyph("IconChevronRight", 22, "TextFaintBrush", 12));
                break;
            case { } icon:
                slots.Add(Glyph(icon, 22, "TextFaintBrush", icon == "IconChevronRight" ? 12 : 14));
                break;
        }

        if (slots.Count == 0)
        {
            return null;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        row.Classes.Add(f.Trail is "lib" or "libedit" ? "p-libaction" : "p-trail");
        row.Children.AddRange(slots.Take(spec.TrailW > 24 ? 2 : 1));
        return row;
    }
}
