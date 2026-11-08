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
/// would touch a card, the curve keeps closer to the route. Wires that share a port leave it or reach it at their own points
/// on the card's edge (<see cref="PortSlots"/>), so no two of them share a trunk.
/// </summary>
internal static class WireCurve
{
    private const double Tolerance = 0.01;

    /// <summary>How far a curve reaches along the runs on each side of a turn, in the order it tries them.</summary>
    private static readonly double[] Reaches = [double.PositiveInfinity, 120, 60, 30, 14, 6, 2];

    private const int Samples = 64;

    // A wire's own card, inside its container's 10 px port gutters, less 1 px, so the wire may leave its own port, and past its top and
    // bottom by a wire's clear space, so a curve never runs along a card's edge where a small bend avoids it.
    private static readonly Thickness Gutter = new(11, -14);

    // The card's body alone, for a wire that has no way to keep that clear space.
    private static readonly Thickness Body = new(11, -1);

    /// <summary>
    /// The curve from <paramref name="source"/> along <paramref name="route"/> to <paramref name="target"/>. It takes the widest
    /// reach that keeps it off every card, first with a wire's clear space, then without it. The narrowest
    /// reach hugs the route, which already goes around every card, so a curve never crosses a card at any zoom.
    /// </summary>
    /// <param name="ownCards">The wire's two cards in <paramref name="cards"/>, whose port gutters the wire may cross. A wire keeps off every other card's whole rectangle.</param>
    public static ImmutableArray<WirePiece> Of(Point source, IReadOnlyList<Point> route, Point target, IReadOnlyList<Rect> cards, (int From, int To) ownCards = default)
    {
        var points = Corners([source, .. route, target]);
        ImmutableArray<WirePiece> curve = [];
        foreach (var margin in new[] { Gutter, Body })
        {
            var near = Near(points, cards, margin, ownCards);
            foreach (var reach in Reaches)
            {
                curve = Pieces(points, reach);
                if (!curve.Any(piece => Touches(piece, near)))
                {
                    return curve;
                }
            }
        }

        return curve;
    }

    /// <summary>The curve with no route and no card, as a proposal's wire and an unrouted wire take.</summary>
    public static ImmutableArray<WirePiece> Plain(Point source, Point target)
    {
        var middle = (source.X + target.X) / 2;
        return Pieces(Corners([source, new Point(middle, source.Y), new Point(middle, target.Y), target]), double.PositiveInfinity);
    }

    private static ImmutableArray<WirePiece> Pieces(List<Point> points, double reach)
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
                var depth = Math.Min(reach, Math.Max(Math.Max(reachIn, reachOut), Math.Min(Math.Abs(bottom.Y - top.Y) / 2, Reaches[1])));
                pieces.Add(new WirePiece(start, start + new Vector(into * depth, 0), end + new Vector(into * depth, 0), end));
            }

            cursor = end;
        }

        Line(pieces, cursor, points[^1]);
        if (pieces.Count == 0)
        {
            return [new WirePiece(points[0], points[0], points[0], points[0])];
        }

        return [.. pieces];
    }

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
    private static Rect[] Near(List<Point> points, IReadOnlyList<Rect> cards, Thickness margin, (int From, int To) ownCards)
    {
        var (left, right) = (points.Min(point => point.X), points.Max(point => point.X));
        var (top, bottom) = (points.Min(point => point.Y), points.Max(point => point.Y));
        var around = new Rect(left, top, right - left, bottom - top).Inflate(Reaches[1]);
        var others = new Thickness(0, margin.Top);
        return [.. cards.Select((card, index) => (card, index)).Where(pair => around.Intersects(pair.card))
            .Select(pair => pair.card.Deflate(pair.index == ownCards.From || pair.index == ownCards.To ? margin : others))];
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
