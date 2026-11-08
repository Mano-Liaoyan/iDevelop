using System.Collections.Immutable;
using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Plans all the wires of a canvas together (ADR 0075). A plan picks where each wire meets its cards, finds each wire's route past the
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
    private double _reach;

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

    /// <summary>The result for one wire: its points on the card edges, its route, and its curve.</summary>
    public readonly record struct Drawn(double Exit, double Entry, ImmutableArray<Point> Route, ImmutableArray<WirePiece> Curve);

    private readonly record struct Kept(Point Source, Point Target, ImmutableArray<Point> Route, Rect Bounds);

    public IReadOnlyList<Drawn> Plan(IReadOnlyList<Rect> cards, IReadOnlyList<Wire> wires, double reach, double scale)
    {
        Forget(cards, wires, reach);
        var heads = wires.Select(wire => (Out: wire.Input.Y, In: wire.Output.Y)).ToArray();
        var ends = Slots(cards, wires, heads, scale);
        var routes = Routes(cards, wires, ends, reach);
        // A wire leaves its port toward where its route goes, which a detour can make another row than its far end's.
        heads = [.. wires.Select((wire, index) => Headings(routes[index], wire))];
        var ordered = Slots(cards, wires, heads, scale);
        if (!ordered.SequenceEqual(ends))
        {
            ends = ordered;
            routes = Routes(cards, wires, ends, reach);
        }

        var stay = new bool[wires.Count];
        var spread = Spread(wires, routes, stay, cards, scale);
        for (var round = 0; round < MostRounds && spread.Overflows.Count > 0; round++)
        {
            foreach (var overflow in spread.Overflows)
            {
                var index = overflow.Wire;
                var wire = wires[index];
                var closed = Closed(wire.Key);
                closed.Add(overflow.Channel);
                var source = wire.Output + new Vector(0, ends[index].Exit);
                var target = wire.Input + new Vector(0, ends[index].Entry);
                var route = WireRouting.Route(source, target, Owned(cards, wire), reach, closed);
                // A wire that the next free channel would take far around stays and shares the full channel's room with its neighbours.
                if (Length(route) > Length(routes[index]) + MostDetour)
                {
                    closed.RemoveAt(closed.Count - 1);
                    stay[index] = true;
                }
                else
                {
                    routes[index] = route;
                    _kept[wire.Key] = new Kept(source, target, route, BoundsOf(source, target, route));
                }
            }

            spread = Spread(wires, routes, stay, cards, scale);
        }

        return [.. wires.Select((wire, index) =>
        {
            var own = Owned(cards, wire);
            var curve = WireCurve.Of(wire.Output + new Vector(0, ends[index].Exit), spread.Routes[index], wire.Input + new Vector(0, ends[index].Entry), own, (wire.FromCard, wire.ToCard));
            return new Drawn(ends[index].Exit, ends[index].Entry, spread.Routes[index], curve);
        })];
    }

    private static WireLanes.Spreading Spread(IReadOnlyList<Wire> wires, ImmutableArray<Point>[] routes, bool[] stay, IReadOnlyList<Rect> cards, double scale) =>
        WireLanes.Spread([.. wires.Select((wire, index) => new WireLanes.Wire(routes[index], wire.FromRect.Top + wire.FromRect.Left / 100000, wire.ToRect.Top + wire.ToRect.Left / 100000, wire.Active || stay[index]))], cards, scale);

    private static double Length(ImmutableArray<Point> route) => route.Zip(route.Skip(1)).Sum(run => Point.Distance(run.First, run.Second));

    private List<Rect> Closed(object key) => _closed.TryGetValue(key, out var channels) ? channels : _closed[key] = [];

    private ImmutableArray<Point>[] Routes(IReadOnlyList<Rect> cards, IReadOnlyList<Wire> wires, (double Exit, double Entry)[] ends, double reach) =>
        [.. wires.Select((wire, index) =>
        {
            var (source, target) = (wire.Output + new Vector(0, ends[index].Exit), wire.Input + new Vector(0, ends[index].Entry));
            if (_kept.TryGetValue(wire.Key, out var kept) && kept.Source == source && kept.Target == target)
            {
                return kept.Route;
            }

            var route = WireRouting.Route(source, target, Owned(cards, wire), reach, _closed.TryGetValue(wire.Key, out var closed) ? closed : null);
            _kept[wire.Key] = new Kept(source, target, route, BoundsOf(source, target, route));
            return route;
        })];

    // Forgets the routes that a card's change can reach. A wire whose wire record leaves the canvas leaves the cache too.
    private void Forget(IReadOnlyList<Rect> cards, IReadOnlyList<Wire> wires, double reach)
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

        // A zoom tier or a new card size changes every card's room.
        if (cards.Count != _cards.Length || reach != _reach)
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
        _reach = reach;
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

    // The row a wire heads for from each of its ports: the row of its first turn, or the far port when it runs straight.
    private static (double Out, double In) Headings(ImmutableArray<Point> route, Wire wire)
    {
        var turns = route.Length >= 3;
        return (turns ? route[1].Y == route[0].Y ? route[2].Y : route[1].Y : wire.Input.Y,
                turns ? route[^2].Y == route[^1].Y ? route[^3].Y : route[^2].Y : wire.Output.Y);
    }

    // Each wire's points on its cards' edges, from the rows it heads for.
    private static (double Exit, double Entry)[] Slots(IReadOnlyList<Rect> cards, IReadOnlyList<Wire> wires, (double Out, double In)[] heads, double scale)
    {
        var ends = new (double Exit, double Entry)[wires.Count];
        foreach (var group in Enumerable.Range(0, wires.Count).GroupBy(index => wires[index].FromCard))
        {
            var members = group.ToList();
            var slots = Port(cards, wires[members[0]].Output.Y, wires[members[0]].FromRect.Height, [.. members.Select(index => heads[index].Out)], scale);
            for (var i = 0; i < members.Count; i++)
            {
                ends[members[i]].Exit = slots[i];
            }
        }

        foreach (var group in Enumerable.Range(0, wires.Count).GroupBy(index => wires[index].ToCard))
        {
            var members = group.ToList();
            var slots = Port(cards, wires[members[0]].Input.Y, wires[members[0]].ToRect.Height, [.. members.Select(index => heads[index].In)], scale);
            for (var i = 0; i < members.Count; i++)
            {
                ends[members[i]].Entry = slots[i];
            }
        }

        return ends;
    }

    private static double[] Port(IReadOnlyList<Rect> cards, double row, double height, double[] heads, double scale)
    {
        var top = row - WorkflowCanvasViewModel.InputPortCenter.Y;
        return PortSlots.Of(heads, row, top, top + height, scale);
    }
}
