namespace IDevelop.Desktop.Canvas;

/// <summary>A move of the selection along the graph.</summary>
public enum CardStep { Successor, Predecessor, PreviousSibling, NextSibling }

/// <summary>The canvas's keyboard moves: Tab reads the cards in the outline's order, and the arrow keys follow the connections.</summary>
public sealed partial class WorkflowCanvasViewModel
{
    /// <summary>Selects the card after or before the selected one in the outline, and shows it. At an end the key goes on to the next control.</summary>
    internal bool SelectInOutline(int step)
    {
        var at = SelectedNode is { } selected ? Outline.IndexOf(selected) : step > 0 ? -1 : Outline.Count;
        var next = at + step;
        if (next < 0 || next >= Outline.Count)
        {
            return false;
        }

        Reveal(Outline[next]);
        return true;
    }

    /// <summary>Selects the card one connection away, or the next card that shares a source, and shows it.</summary>
    internal bool SelectConnected(CardStep step)
    {
        if (SelectedNode is not { } node || Neighbor(node, step) is not { } next)
        {
            return false;
        }

        Reveal(next);
        return true;
    }

    /// <summary>The card a step reaches from <paramref name="node"/>: the highest of its successors or predecessors, or its sibling above or below.</summary>
    internal TaskNodeViewModel? Neighbor(TaskNodeViewModel node, CardStep step)
    {
        switch (step)
        {
            case CardStep.Successor:
                return ByPlace(Connections.Where(wire => wire.From == node).Select(wire => wire.To)).FirstOrDefault();
            case CardStep.Predecessor:
                return ByPlace(Connections.Where(wire => wire.To == node).Select(wire => wire.From)).FirstOrDefault();
            default:
                var siblings = ByPlace(Siblings(node)).ToList();
                var at = siblings.IndexOf(node) + (step == CardStep.NextSibling ? 1 : -1);
                return at >= 0 && at < siblings.Count ? siblings[at] : null;
        }
    }

    // The cards with a source in common, or every card without a source. The card itself is one of them.
    private IEnumerable<TaskNodeViewModel> Siblings(TaskNodeViewModel node)
    {
        var sources = Connections.Where(wire => wire.To == node).Select(wire => wire.From).ToHashSet();
        return sources.Count > 0
            ? Connections.Where(wire => sources.Contains(wire.From)).Select(wire => wire.To).Distinct()
            : Nodes.Where(other => Connections.All(wire => wire.To != other));
    }

    private static IEnumerable<TaskNodeViewModel> ByPlace(IEnumerable<TaskNodeViewModel> nodes) =>
        nodes.OrderBy(node => node.Location.Y).ThenBy(node => node.Location.X);

    private void Reveal(TaskNodeViewModel node)
    {
        Select(node);
        View?.Reveal(node);
    }
}
