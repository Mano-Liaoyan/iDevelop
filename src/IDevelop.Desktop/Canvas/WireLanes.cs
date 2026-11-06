using System.Collections.Immutable;
using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Moves apart the wires that share a run, so a person can trace each one. Every wire on a run takes its own lane, 10 px from
/// the next, in order of its source position and then its target position, so no wires share a trunk (ADR 0045). A wire's
/// ends stay on its ports' rows. A wire that its lane would take through a card stays where it was.
/// </summary>
internal static class WireLanes
{
    private const double Lane = 10;

    private const double Tolerance = 0.01;

    /// <summary>One wire's route and its place in the order of lanes.</summary>
    public readonly record struct Wire(ImmutableArray<Point> Route, double Order, double TargetOrder);

    private readonly record struct Run(int Wire, int First, double Line, double Start, double End);

    /// <summary>Each wire's route, in the order given.</summary>
    public static ImmutableArray<Point>[] Spread(IReadOnlyList<Wire> wires, IReadOnlyList<Rect> cards)
    {
        var moves = wires.Select(wire => new Vector[wire.Route.Length]).ToArray();
        Separate(wires, moves, vertical: true);
        Separate(wires, moves, vertical: false);
        return [.. wires.Select((wire, index) =>
        {
            var moved = wire.Route.Select((point, at) => point + moves[index][at]).ToImmutableArray();
            return Crosses(moved, cards) ? wire.Route : moved;
        })];
    }

    private static void Separate(IReadOnlyList<Wire> wires, Vector[][] moves, bool vertical)
    {
        var runs = wires.SelectMany((wire, index) => Runs(wire.Route, index, vertical)).OrderBy(run => run.Line).ThenBy(run => run.Start).ToList();
        foreach (var shared in Shared(runs))
        {
            var lanes = shared.Select(run => run.Wire).Distinct().OrderBy(wire => wires[wire].Order).ThenBy(wire => wires[wire].TargetOrder).ToList();
            foreach (var run in shared)
            {
                var offset = (lanes.IndexOf(run.Wire) - (lanes.Count - 1) / 2.0) * Lane;
                var move = vertical ? new Vector(offset, 0) : new Vector(0, offset);
                moves[run.Wire][run.First] += move;
                moves[run.Wire][run.First + 1] += move;
            }
        }
    }

    // A wire's runs that may move: a vertical one always, and a horizontal one when it does not end at a stub on a port's row.
    private static IEnumerable<Run> Runs(ImmutableArray<Point> route, int wire, bool vertical)
    {
        for (var first = 0; first < route.Length - 1; first++)
        {
            var (a, b) = (route[first], route[first + 1]);
            if (vertical && Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) > Tolerance)
            {
                yield return new Run(wire, first, a.X, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
            }
            else if (!vertical && first > 0 && first + 1 < route.Length - 1 && Math.Abs(a.Y - b.Y) <= Tolerance && Math.Abs(a.X - b.X) > Tolerance)
            {
                yield return new Run(wire, first, a.Y, Math.Min(a.X, b.X), Math.Max(a.X, b.X));
            }
        }
    }

    // Runs on one line whose lengths overlap. The runs arrive by line and then by start.
    private static IEnumerable<List<Run>> Shared(List<Run> runs)
    {
        var group = new List<Run>();
        var end = double.NegativeInfinity;
        foreach (var run in runs)
        {
            if (group.Count > 0 && (Math.Abs(run.Line - group[0].Line) >= Lane || run.Start >= end - Tolerance))
            {
                yield return group;
                group = [];
                end = double.NegativeInfinity;
            }

            group.Add(run);
            end = Math.Max(end, run.End);
        }

        if (group.Count > 0)
        {
            yield return group;
        }
    }

    private static bool Crosses(ImmutableArray<Point> route, IReadOnlyList<Rect> cards) =>
        route.Zip(route.Skip(1)).Any(run => cards.Any(card =>
            Math.Max(run.First.X, run.Second.X) > card.Left + Tolerance && Math.Min(run.First.X, run.Second.X) < card.Right - Tolerance
            && Math.Max(run.First.Y, run.Second.Y) > card.Top + Tolerance && Math.Min(run.First.Y, run.Second.Y) < card.Bottom - Tolerance));
}
