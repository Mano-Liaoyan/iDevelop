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

    /// <summary>Tells each card which part of its title sets it apart from the other cards' titles.</summary>
    private void MarkTitles()
    {
        var keeps = TitleParts.KeepFrom([.. Nodes.Select(node => node.Title)]);
        foreach (var (node, keep) in Nodes.Zip(keeps))
        {
            node.TitleKeep = keep;
        }
    }

    // While every wire finds its path, each one need not spread them all.
    private bool _spreadHeld;
}
