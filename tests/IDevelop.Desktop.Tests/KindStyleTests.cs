using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

/// <summary>StyleClass's classes and the brushes Kinds.axaml gives them, in both themes.</summary>
public sealed class KindStyleTests
{
    private static Window Show(Control content, ThemeVariant theme)
    {
        var window = new Window { Content = content, RequestedThemeVariant = theme };
        window.Show();
        return window;
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
            ["#FF564ADE #FF564ADE", "#FF007EAE #FF007EAE", "#FFB02FC2 #FFB02FC2", "#FF008575 #FF008575", "#FF956D51 #FF956D51", "#FF6C6C70 #FF6C6C70"],
            Paint(ThemeVariant.Light));
        Assert.Equal(
            ["#FF6D7CFF #FF6D7CFF", "#FF007EAE #FF3CD3FE", "#FFDB34F2 #FFDB34F2", "#FF008575 #FF00DAC3", "#FFB78A66 #FFB78A66", "#FF8E8E93 #FF8E8E93"],
            Paint(ThemeVariant.Dark));
    }

    [AvaloniaFact]
    public void A_ring_takes_its_kinds_hue_while_running_its_states_hue_after_and_the_accent_while_proposing()
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
                "#CCE5E5E5 #00FFFFFF #FF564ADE False",
                "#FF564ADE #1A6155F5 #FF564ADE True",
                "#FFC55300 #1AFF8D28 #FFC55300 True",
                "#FFE9152D #1AFF383C #FFE9152D True",
                "#FF008932 #00FFFFFF #FF008932 True",
                "#FF1E6EF4 #1A0088FF #FF1E6EF4 True",
            ],
            [
                Paint(NodeState.Idle), Paint(NodeState.Running), Paint(NodeState.Waiting), Paint(NodeState.Failed),
                Paint(NodeState.Succeeded), Paint(NodeState.Waiting, NodeRole.Proposing),
            ]);
    }

    [AvaloniaFact]
    public void A_waiting_card_its_ring_and_its_pill_are_all_orange()
    {
        var card = new Border { Classes = { "card", "waiting" } };
        var pill = new Border { Classes = { "pill", "waiting" } };
        var panel = new StackPanel { Children = { card, pill } };
        panel.Styles.Add(new StyleInclude((Uri?)null) { Source = new Uri("avares://IDevelop.Desktop/Canvas/CanvasStyles.axaml") });
        Show(panel, ThemeVariant.Light);

        Assert.Equal(
            ["#1AFF8D28", "#8CFF8D28", "#26FF8D28", "#1AFF8D28", "#66FF8D28"],
            [Hex(card.Background), Hex(card.BorderBrush), $"#{card.BoxShadow[0].Color.ToUInt32():X8}", Hex(pill.Background), Hex(pill.BorderBrush)]);
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

        string[] icons =
        [
            "IconStateNeedsSetup", "IconStateRunning", "IconStateElsewhere", "IconStop", "IconStateWaiting", "IconStateInReview",
            "IconStateSucceeded", "IconStateFailed", "IconStateInterrupted", "IconStateCancelled", "IconSparkle",
        ];
        Assert.Equal(
            icons.Select(key => Application.Current!.FindResource(key)),
            [.. Enum.GetValues<NodeState>().Where(state => state != NodeState.Idle).Select(state => Glyph(state)), Glyph(NodeState.Waiting, NodeRole.Proposing)]);
    }
}
