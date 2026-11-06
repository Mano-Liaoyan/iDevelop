using System.Collections.Immutable;
using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>One cubic Bézier piece of a wire.</summary>
public readonly record struct WirePiece(Point Start, Point First, Point Second, Point End)
{
    /// <summary>The point <paramref name="at"/> of the way along the piece, from 0 to 1.</summary>
    public Point At(double at)
    {
        var rest = 1 - at;
        var (a, b, c, d) = (rest * rest * rest, 3 * rest * rest * at, 3 * rest * at * at, at * at * at);
        return new Point(
            a * Start.X + b * First.X + c * Second.X + d * End.X,
            a * Start.Y + b * First.Y + c * Second.Y + d * End.Y);
    }
}

/// <summary>
/// Turns a wire's orthogonal route into the curve the canvas draws (ADR 0045). The route stays the wire's plan: it says
/// which gaps the wire takes past the cards. Each up or down run of the route becomes an S curve that leaves and arrives
/// across, as a Blender or Unreal wire does, so a wire with one such run is one curve from port to port. Where that curve
/// would touch a card, the curve keeps closer to the route. Wires that share a port leave it or reach it at their own angles,
/// so no two of them share a trunk.
/// </summary>
internal static class WireCurve
{
    /// <summary>How far apart, in canvas units, wires that share a port lie at <see cref="FanRun"/> from it.</summary>
    internal const double Fan = 18;

    /// <summary>The widest fan on one side of a port, so a busy port keeps its wires near its row.</summary>
    private const double FanLimit = 54;

    /// <summary>A fan is an angle: a curve's aim moves by the fan for each this many units along its first run.</summary>
    private const double FanRun = 60;

    /// <summary>How far a long curve's aim may leave its port's row.</summary>
    private const double AimLimit = 140;

    private const double Tolerance = 0.01;

    /// <summary>How far a curve reaches along the runs on each side of a turn, in the order it tries them.</summary>
    private static readonly double[] Reaches = [double.PositiveInfinity, 120, 60, 30];

    private const int Samples = 16;

    // A card's body inside its container's 10 px port gutters, less 1 px, so a wire may leave its own port.
    private static readonly Thickness Gutter = new(11, 1);

    /// <summary>A wire's two ends and how far each aims off its port's row.</summary>
    public readonly record struct Ends(object Source, object Target, double SourceRow, double TargetRow);

    /// <summary>How far each wire aims off its row at its source and at its target, in the order given.</summary>
    public static (double Exit, double Entry)[] Fans(IReadOnlyList<Ends> wires)
    {
        var fans = new (double Exit, double Entry)[wires.Count];
        foreach (var port in Enumerable.Range(0, wires.Count).GroupBy(wire => wires[wire].Source).Where(port => port.Count() > 1))
        {
            var order = port.OrderBy(wire => wires[wire].TargetRow).ToList();
            for (var rank = 0; rank < order.Count; rank++)
            {
                fans[order[rank]].Exit = Offset(rank, order.Count);
            }
        }

        foreach (var port in Enumerable.Range(0, wires.Count).GroupBy(wire => wires[wire].Target).Where(port => port.Count() > 1))
        {
            var order = port.OrderBy(wire => wires[wire].SourceRow).ToList();
            for (var rank = 0; rank < order.Count; rank++)
            {
                fans[order[rank]].Entry = Offset(rank, order.Count);
            }
        }

        return fans;
    }

    /// <summary>
    /// The curve from <paramref name="source"/> along <paramref name="route"/> to <paramref name="target"/>. It takes the
    /// widest reach that keeps it off every card, and the narrowest reach when none does.
    /// </summary>
    public static ImmutableArray<WirePiece> Of(Point source, IReadOnlyList<Point> route, Point target, double exit, double entry, IReadOnlyList<Rect> cards)
    {
        var points = Corners([source, .. route, target]);
        var near = Near(points, cards);
        ImmutableArray<WirePiece> curve = [];
        foreach (var reach in Reaches)
        {
            curve = Pieces(points, reach, exit, entry);
            if (!curve.Any(piece => Touches(piece, near)))
            {
                break;
            }
        }

        return curve;
    }

    /// <summary>The curve with no route and no card, as a proposal's wire and an unrouted wire take.</summary>
    public static ImmutableArray<WirePiece> Plain(Point source, Point target)
    {
        var middle = (source.X + target.X) / 2;
        return Pieces(Corners([source, new Point(middle, source.Y), new Point(middle, target.Y), target]), double.PositiveInfinity, 0, 0);
    }

    private static double Offset(int rank, int count) => Math.Clamp((rank - (count - 1) / 2.0) * Fan, -FanLimit, FanLimit);

    private static ImmutableArray<WirePiece> Pieces(List<Point> points, double reach, double exit, double entry)
    {
        var pieces = new List<WirePiece>();
        var cursor = points[0];
        var turns = Enumerable.Range(0, points.Count - 1).Where(at => IsUpOrDown(points[at], points[at + 1])).ToList();
        foreach (var at in turns)
        {
            // A wire whose card has no place yet may start or end on an up or down run, with no run across beside it.
            var (top, bottom) = (points[at], points[at + 1]);
            var (before, after) = (at > 0 ? points[at - 1] : top, at + 2 < points.Count ? points[at + 2] : bottom);
            var (into, outOf) = (Across(before, top), Across(bottom, after));
            var (runIn, runOut) = (Math.Abs(top.X - before.X), Math.Abs(after.X - bottom.X));
            // Two curves share a run between them. The wire's first and last curves have their outer runs to themselves.
            var reachIn = Math.Min(reach, at == turns[0] ? runIn : runIn / 2);
            var reachOut = Math.Min(reach, at == turns[^1] ? runOut : runOut / 2);
            var (start, end) = (top - new Vector(into * reachIn, 0), bottom + new Vector(outOf * reachOut, 0));
            Line(pieces, cursor, start);
            if (into == outOf)
            {
                pieces.Add(new WirePiece(start, top, bottom, end));
            }
            else
            {
                // A wire that turns back rounds the turn, and the depth of the round grows with the height of the turn.
                var depth = Math.Max(Math.Max(reachIn, reachOut), Math.Min(Math.Abs(bottom.Y - top.Y) / 2, Reaches[1]));
                pieces.Add(new WirePiece(start, start + new Vector(into * depth, 0), end + new Vector(into * depth, 0), end));
            }

            cursor = end;
        }

        Line(pieces, cursor, points[^1]);
        if (pieces.Count == 0)
        {
            return [new WirePiece(points[0], points[0], points[0], points[0])];
        }

        pieces[0] = pieces[0] with { First = pieces[0].First + new Vector(0, Aim(exit, pieces[0].First, pieces[0].Start)) };
        pieces[^1] = pieces[^1] with { Second = pieces[^1].Second + new Vector(0, Aim(entry, pieces[^1].Second, pieces[^1].End)) };
        return [.. pieces];
    }

    // A fixed offset would vanish on a long curve, so the offset grows with the run and every wire keeps its angle at the port.
    private static double Aim(double fan, Point aim, Point port) =>
        Math.Clamp(fan * Math.Max(Math.Abs(aim.X - port.X), FanRun) / FanRun, -AimLimit, AimLimit);

    private static int Across(Point from, Point to) => to.X < from.X - Tolerance ? -1 : 1;

    private static void Line(List<WirePiece> pieces, Point from, Point to)
    {
        if (Point.Distance(from, to) > Tolerance)
        {
            pieces.Add(new WirePiece(from, from + (to - from) / 3, from + (to - from) * 2 / 3, to));
        }
    }

    private static bool IsUpOrDown(Point a, Point b) => Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) > Tolerance;

    // The points where the path turns, with both ends. A route's stub ends lie on the line from its ports.
    private static List<Point> Corners(IEnumerable<Point> points)
    {
        var kept = new List<Point>();
        foreach (var point in points)
        {
            if (kept.Count > 0 && Point.Distance(kept[^1], point) <= Tolerance)
            {
                continue;
            }

            if (kept.Count >= 2 && Straight(kept[^2], kept[^1], point))
            {
                kept[^1] = point;
            }
            else
            {
                kept.Add(point);
            }
        }

        return kept;

        static bool Straight(Point a, Point b, Point c) =>
            (Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(b.X - c.X) <= Tolerance) || (Math.Abs(a.Y - b.Y) <= Tolerance && Math.Abs(b.Y - c.Y) <= Tolerance);
    }

    // The card bodies a curve near these points could touch, so a long list of cards costs a wire little.
    private static Rect[] Near(List<Point> points, IReadOnlyList<Rect> cards)
    {
        var (left, right) = (points.Min(point => point.X), points.Max(point => point.X));
        var (top, bottom) = (points.Min(point => point.Y), points.Max(point => point.Y));
        var around = new Rect(left, top, right - left, bottom - top).Inflate(Reaches[1]);
        return [.. cards.Where(around.Intersects).Select(card => card.Deflate(Gutter))];
    }

    private static bool Touches(WirePiece piece, Rect[] cards)
    {
        for (var sample = 1; sample < Samples; sample++)
        {
            var point = piece.At((double)sample / Samples);
            if (cards.Any(card => card.Contains(point)))
            {
                return true;
            }
        }

        return false;
    }
}
