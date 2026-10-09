using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Proto;

/// <summary>THROWAWAY N1 PROTOTYPE, option B: a pinned card's note in the margin lane.</summary>
public sealed record MarginNote(Point Location, TaskNodeViewModel Node)
{
    public string Surface => "surface:" + Node.Title;
}

// THROWAWAY N1 PROTOTYPE, option B. Never merge. Notes line up in one lane right of the graph, each level with its card
// where it can be, and stacked below the note above it where it cannot. The graph itself never moves.
internal static class N1Notes
{
    public const double Width = 320;

    // The lane keeps this far from the graph's last card, and notes keep this far from each other.
    private const double LaneGap = 48;
    private const double NoteGap = 16;

    private static readonly Dictionary<TaskNodeViewModel, Size> Sizes = [];

    public static void Measured(TaskNodeViewModel node, Size size)
    {
        if (!Sizes.TryGetValue(node, out var old) || old != size)
        {
            Sizes[node] = size;
            N1Layout.Schedule(node.Canvas);
        }
    }

    public static IEnumerable<Rect> Obstacles(WorkflowCanvasViewModel canvas) =>
        canvas.Notes.Select(note => new Rect(note.Location, Sizes.GetValueOrDefault(note.Node, new Size(Width, 200))));

    public static void Place(WorkflowCanvasViewModel canvas)
    {
        var placed = new List<MarginNote>();

        if (canvas.Nodes.Count == 0)
        {
            canvas.Notes.Clear();
            return;
        }

        // The cards' containers have 10 px gutters, so the lane starts LaneGap from the last card's visible edge.
        var lane = canvas.Nodes.Max(node => node.DisplayRect.Right) - 10 + LaneGap;
        var bottom = double.NegativeInfinity;
        foreach (var node in canvas.Nodes.Where(node => node.IsExpanded).OrderBy(node => node.DisplayLocation.Y).ThenBy(node => node.DisplayLocation.X))
        {
            var y = Math.Max(node.DisplayLocation.Y, bottom + NoteGap);
            placed.Add(new MarginNote(new Point(lane, y), node));
            bottom = y + Sizes.GetValueOrDefault(node, new Size(Width, 200)).Height;
        }

        // A note that keeps its place keeps its control, so it keeps its size and its focus.
        if (!placed.SequenceEqual(canvas.Notes))
        {
            canvas.Notes.Clear();
            foreach (var note in placed)
            {
                canvas.Notes.Add(note);
            }
        }
    }
}
