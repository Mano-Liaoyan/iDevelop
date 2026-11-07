using System.Collections.Immutable;
using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

/// <summary>No wire crosses a card's body, in any zoom tier.</summary>
public sealed class WireClearanceTests
{
    public static TheoryData<ZoomTier> Tiers => [.. Enum.GetValues<ZoomTier>()];

    private static readonly Size Card = new(WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight);

    /// <summary>The curves the canvas draws for these cards and connections, and the bodies they must keep out of.</summary>
    private static (ImmutableArray<WirePiece>[] Curves, Rect[] Bodies) Draw((double X, double Y)[] nodes, (int From, int To)[] edges, double reach)
    {
        Rect[] cards = [.. nodes.Select(node => new Rect(new Point(node.X, node.Y), Card + new Size(0, reach)))];
        Point Out(int node) => new(nodes[node].X + Card.Width - WorkflowCanvasViewModel.InputPortCenter.X, nodes[node].Y + WorkflowCanvasViewModel.InputPortCenter.Y);
        Point In(int node) => new Point(nodes[node].X, nodes[node].Y) + (Vector)WorkflowCanvasViewModel.InputPortCenter;
        var wires = edges.Select(edge => new WireLanes.Wire(WireRouting.Route(Out(edge.From), In(edge.To), cards, reach), nodes[edge.From].Y, nodes[edge.To].Y)).ToList();
        var routes = WireLanes.Spread(wires, cards);
        var fans = WireCurve.Fans([.. edges.Select(edge => new WireCurve.Ends(edge.From, edge.To, Out(edge.From).Y, In(edge.To).Y))]);
        var curves = edges.Select((edge, index) => WireCurve.Of(Out(edge.From), routes[index], In(edge.To), fans[index].Exit, fans[index].Entry, cards)).ToArray();
        return (curves, [.. cards.Select(card => card.Deflate(new Thickness(11, 0)))]);
    }

    private static IEnumerable<string> Crossings((double X, double Y)[] nodes, (int From, int To)[] edges, double reach)
    {
        var (curves, bodies) = Draw(nodes, edges, reach);
        for (var wire = 0; wire < curves.Length; wire++)
        {
            for (var body = 0; body < bodies.Length; body++)
            {
                if (curves[wire].SelectMany(piece => Enumerable.Range(0, 65).Select(step => piece.At(step / 64.0))).Any(bodies[body].Contains))
                {
                    yield return $"{edges[wire].From}>{edges[wire].To} crosses {body}";
                }
            }
        }
    }

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

            Assert.Empty(Crossings(nodes, edges, ZoomTiers.CardReach(tier)));
        }
    }
}
