namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Where each wire of a busy port meets its card's edge (ADR 0061). Wires that share a port each take their own point on the
/// port's side, in the order of the rows they come from or go to, so no two wires meet in one point and none crosses another
/// at the card. One wire always keeps the port's own point, so no wire's socket stands beside the port's dot: the wire whose other end
/// lies nearest the port's row where the edge has nearly the full distance, so a wire between two cards on one row stays straight, and otherwise the wire
/// that leaves the widest distance between the points. The points keep to the card's edge (ADR 0075).
/// </summary>
internal static class PortSlots
{
    /// <summary>The distance between two wires' points at the Full tier, in canvas units.</summary>
    internal const double Pitch = 12;

    // The share of the full distance that the wire nearest the port's row still accepts to keep the port's own point.
    private const double StraightKeeps = 0.75;

    // How far the first and the last point keep from the card's top and bottom.
    private const double Inset = 10;

    /// <summary>
    /// How far each wire's point lies under the port at <paramref name="port"/>, in the order given. <paramref name="farRows"/>
    /// holds the row of each wire's other end. <paramref name="top"/> and <paramref name="bottom"/> are the card's drawn edges.
    /// </summary>
    /// <param name="scale">How much thicker wires draw at the zoom tier, which spreads the points as much, so they stay apart on screen.</param>
    public static double[] Of(IReadOnlyList<double> farRows, double port, double top, double bottom, double scale = 1)
    {
        var slots = new double[farRows.Count];
        if (farRows.Count < 2)
        {
            return slots;
        }

        var (above, below) = (Math.Max(0, port - top - Inset), Math.Max(0, bottom - Inset - port));
        var wanted = Pitch * scale;
        var last = farRows.Count - 1;
        var order = Enumerable.Range(0, farRows.Count).OrderBy(wire => farRows[wire]).ThenBy(wire => wire).ToList();
        var straight = order.IndexOf(order.MinBy(wire => Math.Abs(farRows[wire] - port)));
        // One rank always stays on the port, so no wire's socket stands beside the port's own dot. The rank is the straight wire's
        // where the edge leaves it nearly the full distance, and otherwise the rank that leaves the widest distance.
        var kept = Room(straight) >= wanted * StraightKeeps ? straight
            : Enumerable.Range(0, farRows.Count).OrderByDescending(rank => Math.Min(wanted, Room(rank))).ThenBy(rank => Math.Abs(rank - straight)).First();
        var pitch = Math.Min(wanted, Room(kept));
        for (var rank = 0; rank <= last; rank++)
        {
            slots[order[rank]] = (rank - kept) * pitch;
        }

        return slots;

        double Room(int rank) => Math.Min(rank == 0 ? double.PositiveInfinity : above / rank, rank == last ? double.PositiveInfinity : below / (last - rank));
    }
}
