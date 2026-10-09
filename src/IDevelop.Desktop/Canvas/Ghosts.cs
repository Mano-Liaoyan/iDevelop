using Avalonia;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>
/// What the canvas draws for a change that no edit holds yet: a proposal's tasks and connections, and a dropped wire
/// while the Add popover is open. A ghost takes no input.
/// </summary>
public abstract record Ghost(Point Location);

/// <summary>
/// A dashed card for a task that a proposal adds, or fills in place of the empty card, with a card's three lines: the
/// title, <see cref="Label"/>, and <see cref="Detail"/>, the model and level. <see cref="Note"/> says why the planner's
/// choice of agent falls back, in a tab under the card.
/// </summary>
public sealed record GhostCardViewModel(Point Location, string Label, string Title, string Preview, NodeKind Kind) : Ghost(Location)
{
    public string? Detail { get; init; }

    public string? Note { get; init; }

    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>A connection that a proposal adds, drawn dashed in its source's hue. Its ends are relative to its location.</summary>
public sealed record GhostWireViewModel(Point Location, Point Source, Point Target, NodeKind Kind, ConnectionKind Connection) : Ghost(Location)
{
    public bool IsContext => Connection == ConnectionKind.Context;

    public static GhostWireViewModel Between(Point from, Point to, NodeKind kind, ConnectionKind connection)
    {
        var (location, source, target) = GhostFrame.Of(from, to);
        return new(location, source, target, kind, connection);
    }
}

/// <summary>A wire dropped on empty canvas, which stays from its port to the drop while the Add popover is open.</summary>
public sealed record DanglingWireViewModel(Point Location, Point Source, Point Target, NodeKind Kind) : Ghost(Location)
{
    public static DanglingWireViewModel Between(Point from, Point to, NodeKind kind)
    {
        var (location, source, target) = GhostFrame.Of(from, to);
        return new(location, source, target, kind);
    }
}

internal static class GhostFrame
{
    // A step wire runs past its ends by its spacing and its arrow, so its box reaches beyond them.
    private const double Margin = 40;

    /// <summary>A box at the top left of the two points, and the points relative to it.</summary>
    public static (Point Location, Point Source, Point Target) Of(Point from, Point to)
    {
        var location = new Point(Math.Min(from.X, to.X) - Margin, Math.Min(from.Y, to.Y) - Margin);
        return (location, new Point(from.X - location.X, from.Y - location.Y), new Point(to.X - location.X, to.Y - location.Y));
    }
}
