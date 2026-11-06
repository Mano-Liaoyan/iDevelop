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

    /// <summary>Gives each wire its lane beside the wires that share a run with it.</summary>
    internal void SpreadWires()
    {
        if (_spreadHeld)
        {
            return;
        }

        var routes = WireLanes.Spread([.. Connections.Select(connection => connection.Lane)], [.. Nodes.Select(node => node.DisplayRect)]);
        foreach (var (connection, route) in Connections.Zip(routes))
        {
            connection.ShowRoute(route);
        }
    }

    // While every wire finds its path, each one need not spread them all.
    private bool _spreadHeld;
}
