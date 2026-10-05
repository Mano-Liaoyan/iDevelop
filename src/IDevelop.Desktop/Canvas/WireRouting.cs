using System.Collections.Immutable;
using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// The path a wire takes from an output port to an input port. It keeps the step the canvas has always drawn, out of the
/// output, across at the midpoint, and into the input, unless that step passes behind a card. Then it takes the
/// orthogonal detour around every card that weighs least in length and turns. Of those it takes the one that runs least
/// away from its own ports' rows and stub columns, so a wire never seems to come out of a card it does not join, and
/// then the one that passes below the cards rather than above.
/// </summary>
internal static class WireRouting
{
    /// <summary>
    /// How far a wire runs straight out of a port before it turns: the connection's 6 px offset to the edge of the
    /// handle and its 20 px spacing, both set in CanvasStyles.axaml.
    /// </summary>
    public const double Stub = 26;

    // How far a detour keeps from a card's container. A stub ends exactly this far outside its own card's container.
    private const double Clearance = 16;

    // One turn weighs as much as this much length, so a detour turns no more than it has to.
    private const double TurnCost = 60;

    private const double Tolerance = 0.01;

    /// <summary>The bounds of a card whose container sits at the location, its ports included.</summary>
    public static Rect Card(Point location) =>
        new(location, new Size(WorkflowCanvasViewModel.TaskCardWidth, WorkflowCanvasViewModel.TaskCardHeight));

    /// <summary>
    /// The corners of a wire from the output port centered at <paramref name="source"/> to the input port centered at
    /// <paramref name="target"/>, from the end of the source stub to the start of the target stub. <paramref name="cards"/>
    /// holds every card's bounds, the wire's own two included.
    /// </summary>
    public static ImmutableArray<Point> Route(Point source, Point target, IReadOnlyList<Rect> cards)
    {
        var start = new Point(source.X + Stub, source.Y);
        var end = new Point(target.X - Stub, target.Y);
        var step = Step(start, end);
        return cards.Any(card => Crosses(step, card)) ? Detour(start, end, cards) ?? step : step;
    }

    // Nodify's step: across at the middle when the input lies ahead, back along the middle row when it lies behind.
    private static ImmutableArray<Point> Step(Point start, Point end)
    {
        if (end.X >= start.X)
        {
            var x = (start.X + end.X) / 2;
            return Simplify([start, new Point(x, start.Y), new Point(x, end.Y), end]);
        }

        var y = (start.Y + end.Y) / 2;
        return Simplify([start, new Point(start.X, y), new Point(end.X, y), end]);
    }

    // A shortest path on the grid of lines through the stubs' ends and the cards' edges pushed out by the clearance.
    private static ImmutableArray<Point>? Detour(Point start, Point end, IReadOnlyList<Rect> cards)
    {
        // A card closer to a stub's end than the clearance gives up its clearance, so the wire can still leave.
        var walls = cards.Select(card => card.Inflate(Clearance))
            .Select((wall, index) => Inside(start, wall) || Inside(end, wall) ? cards[index] : wall)
            .ToArray();
        if (walls.Any(wall => Inside(start, wall) || Inside(end, wall)))
        {
            return null;
        }

        var xs = Lines(walls.SelectMany(wall => new[] { wall.Left, wall.Right }).Append(start.X).Append(end.X));
        var ys = Lines(walls.SelectMany(wall => new[] { wall.Top, wall.Bottom }).Append(start.Y).Append(end.Y));
        int columns = xs.Length, nodes = xs.Length * ys.Length;
        var inside = new bool[nodes];
        var blockedRight = new bool[nodes];
        var blockedDown = new bool[nodes];
        foreach (var wall in walls)
        {
            int left = Line(xs, wall.Left), right = Line(xs, wall.Right), top = Line(ys, wall.Top), bottom = Line(ys, wall.Bottom);
            for (var j = top; j <= bottom; j++)
            {
                for (var i = left; i <= right; i++)
                {
                    bool withinX = i > left && i < right, withinY = j > top && j < bottom;
                    inside[j * columns + i] |= withinX && withinY;
                    blockedRight[j * columns + i] |= i < right && withinY;
                    blockedDown[j * columns + i] |= j < bottom && withinX;
                }
            }
        }

        // A state is a node and the axis the wire arrived along: 0 across, 1 up or down. A wire leaves its output across.
        // Costs compare in order: length with turns, then length away from the wire's own lines, then length above them.
        var costs = Enumerable.Repeat((Length: double.PositiveInfinity, Drift: 0.0, Rise: 0.0), nodes * 2).ToArray();
        var previous = Enumerable.Repeat(-1, nodes * 2).ToArray();
        var queue = new PriorityQueue<int, (double Length, double Drift, double Rise)>();
        var first = (Line(ys, start.Y) * columns + Line(xs, start.X)) * 2;
        var goal = Line(ys, end.Y) * columns + Line(xs, end.X);
        costs[first] = (0, 0, 0);
        queue.Enqueue(first, costs[first]);

        (int Di, int Dj, int Axis)[] moves = [(1, 0, 0), (0, 1, 1), (-1, 0, 0), (0, -1, 1)];
        while (queue.TryDequeue(out var state, out var priority))
        {
            if (priority != costs[state])
            {
                continue;
            }

            var node = state / 2;
            if (node == goal)
            {
                return Simplify(Path(state));
            }

            int i = node % columns, j = node / columns;
            foreach (var (di, dj, axis) in moves)
            {
                int ni = i + di, nj = j + dj, next = nj * columns + ni;
                if (ni < 0 || nj < 0 || ni >= columns || nj >= ys.Length || inside[next]
                    || (di == 1 && blockedRight[node]) || (di == -1 && blockedRight[next])
                    || (dj == 1 && blockedDown[node]) || (dj == -1 && blockedDown[next]))
                {
                    continue;
                }

                var length = Math.Abs(xs[ni] - xs[i]) + Math.Abs(ys[nj] - ys[j]);
                var turns = (axis != state % 2 ? 1 : 0) + (next == goal && axis == 1 ? 1 : 0);
                var own = axis == 0 ? On(ys[j], start.Y, end.Y) : On(xs[i], start.X, end.X);
                var above = axis == 0 && ys[j] < Math.Min(start.Y, end.Y) - Tolerance;
                var cost = (
                    Length: costs[state].Length + length + turns * TurnCost,
                    Drift: costs[state].Drift + (own ? 0 : length),
                    Rise: costs[state].Rise + (above ? length : 0));
                var reached = next * 2 + axis;
                if (cost.CompareTo(costs[reached]) < 0)
                {
                    costs[reached] = cost;
                    previous[reached] = state;
                    queue.Enqueue(reached, cost);
                }
            }
        }

        return null;

        IEnumerable<Point> Path(int state)
        {
            var path = new List<Point>();
            for (; state >= 0; state = previous[state])
            {
                path.Add(new Point(xs[state / 2 % columns], ys[state / 2 / columns]));
            }

            path.Reverse();
            return path;
        }
    }

    private static bool On(double line, double first, double second) =>
        Math.Abs(line - first) <= Tolerance || Math.Abs(line - second) <= Tolerance;

    private static bool Crosses(ImmutableArray<Point> path, Rect card) =>
        path.Zip(path.Skip(1)).Any(segment =>
            Math.Max(segment.First.X, segment.Second.X) > card.Left + Tolerance && Math.Min(segment.First.X, segment.Second.X) < card.Right - Tolerance
            && Math.Max(segment.First.Y, segment.Second.Y) > card.Top + Tolerance && Math.Min(segment.First.Y, segment.Second.Y) < card.Bottom - Tolerance);

    private static bool Inside(Point point, Rect rect) =>
        point.X > rect.Left + Tolerance && point.X < rect.Right - Tolerance && point.Y > rect.Top + Tolerance && point.Y < rect.Bottom - Tolerance;

    private static double[] Lines(IEnumerable<double> values)
    {
        var lines = new List<double>();
        foreach (var value in values.Order())
        {
            if (lines.Count == 0 || value - lines[^1] > Tolerance)
            {
                lines.Add(value);
            }
        }

        return [.. lines];
    }

    private static int Line(double[] lines, double value)
    {
        var index = Array.BinarySearch(lines, value - Tolerance);
        return index >= 0 ? index : ~index;
    }

    // Drops repeated points and the middle of three points on one line, so only the ends and the turns remain.
    private static ImmutableArray<Point> Simplify(IEnumerable<Point> points)
    {
        var kept = new List<Point>();
        foreach (var point in points)
        {
            if (kept.Count > 0 && Near(kept[^1], point))
            {
                continue;
            }

            if (kept.Count >= 2 && (Same(kept[^2].X, kept[^1].X, point.X) || Same(kept[^2].Y, kept[^1].Y, point.Y)))
            {
                kept[^1] = point;
            }
            else
            {
                kept.Add(point);
            }
        }

        return [.. kept];

        static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance;

        static bool Same(double a, double b, double c) => Math.Abs(a - b) <= Tolerance && Math.Abs(b - c) <= Tolerance;
    }
}
