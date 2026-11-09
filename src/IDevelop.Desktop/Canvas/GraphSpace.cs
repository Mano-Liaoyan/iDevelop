using Avalonia;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// Graph's display compaction (brief S2). A saved layout spaces its cards for the old large card, so the Map shows it at true 1:1
/// cards by scaling each axis. The scale is computed when the workflow opens and is kept until it closes. It converts only where
/// the canvas reads from or writes to the document, so a file keeps its values until the person moves a card.
/// </summary>
public readonly record struct GraphSpace(double Kx, double Ky)
{
    private const double CardWidth = 220;

    private const double CardHeight = 94;

    private const double ColumnGap = 44;

    private const double RowGap = 26;

    /// <summary>A saved layout at its own scale.</summary>
    public static readonly GraphSpace Identity = new(1, 1);

    /// <summary>
    /// The scale of each axis for a saved layout. <see cref="Kx"/> is the largest of (220 + 44) / dx over pairs of cards that
    /// overlap in Y, and <see cref="Ky"/> the largest of (94 + 26) / dy over pairs that overlap in X, each at most 1. A pair that
    /// the two scales would bring to overlap, though it overlaps on neither axis in the saved layout, raises the axis that needs
    /// the less change.
    /// </summary>
    public static GraphSpace Of(IReadOnlyCollection<CanvasPoint> positions)
    {
        var points = positions.ToArray();
        var (kx, ky) = (0.0, 0.0);
        foreach (var (dx, dy) in Pairs(points))
        {
            if (dy < CardHeight && dx > 0)
            {
                kx = Math.Max(kx, (CardWidth + ColumnGap) / dx);
            }

            if (dx < CardWidth && dy > 0)
            {
                ky = Math.Max(ky, (CardHeight + RowGap) / dy);
            }
        }

        (kx, ky) = (kx is 0 ? 1 : Math.Min(kx, 1), ky is 0 ? 1 : Math.Min(ky, 1));
        foreach (var _ in points)
        {
            var moved = false;
            foreach (var (dx, dy) in Pairs(points))
            {
                if (dx * kx >= CardWidth || dy * ky >= CardHeight || (dx < CardWidth && dy < CardHeight))
                {
                    continue;
                }

                var (wantX, wantY) = (dx > 0 ? Math.Min((CardWidth + ColumnGap) / dx, 1) : double.PositiveInfinity, dy > 0 ? Math.Min((CardHeight + RowGap) / dy, 1) : double.PositiveInfinity);
                if (wantX / kx <= wantY / ky)
                {
                    kx = wantX;
                }
                else
                {
                    ky = wantY;
                }

                moved = true;
            }

            if (!moved)
            {
                break;
            }
        }

        return new GraphSpace(kx, ky);
    }

    /// <summary>A saved position as the Map shows it.</summary>
    public Point ToDisplay(CanvasPoint saved) => new(saved.X * Kx, saved.Y * Ky);

    /// <summary>A position on the Map as the document saves it.</summary>
    public CanvasPoint ToSaved(Point display) => new(display.X / Kx, display.Y / Ky);

    private static IEnumerable<(double Dx, double Dy)> Pairs(CanvasPoint[] points)
    {
        for (var first = 0; first < points.Length; first++)
        {
            for (var second = first + 1; second < points.Length; second++)
            {
                yield return (Math.Abs(points[first].X - points[second].X), Math.Abs(points[first].Y - points[second].Y));
            }
        }
    }
}
