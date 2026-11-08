using System.Diagnostics;
using Avalonia;
using IDevelop.Desktop.Canvas;
using Xunit.Abstractions;

namespace IDevelop.Desktop.Tests;

/// <summary>Plans for the review fixture's saved layouts (ADR 0075).</summary>
public sealed class WireRouterTests(ITestOutputHelper output)
{
    public static TheoryData<string, ZoomTier> Plans => Cases();

    private static TheoryData<string, ZoomTier> Cases()
    {
        var data = new TheoryData<string, ZoomTier>();
        foreach (var fixture in WireFixtures.All)
        {
            foreach (var tier in Enum.GetValues<ZoomTier>())
            {
                data.Add(fixture.Name, tier);
            }
        }

        return data;
    }

    private static WirePlans.Plan PlanOf(string name, ZoomTier tier)
    {
        var fixture = WireFixtures.All.Single(item => item.Name == name);
        return WirePlans.Of(fixture.Nodes, fixture.Edges, tier);
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void No_wire_touches_a_card_it_does_not_join_or_a_body_it_joins(string name, ZoomTier tier) =>
        Assert.Empty(WirePlans.CardHits(PlanOf(name, tier)));

    [Theory]
    [MemberData(nameof(Plans))]
    public void Lanes_that_run_side_by_side_keep_the_least_spacing(string name, ZoomTier tier)
    {
        var plan = PlanOf(name, tier);
        var least = WireLanes.MinimumLane * ZoomTiers.WireScale(tier) - 0.01;
        var runs = plan.Drawn.SelectMany((wire, index) => wire.Route.Zip(wire.Route.Skip(1)).Select(run => (Wire: index, A: run.First, B: run.Second))).ToList();
        foreach (var a in runs)
        {
            foreach (var b in runs.Where(other => other.Wire > a.Wire))
            {
                if (Math.Abs(a.A.X - a.B.X) < 0.01 && Math.Abs(b.A.X - b.B.X) < 0.01 && Overlap(a.A.Y, a.B.Y, b.A.Y, b.B.Y) > 1)
                {
                    Assert.True(Math.Abs(a.A.X - b.A.X) >= least, $"{name}: wires {a.Wire} and {b.Wire} run {Math.Abs(a.A.X - b.A.X)} apart up or down at x {a.A.X}.");
                }

                if (Math.Abs(a.A.Y - a.B.Y) < 0.01 && Math.Abs(b.A.Y - b.B.Y) < 0.01 && Overlap(a.A.X, a.B.X, b.A.X, b.B.X) > 1)
                {
                    Assert.True(Math.Abs(a.A.Y - b.A.Y) >= least, $"{name}: wires {a.Wire} and {b.Wire} run {Math.Abs(a.A.Y - b.A.Y)} apart across at y {a.A.Y}.");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void Wires_that_share_a_port_take_separate_points_on_the_card_edge(string name, ZoomTier tier)
    {
        var plan = PlanOf(name, tier);
        var least = WireLanes.MinimumLane * ZoomTiers.WireScale(tier) - 0.01;
        foreach (var (group, side) in new[] { (plan.Wires.Select((wire, index) => (Card: wire.FromCard, Slot: plan.Drawn[index].Exit)), "output"), (plan.Wires.Select((wire, index) => (Card: wire.ToCard, Slot: plan.Drawn[index].Entry)), "input") })
        {
            foreach (var port in group.GroupBy(end => end.Card).Where(port => port.Count() > 1))
            {
                var slots = port.Select(end => end.Slot).Order().ToList();
                Assert.All(slots.Zip(slots.Skip(1)), pair => Assert.True(pair.Second - pair.First >= least, $"{name}: {side} of card {port.Key} has points {pair.First} and {pair.Second}."));
                Assert.All(slots, slot => Assert.InRange(slot, -plan.Cards[port.Key].Height, plan.Cards[port.Key].Height));
            }
        }
    }

    [Fact]
    public void A_plan_for_the_large_workflow_is_fast_and_a_drag_finds_only_the_routes_near_the_card()
    {
        var fixture = WireFixtures.Ledger;
        var router = new WireRouter();
        var (cards, wires) = WirePlans.Input(fixture.Nodes, fixture.Edges, ZoomTier.Title);
        var first = Stopwatch.StartNew();
        router.Plan(cards, wires, ZoomTiers.CardReach(ZoomTier.Title), ZoomTiers.WireScale(ZoomTier.Title));
        first.Stop();

        var timer = Stopwatch.StartNew();
        const int Steps = 200;
        var moved = cards.ToArray();
        for (var step = 0; step < Steps; step++)
        {
            moved[0] = cards[0].Translate(new Vector(step, 0));
            router.Plan(moved, wires, ZoomTiers.CardReach(ZoomTier.Title), ZoomTiers.WireScale(ZoomTier.Title));
        }

        timer.Stop();
        var perDrag = timer.Elapsed.TotalMilliseconds / Steps;
        output.WriteLine($"First plan of 50 nodes: {first.Elapsed.TotalMilliseconds:F1} ms. One drag step: {perDrag:F2} ms.");
        Assert.True(perDrag < 16, $"A drag step took {perDrag} ms.");
    }

    [Fact]
    public void Wires_cross_no_more_than_the_ports_make_them_in_the_small_fixtures()
    {
        foreach (var fixture in new[] { WireFixtures.Usage, WireFixtures.Webhook })
        {
            var plan = WirePlans.Of(fixture.Nodes, fixture.Edges, ZoomTier.Full);
            var crossings = 0;
            for (var a = 0; a < plan.Drawn.Count; a++)
            {
                for (var b = a + 1; b < plan.Drawn.Count; b++)
                {
                    crossings += WirePlans.Crossings(plan.Drawn[a].Route, plan.Drawn[b].Route);
                }
            }

            Assert.True(crossings <= 2, $"{fixture.Name} has {crossings} crossings.");
        }
    }

    private static double Overlap(double a1, double a2, double b1, double b2) =>
        Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) - Math.Max(Math.Min(a1, a2), Math.Min(b1, b2));
}

/// <summary>Builds a plan the way the canvas does, from saved locations.</summary>
internal static class WirePlans
{
    internal sealed record Plan(IReadOnlyList<Rect> Cards, IReadOnlyList<WireRouter.Wire> Wires, IReadOnlyList<WireRouter.Drawn> Drawn);

    public static (Rect[] Cards, WireRouter.Wire[] Wires) Input((double X, double Y)[] nodes, (int From, int To)[] edges, ZoomTier tier)
    {
        var reach = ZoomTiers.CardReach(tier);
        Rect[] cards = [.. nodes.Select(node => new Rect(node.X, node.Y, WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight + reach))];
        WireRouter.Wire[] wires = [.. edges.Select((edge, index) => new WireRouter.Wire(
            index,
            edge.From,
            edge.To,
            new Point(nodes[edge.From].X + WorkflowCanvasViewModel.TaskCardWidth - WorkflowCanvasViewModel.InputPortCenter.X, nodes[edge.From].Y + WorkflowCanvasViewModel.InputPortCenter.Y),
            new Point(nodes[edge.To].X, nodes[edge.To].Y) + (Vector)WorkflowCanvasViewModel.InputPortCenter,
            cards[edge.From],
            cards[edge.To],
            false))];
        return (cards, wires);
    }

    public static Plan Of((double X, double Y)[] nodes, (int From, int To)[] edges, ZoomTier tier)
    {
        var (cards, wires) = Input(nodes, edges, tier);
        return new Plan(cards, wires, new WireRouter().Plan(cards, wires, ZoomTiers.CardReach(tier), ZoomTiers.WireScale(tier)));
    }

    /// <summary>Each place where a wire's curve enters a card's whole rectangle, or its own card past the port gutters.</summary>
    public static IEnumerable<string> CardHits(Plan plan)
    {
        for (var wire = 0; wire < plan.Drawn.Count; wire++)
        {
            for (var card = 0; card < plan.Cards.Count; card++)
            {
                var own = card == plan.Wires[wire].FromCard || card == plan.Wires[wire].ToCard;
                var body = own ? plan.Cards[card].Deflate(new Thickness(11, 0)) : plan.Cards[card];
                if (plan.Drawn[wire].Curve.SelectMany(piece => Enumerable.Range(1, 63).Select(step => piece.At(step / 64.0))).Any(body.Contains))
                {
                    yield return $"wire {wire} ({plan.Wires[wire].FromCard}>{plan.Wires[wire].ToCard}) enters card {card}";
                }
            }
        }
    }

    /// <summary>How many times two routes cross.</summary>
    public static int Crossings(IReadOnlyList<Point> a, IReadOnlyList<Point> b)
    {
        var count = 0;
        for (var i = 0; i + 1 < a.Count; i++)
        {
            for (var j = 0; j + 1 < b.Count; j++)
            {
                var (p, q, r, s) = (a[i], a[i + 1], b[j], b[j + 1]);
                bool vertical = Math.Abs(p.X - q.X) < 0.01, other = Math.Abs(r.X - s.X) < 0.01;
                if (vertical != other)
                {
                    var (v1, v2, h1, h2) = vertical ? (p, q, r, s) : (r, s, p, q);
                    if (Math.Min(h1.X, h2.X) < v1.X - 0.01 && Math.Max(h1.X, h2.X) > v1.X + 0.01 && Math.Min(v1.Y, v2.Y) < h1.Y - 0.01 && Math.Max(v1.Y, v2.Y) > h1.Y + 0.01)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }
}
