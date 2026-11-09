using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;
using EngineState = IDevelop.Engine.NodeState;
using Path = System.IO.Path;

namespace IDevelop.Desktop.Tests;

/// <summary>The one State Mark and the six state words (design brief S5).</summary>
public sealed class StateMarkTests
{
    private static StateMark Show(NodeState state, WaitReason why = WaitReason.None, NodeRole role = NodeRole.None, ThemeVariant? theme = null)
    {
        var mark = new StateMark { State = state, Why = why, Role = role };
        var window = new Window { Content = mark, RequestedThemeVariant = theme ?? ThemeVariant.Light };
        window.Show();
        mark.ApplyTemplate();
        window.UpdateLayout();
        return mark;
    }

    private static T Part<T>(StateMark mark, string name) where T : Control =>
        mark.GetVisualDescendants().OfType<T>().Single(part => part.Classes.Contains(name));

    private static string Source([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "IDevelop.Desktop"));

    private static string Hex(IBrush? brush) => brush is ISolidColorBrush solid ? $"#{solid.Color.ToUInt32():X8}" : "";

    private static string Token(string key) => Hex((IBrush)Application.Current!.FindResource(ThemeVariant.Light, key)!);

    [Theory]
    [InlineData(NodeState.Idle, "Not started")]
    [InlineData(NodeState.Running, "Running")]
    [InlineData(NodeState.InputRequired, "Needs you")]
    [InlineData(NodeState.Completed, "Done")]
    [InlineData(NodeState.Failed, "Failed")]
    [InlineData(NodeState.Stale, "Stale")]
    public void Each_state_has_one_word(NodeState state, string word) => Assert.Equal(word, NodeStates.Word(state));

    public static TheoryData<EngineState, string> WaitingLabels => new()
    {
        { new EngineState.InputRequired("Which file?"), "Needs you · Question" },
        { new EngineState.InputRequired(EngineState.Reply), "Needs you · Reply" },
        { new EngineState.InputRequired(EngineState.Approval), "Needs you · Approval" },
        { new EngineState.InputRequired(EngineState.Conflict), "Needs you · Conflict" },
        { new EngineState.InputRequired(EngineState.Interrupted), "Needs you · Interrupted" },
        { new EngineState.InputRequired(EngineState.RoundLimit), "Needs you · Round limit" },
    };

    [Theory]
    [MemberData(nameof(WaitingLabels))]
    public void Every_wait_says_Needs_you_and_then_its_reason(EngineState state, string label)
    {
        Assert.Equal(label, NodeStates.Label(state));
        Assert.Equal(Execution.StatusTone.Waiting, NodeStates.Tone(NodeStates.Of(state)));
    }

    [Fact]
    public void Counts_have_one_needs_you_group_that_holds_every_wait()
    {
        Assert.Equal("1 needs you", StateCounts.NeedYou(1));
        Assert.Equal("3 need you", StateCounts.NeedYou(3));
        Assert.Equal("5 of 9 · 1 running · 3 need you", new StateCounts(5, 1, 3, 0, 0, 9).Summary());
    }

    [AvaloniaFact]
    public void Not_started_is_a_hollow_ring_in_the_faint_text_color()
    {
        var disc = Part<Ellipse>(Show(NodeState.Idle), "markDisc");

        Assert.Equal(Token("TextFaintBrush"), Hex(disc.Stroke));
        Assert.Equal(1.5, disc.StrokeThickness);
        Assert.Equal("#00FFFFFF", Hex(disc.Fill));
    }

    [AvaloniaFact]
    public void Running_is_an_arc_of_270_degrees_in_the_running_color_with_no_disc_fill_or_glyph()
    {
        var mark = Show(NodeState.Running);
        var arc = Part<Arc>(mark, "markArc");

        Assert.True(arc.IsVisible);
        Assert.Equal(270, arc.SweepAngle);
        Assert.Equal(Token("StateRunningBrush"), Hex(arc.Stroke));
        Assert.Contains("state-running", arc.Classes);
        Assert.False(Part<PathIcon>(mark, "markGlyph").IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(WaitReason.Question, "IconBadgeQuestion")]
    [InlineData(WaitReason.Reply, "IconBadgeQuestion")]
    [InlineData(WaitReason.Approval, "IconBadgeApproval")]
    [InlineData(WaitReason.Conflict, "IconBadgeConflict")]
    [InlineData(WaitReason.Interrupted, "IconBadgePause")]
    [InlineData(WaitReason.RoundLimit, "IconBadgeRoundLimit")]
    public void Every_reason_keeps_the_orange_disc_and_the_glyph_tells_the_reason(WaitReason why, string glyph)
    {
        var mark = Show(NodeState.InputRequired, why);

        Assert.Equal(Token("StateWaitingStrongBrush"), Hex(Part<Ellipse>(mark, "markDisc").Fill));
        Assert.Same(Application.Current!.FindResource(glyph), Part<PathIcon>(mark, "markGlyph").Data);
    }

    [AvaloniaTheory]
    [InlineData(NodeState.Completed, "StateSuccessStrongBrush", "IconBadgeDone")]
    [InlineData(NodeState.Failed, "StateFailedStrongBrush", "IconBadgeFailed")]
    [InlineData(NodeState.Stale, "StateWarningStrongBrush", "IconBadgeStale")]
    public void A_filled_state_has_a_disc_in_its_strong_color_and_its_glyph(NodeState state, string fill, string glyph)
    {
        var mark = Show(state);

        Assert.Equal(Token(fill), Hex(Part<Ellipse>(mark, "markDisc").Fill));
        Assert.Same(Application.Current!.FindResource(glyph), Part<PathIcon>(mark, "markGlyph").Data);
    }

    [AvaloniaTheory]
    [InlineData(NodeRole.None, false)]
    [InlineData(NodeRole.UnderReview, true)]
    [InlineData(NodeRole.Proposing, true)]
    public void A_role_adds_one_6_px_accent_dot(NodeRole role, bool shows)
    {
        var dot = Part<Ellipse>(Show(NodeState.Completed, role: role), "markRole");

        Assert.Equal(shows, dot.IsVisible);
        Assert.Equal((6.0, 6.0), (dot.Width, dot.Height));
    }

    [Theory]
    [InlineData("Theme/Controls.axaml")]
    [InlineData("Theme/Kinds.axaml")]
    [InlineData("Canvas/CanvasTemplates.axaml")]
    [InlineData("Sidebar/SidebarView.axaml")]
    [InlineData("Inspector/AgentPanelView.axaml")]
    [InlineData("Conversation/ConversationView.axaml")]
    public void No_view_keeps_the_old_marks(string file)
    {
        var text = File.ReadAllText(Path.Combine(Source(), file));

        Assert.DoesNotContain("badgeDisc", text);
        Assert.DoesNotContain("StateBadge", text);
        Assert.DoesNotContain("StateApproval", text);
    }

    [Fact]
    public void Every_font_size_comes_from_the_type_ramp()
    {
        // The Graph's card text takes its sizes from ZoomTiers.Metrics through dynamic resources, in canvas units.
        var literal = new Regex("FontSize(?:=|\" Value=)\"(?<value>[^\"]+)\"");
        var offenders = Directory.EnumerateFiles(Source(), "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith("Tokens.axaml", StringComparison.Ordinal))
            .SelectMany(file => literal.Matches(File.ReadAllText(file)).Select(match => (file, value: match.Groups["value"].Value)))
            .Where(found => !found.value.Contains("StaticResource Type", StringComparison.Ordinal) && !found.value.StartsWith("{DynamicResource", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }
}
