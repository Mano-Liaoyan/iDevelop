namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Where each wire of a busy port meets its card's edge (ADR 0061). Wires that share a port each take their own point on the
/// port's side, in the order of the rows they come from or go to, so no two wires meet in one point and none crosses another
/// at the card. The wire whose other end lies nearest the port's row keeps the port's own point, so a wire between two cards
/// on one row stays straight, and the points keep to the card's edge.
/// </summary>
internal static class PortSlots
{
    /// <summary>The distance between two wires' points at the Full tier, in canvas units.</summary>
    internal const double Pitch = 12;

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

        var room = Math.Max(0, bottom - top - 2 * Inset);
        var pitch = Math.Min(Pitch * scale, room / (farRows.Count - 1));
        var span = pitch * (farRows.Count - 1);
        var order = Enumerable.Range(0, farRows.Count).OrderBy(wire => farRows[wire]).ThenBy(wire => wire).ToList();
        var straight = order.IndexOf(order.MinBy(wire => Math.Abs(farRows[wire] - port)));
        var first = Math.Clamp(port - straight * pitch, top + Inset, Math.Max(top + Inset, bottom - Inset - span));
        for (var rank = 0; rank < order.Count; rank++)
        {
            slots[order[rank]] = first + rank * pitch - port;
        }

        return slots;
    }
}
