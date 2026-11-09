using Avalonia;
using Avalonia.Threading;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Any card can stand in a wire's way, so the wires find their paths again once cards move, arrive, or leave. <see cref="WireRouter"/>
/// plans all wires at once, because their lanes depend on one another, and keeps the routes that a change cannot reach. A card that
/// moves changes its ports one by one during layout, so those changes ask for one plan after the layout.
/// </summary>
public sealed partial class WorkflowCanvasViewModel
{
    private readonly WireRouter _wireRouter = new();
    private bool _rerouteRequested;

    /// <summary>Asks for one plan after the current layout pass.</summary>
    internal void RequestReroute()
    {
        if (!_rerouteRequested)
        {
            _rerouteRequested = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_rerouteRequested)
                {
                    Reroute();
                }
            }, DispatcherPriority.Default);
        }
    }

    private void Reroute()
    {
        _rerouteRequested = false;
        var cardIndex = Nodes.Select((node, index) => (node, index)).ToDictionary(pair => pair.node, pair => pair.index);
        var wires = Connections.Select(wire => new WireRouter.Wire(
            wire.Key,
            cardIndex[wire.From],
            cardIndex[wire.To],
            wire.From.Output.Anchor,
            wire.To.Input.Anchor,
            // The wire's own two cards stand where their ports are, which Nodify keeps current while a card is dragged.
            new Rect(wire.From.Output.Anchor - new Vector(wire.From.CardSize.Width - InputPortCenter.X, InputPortCenter.Y), wire.From.DrawnSize),
            new Rect(wire.To.Input.Anchor - (Vector)InputPortCenter, wire.To.DrawnSize),
            wire.Flow == WireFlow.Feeding)).ToList();
        // Every card stands where it shows and at the size it draws, so a wire goes around an expanded card (ADR 0024).
        var drawn = _wireRouter.Plan([.. Nodes.Select(node => node.DrawnRect)], wires);
        for (var wire = 0; wire < drawn.Count; wire++)
        {
            Connections[wire].Show(drawn[wire]);
        }

        ShowPills();
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
}
