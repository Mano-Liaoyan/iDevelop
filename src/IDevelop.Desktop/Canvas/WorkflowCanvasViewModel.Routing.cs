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
        foreach (var connection in Connections)
        {
            connection.Reroute();
        }
    }
}
