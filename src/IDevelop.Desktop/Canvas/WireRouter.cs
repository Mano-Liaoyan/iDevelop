using System.Collections.Immutable;
using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Plans all the wires of a canvas together (ADR 0075). A plan finds each wire's route past the
/// cards, gives each wire a lane in the channels it passes, and turns each route into the curve the canvas draws. A channel that lacks room for its
/// lanes sends a wire to another channel. The router keeps every route that a change of the cards cannot reach, so a drag finds only the routes near the moved card.
/// </summary>
internal sealed class WireRouter
{
    // The most times a plan sends wires out of full channels.
    private const int MostRounds = 8;

    // The room around a kept route that a moved card must leave free, so the route can stay.
    private const double KeptClearance = 60;

    // How much longer a route may grow to leave a full channel.
    private const double MostDetour = 900;

    private readonly Dictionary<object, Kept> _kept = [];
    private readonly Dictionary<object, List<Rect>> _closed = [];
    private Rect[] _cards = [];

    /// <summary>One wire to plan. <paramref name="FromCard"/> and <paramref name="ToCard"/> index the card list.</summary>
    /// <param name="Key">The wire's identity between plans.</param>
    /// <param name="FromCard">The index of the card the wire leaves.</param>
    /// <param name="ToCard">The index of the card the wire enters.</param>
    /// <param name="Output">The center of the card's output port.</param>
    /// <param name="Input">The center of the card's input port.</param>
    /// <param name="FromRect">The bounds of the card the wire leaves, where its port is.</param>
    /// <param name="ToRect">The bounds of the card the wire enters, where its port is.</param>
    /// <param name="Active">The wire feeds a running node, so it stays in its channel.</param>
    public readonly record struct Wire(object Key, int FromCard, int ToCard, Point Output, Point Input, Rect FromRect, Rect ToRect, bool Active);

    /// <summary>The result for one wire: its route and its curve.</summary>
    public readonly record struct Drawn(ImmutableArray<Point> Route, ImmutableArray<WirePiece> Curve);

    private readonly record struct Kept(Point Source, Point Target, ImmutableArray<Point> Route, Rect Bounds);

    public IReadOnlyList<Drawn> Plan(IReadOnlyList<Rect> cards, IReadOnlyList<Wire> wires)
    {
        Forget(cards, wires);
        var routes = Routes(cards, wires);
        var stay = new bool[wires.Count];
        var spread = Spread(wires, routes, stay, cards);
        for (var round = 0; round < MostRounds && spread.Overflows.Count > 0; round++)
        {
            foreach (var overflow in spread.Overflows)
            {
                var index = overflow.Wire;
                var wire = wires[index];
                var closed = Closed(wire.Key);
                closed.Add(overflow.Channel);
                var route = WireRouting.Route(wire.Output, wire.Input, Owned(cards, wire), closed);
                // A wire that the next free channel would take far around stays and shares the full channel's room with its neighbours.
                if (Length(route) > Length(routes[index]) + MostDetour)
                {
                    closed.RemoveAt(closed.Count - 1);
                    stay[index] = true;
                }
                else
                {
                    routes[index] = route;
                    _kept[wire.Key] = new Kept(wire.Output, wire.Input, route, BoundsOf(wire.Output, wire.Input, route));
                }
            }

            spread = Spread(wires, routes, stay, cards);
        }

        return [.. wires.Select((wire, index) =>
        {
            var curve = WireCurve.Of(wire.Output, spread.Routes[index], wire.Input, Owned(cards, wire), (wire.FromCard, wire.ToCard));
            return new Drawn(spread.Routes[index], curve);
        })];
    }

    private static WireLanes.Spreading Spread(IReadOnlyList<Wire> wires, ImmutableArray<Point>[] routes, bool[] stay, IReadOnlyList<Rect> cards) =>
        WireLanes.Spread([.. wires.Select((wire, index) => new WireLanes.Wire(routes[index], wire.FromRect.Top + wire.FromRect.Left / 100000, wire.ToRect.Top + wire.ToRect.Left / 100000, wire.Active || stay[index], wire.FromCard))], cards);

    private static double Length(ImmutableArray<Point> route) => route.Zip(route.Skip(1)).Sum(run => Point.Distance(run.First, run.Second));

    private List<Rect> Closed(object key) => _closed.TryGetValue(key, out var channels) ? channels : _closed[key] = [];

    private ImmutableArray<Point>[] Routes(IReadOnlyList<Rect> cards, IReadOnlyList<Wire> wires) =>
        [.. wires.Select(wire =>
        {
            if (_kept.TryGetValue(wire.Key, out var kept) && kept.Source == wire.Output && kept.Target == wire.Input)
            {
                return kept.Route;
            }

            var route = WireRouting.Route(wire.Output, wire.Input, Owned(cards, wire), _closed.TryGetValue(wire.Key, out var closed) ? closed : null);
            _kept[wire.Key] = new Kept(wire.Output, wire.Input, route, BoundsOf(wire.Output, wire.Input, route));
            return route;
        })];

    // Forgets the routes that a card's change can reach. A wire whose wire record leaves the canvas leaves the cache too.
    private void Forget(IReadOnlyList<Rect> cards, IReadOnlyList<Wire> wires)
    {
        var known = wires.Select(wire => wire.Key).ToHashSet();
        foreach (var gone in _kept.Keys.Where(key => !known.Contains(key)).ToList())
        {
            _kept.Remove(gone);
        }

        foreach (var gone in _closed.Keys.Where(key => !known.Contains(key)).ToList())
        {
            _closed.Remove(gone);
        }

        if (cards.Count != _cards.Length)
        {
            _kept.Clear();
            _closed.Clear();
        }
        else
        {
            var changed = cards.SelectMany((card, index) => card == _cards[index] ? [] : new[] { card, _cards[index] }).Select(card => card.Inflate(KeptClearance)).ToList();
            foreach (var entry in _kept.Where(entry => changed.Any(entry.Value.Bounds.Intersects)).ToList())
            {
                _kept.Remove(entry.Key);
                _closed.Remove(entry.Key);
            }
        }

        _cards = [.. cards];
    }

    private static Rect BoundsOf(Point source, Point target, ImmutableArray<Point> route)
    {
        Point[] points = [source, target, .. route];
        var (left, right) = (points.Min(point => point.X), points.Max(point => point.X));
        var (top, bottom) = (points.Min(point => point.Y), points.Max(point => point.Y));
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect[] Owned(IReadOnlyList<Rect> cards, Wire wire)
    {
        Rect[] own = [.. cards];
        own[wire.FromCard] = wire.FromRect;
        own[wire.ToCard] = wire.ToRect;
        return own;
    }
}
