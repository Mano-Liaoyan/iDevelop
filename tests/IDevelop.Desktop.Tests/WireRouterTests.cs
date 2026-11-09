using System.Diagnostics;
using Avalonia;
using IDevelop.Desktop.Canvas;
using Xunit.Abstractions;

namespace IDevelop.Desktop.Tests;

/// <summary>Plans for the review fixture's saved layouts (ADR 0075).</summary>
public sealed class WireRouterTests(ITestOutputHelper output)
{
    public static TheoryData<string> Plans => [.. WireFixtures.All.Select(fixture => fixture.Name)];

    private static WirePlans.Plan PlanOf(string name)
    {
        var fixture = WireFixtures.All.Single(item => item.Name == name);
        return WirePlans.Of(fixture.Nodes, fixture.Edges);
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void No_wire_touches_a_card_it_does_not_join_or_a_body_it_joins(string name) =>
        Assert.Empty(WirePlans.CardHits(PlanOf(name)));

    [Theory]
    [MemberData(nameof(Plans))]
    public void Lanes_of_different_outputs_keep_the_least_spacing_side_by_side(string name)
    {
        var plan = PlanOf(name);
        var runs = plan.Drawn.SelectMany((wire, index) => wire.Route.Zip(wire.Route.Skip(1)).Select(run => (Wire: index, A: run.First, B: run.Second))).ToList();
        foreach (var a in runs)
        {
            foreach (var b in runs.Where(other => other.Wire > a.Wire && plan.Wires[other.Wire].FromCard != plan.Wires[a.Wire].FromCard))
            {
                if (Math.Abs(a.A.X - a.B.X) < 0.01 && Math.Abs(b.A.X - b.B.X) < 0.01 && Overlap(a.A.Y, a.B.Y, b.A.Y, b.B.Y) > 1)
                {
                    Assert.True(Math.Abs(a.A.X - b.A.X) >= WireLanes.MinimumColumnLane - 0.01, $"{name}: wires {a.Wire} and {b.Wire} run {Math.Abs(a.A.X - b.A.X)} apart up or down at x {a.A.X}.");
                }

                if (Math.Abs(a.A.Y - a.B.Y) < 0.01 && Math.Abs(b.A.Y - b.B.Y) < 0.01 && Overlap(a.A.X, a.B.X, b.A.X, b.B.X) > 1)
                {
                    Assert.True(Math.Abs(a.A.Y - b.A.Y) >= WireLanes.MinimumRowLane - 0.01, $"{name}: wires {a.Wire} and {b.Wire} run {Math.Abs(a.A.Y - b.A.Y)} apart across at y {a.A.Y}.");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void The_wires_of_one_output_leave_its_socket_on_one_trunk(string name)
    {
        var plan = PlanOf(name);
        foreach (var output in plan.Wires.Select((wire, index) => (wire, index)).GroupBy(pair => pair.wire.FromCard).Where(group => group.Count() > 1))
        {
            var trunks = output.Select(pair => FirstVertical(plan.Drawn[pair.index].Route)).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
            var near = trunks.GroupBy(x => Math.Round(x / 40)).Max(group => group.Count());
            Assert.True(trunks.Count == 0 || near == 1 || trunks.Max() - trunks.Min() > 40, $"{name}: output {output.Key} leaves on {trunks.Count} trunks.");
        }

        static double? FirstVertical(IReadOnlyList<Point> route) => route.Zip(route.Skip(1)).Where(run => Math.Abs(run.First.X - run.Second.X) < 0.01).Select(run => (double?)run.First.X).FirstOrDefault();
    }

    [Fact]
    public void A_plan_for_the_large_workflow_is_fast_and_a_drag_finds_only_the_routes_near_the_card()
    {
        var fixture = WireFixtures.Ledger;
        var router = new WireRouter();
        var (cards, wires) = WirePlans.Input(fixture.Nodes, fixture.Edges);
        var first = Stopwatch.StartNew();
        router.Plan(cards, wires);
        first.Stop();

        var timer = Stopwatch.StartNew();
        const int Steps = 200;
        Assert.True(first.Elapsed.TotalMilliseconds < 200, $"The first plan took {first.Elapsed.TotalMilliseconds} ms.");
        var moved = cards.ToArray();
        for (var step = 0; step < Steps; step++)
        {
            moved[0] = cards[0].Translate(new Vector(step, 0));
            router.Plan(moved, wires);
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
            var plan = WirePlans.Of(fixture.Nodes, fixture.Edges);
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

    /// <summary>The cards at their compacted positions on the Map, as the Graph shows them.</summary>
    public static (Rect[] Cards, WireRouter.Wire[] Wires) Input((double X, double Y)[] nodes, (int From, int To)[] edges)
    {
        var space = GraphSpace.Of([.. nodes.Select(node => new IDevelop.Workflows.CanvasPoint(node.X, node.Y))]);
        var shown = nodes.Select(node => space.ToDisplay(new IDevelop.Workflows.CanvasPoint(node.X, node.Y))).ToArray();
        Rect[] cards = [.. shown.Select(at => new Rect(at.X, at.Y, WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight))];
        WireRouter.Wire[] wires = [.. edges.Select((edge, index) => new WireRouter.Wire(
            index,
            edge.From,
            edge.To,
            shown[edge.From] + (Vector)WorkflowCanvasViewModel.OutputPortCenter,
            shown[edge.To] + (Vector)WorkflowCanvasViewModel.InputPortCenter,
            cards[edge.From],
            cards[edge.To],
            false))];
        return (cards, wires);
    }

    public static Plan Of((double X, double Y)[] nodes, (int From, int To)[] edges)
    {
        var (cards, wires) = Input(nodes, edges);
        return new Plan(cards, wires, new WireRouter().Plan(cards, wires));
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
