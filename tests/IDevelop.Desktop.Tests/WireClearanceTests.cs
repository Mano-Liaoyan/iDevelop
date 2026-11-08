using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

/// <summary>Random layouts: no wire crosses a card, in any zoom tier.</summary>
public sealed class WireClearanceTests
{
    public static TheoryData<ZoomTier> Tiers => [.. Enum.GetValues<ZoomTier>()];

    /// <summary>Cards on a loose grid at a layout's closest pitch, joined at random, forward and back.</summary>
    private static ((double X, double Y)[] Nodes, (int From, int To)[] Edges) Random(int seed, double columns, double rows)
    {
        var random = new Random(seed);
        var nodes = Enumerable.Range(0, 12).Select(node => ((node % 4) * columns + random.Next(0, 3) * 15, (node / 4) * rows + random.Next(0, 3) * 15)).ToArray();
        var edges = Enumerable.Range(0, 14).Select(_ => (random.Next(12), random.Next(12))).Where(edge => edge.Item1 != edge.Item2).Distinct().ToArray();
        return (nodes, edges);
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void No_wire_crosses_a_card_in_any_zoom_tier(ZoomTier tier)
    {
        foreach (var seed in Enumerable.Range(0, 200))
        {
            var (nodes, edges) = Random(seed, 340, 190);
            var plan = WirePlans.Of(nodes, edges, tier);

            Assert.Empty(WirePlans.CardHits(plan));
        }
    }
}
