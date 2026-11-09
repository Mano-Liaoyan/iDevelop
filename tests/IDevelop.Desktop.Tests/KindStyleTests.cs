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
            ["#FF564ADE #FF564ADE", "#FF007EAE #FF007EAE", "#FFB02FC2 #FFB02FC2", "#FF008575 #FF008575", "#FF956D51 #FF956D51", "#FF5E6B10 #FF5E6B10", "#FF6C6C70 #FF6C6C70"],
            Paint(ThemeVariant.Light));
        Assert.Equal(
            ["#FF6D7CFF #FF6D7CFF", "#FF1894C2 #FF3CD3FE", "#FFDB34F2 #FFDB34F2", "#FF009B89 #FF00DAC3", "#FFB78A66 #FFB78A66", "#FF7C8F22 #FFA9BC3F", "#FF8E8E93 #FF8E8E93"],
            Paint(ThemeVariant.Dark));
    }

    [AvaloniaFact]
    public void A_card_ring_takes_the_state_s_stroke_and_a_quiet_state_has_none()
    {
        var ring = new Border { Classes = { "stateStroke" } };
        Show(ring, ThemeVariant.Light);
        string Paint(NodeState state, WaitReason why = WaitReason.Question)
        {
            StyleClass.SetState(ring, state);
            StyleClass.SetWhy(ring, why);
            return $"{Hex(ring.BorderBrush)} {ring.BorderThickness.Top}";
        }

        Assert.Equal(
            ["#FFE5E5E5 1", "#FF0088FF 1.5", "#FFC55300 1.5", "#FFC55300 1.5", "#FFC55300 1.5", "#FFE9152D 1.5", "#FFE5E5E5 1", "#00FFFFFF 1"],
            [
                Paint(NodeState.Idle), Paint(NodeState.Running), Paint(NodeState.InputRequired), Paint(NodeState.InputRequired, WaitReason.Approval),
                Paint(NodeState.InputRequired, WaitReason.Interrupted), Paint(NodeState.Failed), Paint(NodeState.Completed), Paint(NodeState.Stale),
            ]);
    }

    [AvaloniaFact]
    public void A_stale_card_has_the_one_dashed_ring_in_the_warning_stroke()
    {
        var dashes = new Avalonia.Controls.Shapes.Rectangle { Classes = { "staleEdge" } };
        Show(dashes, ThemeVariant.Light);
        Assert.False(dashes.IsVisible);

        StyleClass.SetState(dashes, NodeState.Stale);

        Assert.True(dashes.IsVisible);
        Assert.Equal(1.5, dashes.StrokeThickness);
        Assert.Equal(((ISolidColorBrush)Application.Current!.FindResource(ThemeVariant.Light, "StateWarningStrokeBrush")!).Color, ((ISolidColorBrush)dashes.Stroke!).Color);
    }

    [AvaloniaFact]
    public void A_waiting_pill_is_orange()
    {
        var pill = new Border { Classes = { "pill", "waiting" } };
        Show(pill, ThemeVariant.Light);

        Assert.Equal(["#1AFF8D28", "#FFC55300"], [Hex(pill.Background), Hex(pill.BorderBrush)]);
    }
}
