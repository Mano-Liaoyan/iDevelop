using System.Collections.Immutable;
using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Gives each wire its own lane in the free channels between the cards. A channel is the free space beside a wire's run: between two
/// columns for a run up or down, and between two rows for a run across. Lanes in a channel take an order that crosses the fewest
/// wires, and spread evenly in the channel's middle. A channel too narrow for its lanes at the least spacing reports the
/// wire that <see cref="WireRouter"/> sends to another channel. A wire's ends stay on their rows (ADR 0075).
/// </summary>
internal static class WireLanes
{
    /// <summary>The spacing between two lanes where the channel has room, at the Full tier, in canvas units.</summary>
    internal const double PreferredLane = 16;

    /// <summary>The least spacing between two lanes at the Full tier, in canvas units.</summary>
    internal const double MinimumLane = 8;

    // How far a run up or down keeps from the sides of its cards: the port's gutter and socket.
    private const double ColumnMargin = 14;

    // How far a run across keeps from the top and bottom of its cards where a channel holds more than one lane.
    private const double RowMargin = 8;

    // Runs closer than this many preferred lanes share a channel, so a wide channel leaves far runs where they are.
    private const double NearLanes = 2;

    // How far past its ends a run looks for the cards beside it.
    private const double EndReach = 1;

    private const double Tolerance = 0.01;

    /// <summary>One wire's route, its place in a stable order, and whether it may leave a full channel.</summary>
    public readonly record struct Wire(ImmutableArray<Point> Route, double Order, double TargetOrder, bool Stays = false);

    /// <summary>A channel that lacks room, and the wire that must leave it.</summary>
    public readonly record struct Overflow(int Wire, Rect Channel);

    /// <summary>Each wire's route with its lanes, in the order given, and the channels that lack room.</summary>
    public readonly record struct Spreading(ImmutableArray<Point>[] Routes, IReadOnlyList<Overflow> Overflows);

    // A run of a wire. Its ends have a place along the run and a side, -1 or 1, on which the wire's next run leaves it.
    private readonly record struct Run(int Wire, int First, double Line, double Start, double End, double StartAt, int StartSide, double EndAt, int EndSide)
    {
        public bool Spans(double at) => at > Start + Tolerance && at < End - Tolerance;

        public bool Overlaps(Run other) => Math.Min(End, other.End) - Math.Max(Start, other.Start) > Tolerance;
    }

    /// <param name="scale">How much thicker wires draw at the zoom tier, which widens the lanes as much, so they stay apart on screen.</param>
    public static Spreading Spread(IReadOnlyList<Wire> wires, IReadOnlyList<Rect> cards, double scale = 1)
    {
        var routes = wires.Select(wire => wire.Route.ToArray()).ToArray();
        var overflows = new List<Overflow>();
        Separate(wires, routes, cards, vertical: true, scale, overflows);
        Separate(wires, routes, cards, vertical: false, scale, overflows);
        // A lane that moved a wire into a card leaves that wire on its own route.
        return new([.. wires.Select((wire, index) => Hits(routes[index], cards) ? wire.Route : [.. routes[index]])], overflows);
    }

    private static void Separate(IReadOnlyList<Wire> wires, Point[][] routes, IReadOnlyList<Rect> cards, bool vertical, double scale, List<Overflow> overflows)
    {
        var runs = routes.SelectMany((route, index) => Runs(route, index, vertical)).OrderBy(run => run.Line).ThenBy(run => wires[run.Wire].Order).ThenBy(run => wires[run.Wire].TargetOrder).ToList();
        var free = runs.Select(run => Free(run, cards, vertical)).ToList();
        var near = NearLanes * PreferredLane * scale;
        bool Conflict(int a, int b) =>
            runs[a].Overlaps(runs[b]) && Math.Abs(runs[a].Line - runs[b].Line) < near
            && Math.Max(free[a].Low, free[b].Low) < Math.Min(free[a].High, free[b].High);

        foreach (var channel in Channels(runs.Count, Conflict))
        {
            var order = Ordered(channel, runs, Conflict, wires);
            var levels = new int[order.Count];
            for (var i = 0; i < order.Count; i++)
            {
                levels[i] = Enumerable.Range(0, i).Where(j => Conflict(order[i], order[j])).Select(j => levels[j] + 1).DefaultIfEmpty(0).Max();
            }

            Place(channel, order, levels, runs, free, routes, wires, vertical, scale, overflows);
        }
    }

    private static void Place(
        List<int> channel, List<int> order, int[] levels, List<Run> runs, List<(double Low, double High)> free, Point[][] routes,
        IReadOnlyList<Wire> wires, bool vertical, double scale, List<Overflow> overflows)
    {
        var lanes = levels.Max() + 1;
        var (low, high) = (channel.Max(run => free[run].Low), channel.Min(run => free[run].High));
        var margin = vertical ? ColumnMargin : RowMargin;
        var (minimum, spacing) = (MinimumLane * scale, PreferredLane * scale);
        double middle;
        if (double.IsFinite(low) && double.IsFinite(high))
        {
            var room = high - low - 2 * margin;
            if (lanes > 1)
            {
                if (room / (lanes - 1) < minimum - Tolerance)
                {
                    overflows.Add(LeaveChannel(channel, runs, low, high, wires, vertical));
                }

                spacing = Math.Max(Math.Min(spacing, room / (lanes - 1)), 2);
            }

            middle = (low + high) / 2;
        }
        else
        {
            // Open space has no middle. The lanes stay near the wires' own line and keep off the cards at the channel's closed side.
            var lines = channel.Select(run => runs[run].Line).Order().ToList();
            var half = (lanes - 1) * spacing / 2;
            middle = lines[lines.Count / 2];
            middle = Math.Max(middle, double.IsFinite(low) ? low + margin + half : middle);
            middle = Math.Min(middle, double.IsFinite(high) ? high - margin - half : middle);
        }

        for (var i = 0; i < order.Count; i++)
        {
            var run = runs[order[i]];
            var line = Inside(middle + (levels[i] - (lanes - 1) / 2.0) * spacing, free[order[i]], margin);
            var move = vertical ? new Vector(line - run.Line, 0) : new Vector(0, line - run.Line);
            routes[run.Wire][run.First] += move;
            routes[run.Wire][run.First + 1] += move;
        }
    }

    // A channel that lacks room can place a lane past the free space of one run, which keeps that run off its own cards.
    private static double Inside(double line, (double Low, double High) free, double margin)
    {
        var (low, high) = (free.Low + margin, free.High - margin);
        return low <= high ? Math.Clamp(line, low, high) : (free.Low + free.High) / 2;
    }

    // The wire that leaves a full channel is the longest one that may leave, because it has the most other ways to go.
    private static Overflow LeaveChannel(List<int> channel, List<Run> runs, double low, double high, IReadOnlyList<Wire> wires, bool vertical)
    {
        var leaver = channel.OrderBy(run => wires[runs[run].Wire].Stays).ThenByDescending(run => runs[run].End - runs[run].Start).ThenBy(run => runs[run].Wire).First();
        var (start, end) = (runs[leaver].Start, runs[leaver].End);
        var rect = vertical ? new Rect(low, start, high - low, end - start) : new Rect(start, low, end - start, high - low);
        return new Overflow(runs[leaver].Wire, rect);
    }

    // The runs of a wire that may move: each run up or down, and each run across that is not on a port's row.
    private static IEnumerable<Run> Runs(Point[] route, int wire, bool vertical)
    {
        for (var first = 0; first < route.Length - 1; first++)
        {
            var (a, b) = (route[first], route[first + 1]);
            var upOrDown = Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) > Tolerance;
            var across = Math.Abs(a.Y - b.Y) <= Tolerance && Math.Abs(a.X - b.X) > Tolerance;
            if (vertical && upOrDown)
            {
                // A route's first point comes from the output at its left, and its last point goes to the input at its right.
                var before = first > 0 ? Math.Sign(route[first - 1].X - a.X) : -1;
                var after = first + 2 < route.Length ? Math.Sign(route[first + 2].X - b.X) : 1;
                yield return new Run(wire, first, a.X, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), a.Y, before, b.Y, after);
            }
            else if (!vertical && across && first > 0 && first + 1 < route.Length - 1)
            {
                yield return new Run(wire, first, a.Y, Math.Min(a.X, b.X), Math.Max(a.X, b.X), a.X, Math.Sign(route[first - 1].Y - a.Y), b.X, Math.Sign(route[first + 2].Y - b.Y));
            }
        }
    }

    // The free space on both sides of a run, up to the nearest cards that stand beside any of its length, its ends included:
    // the wire's next run leaves from an end, so a lane there may not bring that run onto a card.
    private static (double Low, double High) Free(Run run, IReadOnlyList<Rect> cards, bool vertical)
    {
        var (low, high) = (double.NegativeInfinity, double.PositiveInfinity);
        foreach (var card in cards)
        {
            var (start, end) = vertical ? (card.Top, card.Bottom) : (card.Left, card.Right);
            if (end < run.Start - EndReach || start > run.End + EndReach)
            {
                continue;
            }

            var (near, far) = vertical ? (card.Left, card.Right) : (card.Top, card.Bottom);
            if (far <= run.Line + Tolerance)
            {
                low = Math.Max(low, far);
            }
            else if (near >= run.Line - Tolerance)
            {
                high = Math.Min(high, near);
            }
            else
            {
                return (run.Line, run.Line);
            }
        }

        return (low, high);
    }

    // Runs joined through conflicts share a channel.
    private static IEnumerable<List<int>> Channels(int count, Func<int, int, bool> conflict)
    {
        var parent = Enumerable.Range(0, count).ToArray();
        int Root(int run) => parent[run] == run ? run : parent[run] = Root(parent[run]);
        for (var a = 0; a < count; a++)
        {
            for (var b = a + 1; b < count; b++)
            {
                if (Root(a) != Root(b) && conflict(a, b))
                {
                    parent[Root(a)] = Root(b);
                }
            }
        }

        return Enumerable.Range(0, count).GroupBy(Root).Select(group => group.ToList());
    }

    // Swaps neighbours in the channel while a swap crosses fewer wires. The runs keep their stable order where a swap gains nothing.
    private static List<int> Ordered(List<int> channel, List<Run> runs, Func<int, int, bool> conflict, IReadOnlyList<Wire> wires)
    {
        var order = channel.ToList();
        for (var pass = 0; pass < order.Count; pass++)
        {
            var swapped = false;
            for (var i = 0; i + 1 < order.Count; i++)
            {
                var (a, b) = (order[i], order[i + 1]);
                if (conflict(a, b) && Crossings(runs[a], runs[b]) > Crossings(runs[b], runs[a]))
                {
                    (order[i], order[i + 1]) = (b, a);
                    swapped = true;
                }
            }

            if (!swapped)
            {
                break;
            }
        }

        return order;
    }

    // How many wires cross if run a takes a lower lane than run b. A run's next run crosses the other run where it leaves toward that run's lane.
    private static int Crossings(Run a, Run b)
    {
        var count = 0;
        count += a.StartSide > 0 && b.Spans(a.StartAt) ? 1 : 0;
        count += a.EndSide > 0 && b.Spans(a.EndAt) ? 1 : 0;
        count += b.StartSide < 0 && a.Spans(b.StartAt) ? 1 : 0;
        count += b.EndSide < 0 && a.Spans(b.EndAt) ? 1 : 0;
        return count;
    }

    private static bool Hits(Point[] route, IReadOnlyList<Rect> cards) =>
        route.Zip(route.Skip(1)).Any(run => cards.Any(card =>
            Math.Max(run.First.X, run.Second.X) > card.Left + Tolerance && Math.Min(run.First.X, run.Second.X) < card.Right - Tolerance
            && Math.Max(run.First.Y, run.Second.Y) > card.Top + Tolerance && Math.Min(run.First.Y, run.Second.Y) < card.Bottom - Tolerance));
}
