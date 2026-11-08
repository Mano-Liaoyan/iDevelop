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
        ShowFarText();
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
        // Every card stands where it shows and at the size it draws, so a wire goes around an expanded card (ADR 0024) and a
        // zoomed out card's last line.
        var drawn = _wireRouter.Plan([.. Nodes.Select(node => node.DrawnRect)], wires, CardReach, ZoomTiers.WireScale(Tier));
        for (var wire = 0; wire < drawn.Count; wire++)
        {
            Connections[wire].Show(drawn[wire]);
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
    private double RowPitch() => ZoomTiers.RowPitch([.. Nodes.Where(node => !node.IsExpanded).Select(node => node.DisplayLocation)]);
}
