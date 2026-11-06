using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

/// <summary>StyleClass's classes and the brushes Kinds.axaml gives them, in both themes.</summary>
public sealed class KindStyleTests
{
    private static void Show(Control content, ThemeVariant theme)
    {
        var window = new Window { Content = content, RequestedThemeVariant = theme };
        window.Show();
    }

    private static string Hex(IBrush? brush) => brush is ISolidColorBrush solid ? $"#{solid.Color.ToUInt32():X8}" : $"{brush}";

    [AvaloniaFact]
    public void A_kind_class_replaces_the_one_before_it()
    {
        var border = new Border();

        StyleClass.SetKind(border, NodeKind.Plan);
        StyleClass.SetKind(border, NodeKind.Review);
        StyleClass.SetState(border, NodeState.Failed);
        StyleClass.SetRole(border, NodeRole.Proposing);
        StyleClass.SetRole(border, null);

        Assert.Equal(["kind-review", "state-failed"], border.Classes);
    }

    [AvaloniaFact]
    public void Each_kind_paints_its_tile_and_its_stroke_and_the_stroke_is_the_increased_contrast_hue_in_light()
    {
        string[] Paint(ThemeVariant theme) => [.. Enum.GetValues<NodeKind>().Select(kind =>
        {
            var tile = new Border { Classes = { "kindTile" } };
            var wire = new Avalonia.Controls.Shapes.Path { Classes = { "kindStroke" } };
            StyleClass.SetKind(tile, kind);
            StyleClass.SetKind(wire, kind);
            Show(new StackPanel { Children = { tile, wire } }, theme);
            return $"{Hex(tile.Background)} {Hex(wire.Stroke)}";
        })];

        Assert.Equal(
            ["#FF564ADE #FF564ADE", "#FF007EAE #FF007EAE", "#FFB02FC2 #FFB02FC2", "#FF008575 #FF008575", "#FF956D51 #FF956D51", "#FFE7124D #FFE7124D", "#FF6C6C70 #FF6C6C70"],
            Paint(ThemeVariant.Light));
        Assert.Equal(
            ["#FF6D7CFF #FF6D7CFF", "#FF1894C2 #FF3CD3FE", "#FFDB34F2 #FFDB34F2", "#FF009B89 #FF00DAC3", "#FFB78A66 #FFB78A66", "#FFFF375F #FFFF375F", "#FF8E8E93 #FF8E8E93"],
            Paint(ThemeVariant.Dark));
    }

    [AvaloniaFact]
    public void A_ring_takes_its_states_color_and_only_failed_waiting_and_running_tint_the_card()
    {
        var ring = new Border { Classes = { "stateStroke" } };
        var tint = new Border { Classes = { "stateTint" } };
        var glyph = new PathIcon { Classes = { "stateIcon" } };
        Show(new StackPanel { Children = { ring, tint, glyph } }, ThemeVariant.Light);
        string Paint(NodeState state, NodeRole role = NodeRole.None)
        {
            foreach (var element in new StyledElement[] { ring, tint, glyph })
            {
                StyleClass.SetKind(element, NodeKind.Implement);
                StyleClass.SetState(element, state);
                StyleClass.SetRole(element, role);
            }

            return $"{Hex(ring.BorderBrush)} {Hex(tint.Background)} {Hex(glyph.Foreground)} {glyph.IsVisible}";
        }

        Assert.Equal(
            [
                "#FFE5E5E5  #FF564ADE False",
                "#FF0088FF #1A0088FF #FF0088FF True",
                "#FFC55300 #1AFF8D28 #FFC55300 True",
                "#FFE9152D #1AFF383C #FFE9152D True",
                "#FFE5E5E5  #FF008932 True",
                "#FF1E6EF4 #1A0088FF #FF1E6EF4 True",
            ],
            [
                Paint(NodeState.Idle), Paint(NodeState.Running), Paint(NodeState.InputRequired), Paint(NodeState.Failed),
                Paint(NodeState.Completed), Paint(NodeState.InputRequired, NodeRole.Proposing),
            ]);
    }

    [AvaloniaFact]
    public void A_stale_card_and_its_status_pill_share_one_warning_color_and_the_sync_glyph()
    {
        var ring = new Border { Classes = { "stateStroke" } };
        var tint = new Border { Classes = { "stateTint" } };
        var glyph = new PathIcon { Classes = { "stateIcon" } };
        var pillGlyph = new PathIcon();
        var pill = new Border { Classes = { "pill", "warning" }, Child = pillGlyph };
        Show(new StackPanel { Children = { ring, tint, glyph, pill } }, ThemeVariant.Light);
        foreach (var element in new StyledElement[] { ring, tint, glyph })
        {
            StyleClass.SetKind(element, NodeKind.Implement);
            StyleClass.SetState(element, NodeState.Stale);
        }

        Assert.Equal(Hex(ring.BorderBrush), Hex(pill.BorderBrush));
        Assert.Equal(Hex(glyph.Foreground), Hex(pillGlyph.Foreground));
        Assert.Same(Application.Current!.FindResource("IconStateOutOfDate"), glyph.Data);
        Assert.Same(glyph.Data, pillGlyph.Data);
    }

    [AvaloniaFact]
    public void A_waiting_pill_is_orange()
    {
        var pill = new Border { Classes = { "pill", "waiting" } };
        Show(pill, ThemeVariant.Light);

        Assert.Equal(["#1AFF8D28", "#FFC55300"], [Hex(pill.Background), Hex(pill.BorderBrush)]);
    }

    [AvaloniaFact]
    public void Each_state_after_idle_has_its_own_glyph_and_a_proposing_planner_shows_the_sparkle()
    {
        var glyph = new PathIcon { Classes = { "stateIcon" } };
        Show(glyph, ThemeVariant.Light);
        StyleClass.SetKind(glyph, NodeKind.Plan);
        Geometry? Glyph(NodeState state, NodeRole role = NodeRole.None)
        {
            StyleClass.SetState(glyph, state);
            StyleClass.SetRole(glyph, role);
            return glyph.Data;
        }

        string[] icons = ["IconStateRunning", "IconStateWaiting", "IconStateSucceeded", "IconStateFailed", "IconStateOutOfDate", "IconSparkle"];
        Assert.Equal(
            icons.Select(key => Application.Current!.FindResource(key)),
            [.. Enum.GetValues<NodeState>().Where(state => state != NodeState.Idle).Select(state => Glyph(state)), Glyph(NodeState.InputRequired, NodeRole.Proposing)]);
    }
}
