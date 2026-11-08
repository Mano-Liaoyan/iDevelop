using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

/// <summary>A card never grows into the card under it, at any tier, at the saved positions of the review fixture's workflows (ADR 0071).</summary>
public sealed class CardGapTests
{
    private const double X = 340;
    private const double Y = 190;

    /// <summary>The 50-node workflow: eight columns of 4, 6, 8, 6, 8, 8, 6, and 4 cards, one row apart.</summary>
    private static readonly Point[] Large =
    [
        .. new[] { 4, 6, 8, 6, 8, 8, 6, 4 }.SelectMany((rows, column) => Enumerable.Range(0, rows).Select(row => new Point(column * X, row * Y))),
    ];

    /// <summary>The 9-node workflow, with its cards on three rows.</summary>
    private static readonly Point[] Small =
    [
        new(0, Y), new(X, 0), new(X, Y), new(X, 2 * Y), new(2 * X, 0), new(2 * X, Y), new(3 * X, 0), new(3 * X, Y), new(4 * X, Y),
    ];

    public static TheoryData<string, double> Zooms()
    {
        var data = new TheoryData<string, double>();
        foreach (var name in new[] { nameof(Large), nameof(Small) })
        {
            // One zoom or more in each tier, from Full down to the canvas's least zoom, and the overview zooms that a fit takes.
            foreach (var zoom in new[] { 1.0, 0.7, 0.55, 0.4, 0.36, 0.352, 0.32, 0.3, 0.276, 0.26, 0.2448, 0.23, 0.2, 0.15, 0.1 })
            {
                data.Add(name, zoom);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Zooms))]
    public void No_card_touches_the_card_under_it(string workflow, double zoom)
    {
        var cards = workflow == nameof(Large) ? Large : Small;
        var tier = ZoomTiers.Next(ZoomTier.Full, zoom);
        var pitch = ZoomTiers.RowPitch(cards);
        var reach = tier switch
        {
            ZoomTier.Title => ZoomTiers.TitleText(zoom, ZoomTier.Title, pitch).Reach,
            ZoomTier.Block => ZoomTiers.TitleText(zoom, ZoomTier.Block).Reach,
            _ => ZoomTiers.CardReach(tier),
        };

        var gap = (pitch - WorkflowCanvasViewModel.TaskCardHeight - reach) * zoom;

        Assert.True(gap >= (tier == ZoomTier.Title ? ZoomTiers.CardGap : 3), $"At {zoom} ({tier}) the gap is {gap:0.0} px.");
    }

    [Theory]
    [InlineData(0.36)]
    [InlineData(0.3)]
    [InlineData(0.276)]
    [InlineData(0.2448)]
    [InlineData(0.23)]
    public void A_title_tier_chip_row_keeps_4_px_under_the_last_title_line_and_its_descenders(double zoom)
    {
        var text = ZoomTiers.TitleText(zoom, ZoomTier.Title, Y);
        var lastLine = ZoomTiers.TitleTop + text.Lines * text.Line;

        Assert.True((text.ChipTop - lastLine) * zoom >= 4, $"At {zoom} the chip row is {(text.ChipTop - lastLine) * zoom:0.0} px under the title.");
        Assert.Equal(64 + text.Reach, text.ChipTop + ZoomTiers.ChipRow + 5);
    }
}
