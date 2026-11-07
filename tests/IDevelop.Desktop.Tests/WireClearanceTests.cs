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
        Point Port(int node) => new(nodes[node].X + Card.Width - WorkflowCanvasViewModel.InputPortCenter.X, nodes[node].Y + WorkflowCanvasViewModel.InputPortCenter.Y);
        Point Entry(int node) => new Point(nodes[node].X, nodes[node].Y) + (Vector)WorkflowCanvasViewModel.InputPortCenter;
        // Each wire meets its cards at its own point, as the canvas places them.
        double Slot(int wire, Func<(int From, int To), int> own, Func<(int From, int To), int> far)
        {
            var port = edges.Select((edge, index) => (edge, index)).Where(pair => own(pair.edge) == own(edges[wire])).ToList();
            var row = nodes[own(edges[wire])].Y + WorkflowCanvasViewModel.InputPortCenter.Y;
            var slots = PortSlots.Of([.. port.Select(pair => nodes[far(pair.edge)].Y)], row, nodes[own(edges[wire])].Y, nodes[own(edges[wire])].Y + Card.Height + reach);
            return slots[port.FindIndex(pair => pair.index == wire)];
        }

        Point Out(int wire) => Port(edges[wire].From) + new Vector(0, Slot(wire, edge => edge.From, edge => edge.To));
        Point In(int wire) => Entry(edges[wire].To) + new Vector(0, Slot(wire, edge => edge.To, edge => edge.From));
        var wires = edges.Select((edge, index) => new WireLanes.Wire(WireRouting.Route(Out(index), In(index), cards, reach), nodes[edge.From].Y, nodes[edge.To].Y)).ToList();
        var routes = WireLanes.Spread(wires, cards);
        var curves = edges.Select((edge, index) => WireCurve.Of(Out(index), routes[index], In(index), cards)).ToArray();
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
