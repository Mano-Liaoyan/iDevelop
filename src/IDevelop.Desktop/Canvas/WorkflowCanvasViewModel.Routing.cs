using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Any card can stand in a wire's way, so every wire finds its path again once cards move, arrive, or leave. Each of those
/// is an edit, an undo, or a redo, and a drop commits all of its moves as one edit, so Sync reroutes every wire once for
/// all the cards rather than once for each card.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private void Reroute()
    {
        ShowFarText();
        _spreadHeld = true;
        foreach (var connection in Connections)
        {
            connection.Reroute();
        }

        _spreadHeld = false;

        SpreadWires();
    }

    /// <summary>Gives each wire its lane beside the wires that share a run with it, and its curve along that lane.</summary>
    internal void SpreadWires()
    {
        if (_spreadHeld)
        {
            return;
        }

        Rect[] cards = [.. Nodes.Select(node => node.DrawnRect)];
        var scale = ZoomTiers.WireScale(Tier);
        var routes = WireLanes.Spread([.. Connections.Select(connection => connection.Lane)], cards, scale);
        for (var wire = 0; wire < routes.Length; wire++)
        {
            Connections[wire].ShowRoute(routes[wire], cards);
        }
    }

    /// <summary>How far <paramref name="wire"/> meets its source card under the output port and its target card under the input port.</summary>
    internal (double Exit, double Entry) Slots(ConnectionViewModel wire)
    {
        var scale = ZoomTiers.WireScale(Tier);
        var outs = Connections.Where(other => other.From == wire.From).Append(wire).Distinct().ToList();
        var ins = Connections.Where(other => other.To == wire.To).Append(wire).Distinct().ToList();
        return (Slot(outs, wire, wire.From.Output.Anchor.Y, wire.From, other => other.To.Input.Anchor.Y),
                Slot(ins, wire, wire.To.Input.Anchor.Y, wire.To, other => other.From.Output.Anchor.Y));

        double Slot(List<ConnectionViewModel> port, ConnectionViewModel own, double row, TaskNodeViewModel card, Func<ConnectionViewModel, double> far)
        {
            var top = row - InputPortCenter.Y;
            return PortSlots.Of([.. port.Select(far)], row, top, top + card.DrawnSize.Height, scale)[port.IndexOf(own)];
        }
    }

    /// <summary>Tells each card which words of its title set it apart from every other card's title.</summary>
    private void MarkTitles()
    {
        var keeps = TitleParts.Keys([.. Nodes.Select(node => node.Title)]);
        foreach (var (node, keep) in Nodes.Zip(keeps))
        {
            node.TitleKey = keep;
        }
    }

    /// <summary>Sets the zoomed out card's text for the zoom and the cards' rows. It returns whether a card's drawn height changed.</summary>
    private bool ShowFarText()
    {
        var text = ZoomTiers.TitleText(ViewportZoom, Tier == ZoomTier.Block ? ZoomTier.Block : ZoomTier.Title, Tier == ZoomTier.Title ? RowPitch() : double.PositiveInfinity);
        var grew = text.Reach != FarText.Reach;
        FarText = text;
        return grew;
    }

    /// <summary>The least distance from a collapsed card's top to the top of a collapsed card under it that it overlaps across.</summary>
    private double RowPitch()
    {
        const double width = TaskCardWidth + 32;
        var pitch = double.PositiveInfinity;
        var cards = Nodes.Where(node => !node.IsExpanded).Select(node => node.DisplayLocation).ToArray();
        for (var first = 0; first < cards.Length; first++)
        {
            for (var second = first + 1; second < cards.Length; second++)
            {
                var (dx, dy) = (Math.Abs(cards[first].X - cards[second].X), Math.Abs(cards[first].Y - cards[second].Y));
                if (dx < width && dy > 0)
                {
                    pitch = Math.Min(pitch, dy);
                }
            }
        }

        return pitch;
    }

    // While every wire finds its path, each one need not spread them all.
    private bool _spreadHeld;
}
