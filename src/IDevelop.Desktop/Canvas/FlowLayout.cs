using System.Collections.Immutable;
using Avalonia;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>Flow's two card forms.</summary>
public enum FlowDensity { Card, Chip }

/// <summary>How wide Flow's cards are and how many stand in a row, at a Map width.</summary>
public readonly record struct FlowGrid(double CardWidth, int Columns, bool Wraps);

/// <summary>One stage as Flow lays it out: how many agents it holds and whether its band folds.</summary>
public readonly record struct FlowStage(int Agents, bool IsFolded);

/// <summary>Where one band's header and its cards stand. A folded band has no cards.</summary>
public readonly record struct FlowBandPlace(Rect Header, ImmutableArray<Rect> Cards);

/// <summary>Flow's places: every band and card, in the surface's coordinates, and the surface's height.</summary>
public sealed record FlowPlan(FlowGrid Grid, FlowDensity Density, double Height, ImmutableArray<FlowBandPlace> Bands);

/// <summary>A move of the selection among Flow's cards.</summary>
public enum FlowMove { Up, Down, Left, Right }

/// <summary>
/// How a workflow's nodes read by depth: the Index's stages now, and Flow's rows with their widths later. A pure function, so saved
/// positions never change.
/// </summary>
public static class FlowLayout
{
    /// <summary>The space at each side of the surface, and over and under its bands.</summary>
    public const double SideMargin = 12;

    /// <summary>The gap between cards of a row.</summary>
    public const double ColumnGap = 10;

    /// <summary>The gap between rows of a stage.</summary>
    public const double RowGap = 8;

    /// <summary>The gap between two bands.</summary>
    public const double BandGap = 8;

    /// <summary>A band header's height.</summary>
    public const double HeaderHeight = 26;

    /// <summary>A Card's height.</summary>
    public const double CardHeight = 68;

    /// <summary>A Chip's height.</summary>
    public const double ChipHeight = 44;

    /// <summary>A card's largest width.</summary>
    public const double MaxCardWidth = 220;

    /// <summary>The least card width of a row that does not wrap.</summary>
    private const double LeastUnwrapped = 132;

    /// <summary>A wrapped row's column pitch: a card and its gap.</summary>
    private const double WrappedPitch = 142;

    /// <summary>The channel under a header where a row's wires cross. Only a layout that draws every wire keeps it.</summary>
    private const double WireChannel = 14;

    /// <summary>The space under a header in a layout that wraps, where wires draw only for a hovered or selected node.</summary>
    private const double WrappedHeaderGap = 4;

    /// <summary>The largest workflow that opens in Card density.</summary>
    public const int CardDensityLimit = 24;

    /// <summary>
    /// The card width and the columns at <paramref name="mapWidth"/> for stages of <paramref name="stageSizes"/>. Every stage fits in one
    /// row at 132 px a card or more, else the stages wrap at a 142 px pitch. A card's width is a whole number of pixels.
    /// </summary>
    public static FlowGrid Measure(double mapWidth, IReadOnlyList<int> stageSizes)
    {
        var available = Math.Max(mapWidth - 2 * SideMargin, 0);
        var largest = Math.Max(1, stageSizes.DefaultIfEmpty(1).Max());
        var whole = (available - ColumnGap * (largest - 1)) / largest;
        if (whole >= LeastUnwrapped)
        {
            return new(Math.Floor(Math.Min(whole, MaxCardWidth)), largest, false);
        }

        var columns = Math.Max(1, (int)Math.Floor((available + ColumnGap) / WrappedPitch));
        return new(Math.Floor(Math.Min((available - ColumnGap * (columns - 1)) / columns, MaxCardWidth)), columns, columns < largest);
    }

    /// <summary>The density a workflow of <paramref name="agents"/> agents opens in.</summary>
    public static FlowDensity DefaultDensity(int agents) => agents <= CardDensityLimit ? FlowDensity.Card : FlowDensity.Chip;

    /// <summary>A card's height at <paramref name="density"/>.</summary>
    public static double Height(FlowDensity density) => density == FlowDensity.Card ? CardHeight : ChipHeight;

    /// <summary>
    /// Places every band and card at <paramref name="mapWidth"/>. A stacked layout, so nothing can overlap. Rows center when no stage
    /// wraps, and they start at the left edge when any stage wraps.
    /// </summary>
    public static FlowPlan Place(double mapWidth, FlowDensity density, IReadOnlyList<FlowStage> stages)
    {
        var grid = Measure(mapWidth, [.. stages.Select(stage => stage.Agents)]);
        var width = Math.Max(mapWidth - 2 * SideMargin, 0);
        var height = Height(density);
        var bands = ImmutableArray.CreateBuilder<FlowBandPlace>(stages.Count);
        var y = SideMargin;
        foreach (var stage in stages)
        {
            var header = new Rect(SideMargin, y, width, HeaderHeight);
            y += HeaderHeight;
            if (stage.IsFolded || stage.Agents == 0)
            {
                bands.Add(new(header, []));
            }
            else
            {
                y += grid.Wraps ? WrappedHeaderGap : WireChannel;
                var cards = ImmutableArray.CreateBuilder<Rect>(stage.Agents);
                for (var at = 0; at < stage.Agents; at += grid.Columns)
                {
                    var count = Math.Min(grid.Columns, stage.Agents - at);
                    var row = count * grid.CardWidth + (count - 1) * ColumnGap;
                    var left = grid.Wraps ? SideMargin : SideMargin + Math.Floor((width - row) / 2);
                    for (var column = 0; column < count; column++)
                    {
                        cards.Add(new Rect(left + column * (grid.CardWidth + ColumnGap), y, grid.CardWidth, height));
                    }

                    y += height + (at + count < stage.Agents ? RowGap : 0);
                }

                bands.Add(new(header, cards.ToImmutable()));
            }

            y += BandGap;
        }

        return new(grid, density, y - BandGap + SideMargin, bands.ToImmutable());
    }

    /// <summary>
    /// The points of one wire, from an output socket at the bottom of a card or band to an input socket at the top of one. Reuses
    /// <see cref="WireRouting.Route"/> with x and y swapped, then swaps the result back, so every wire of an output leaves on one trunk
    /// and fans out in the channel over its target's row. <paramref name="lane"/> shifts that channel, so wires of different outputs
    /// keep apart. A wire past another row takes short stubs, so the detour starts close to its port.
    /// </summary>
    public static ImmutableArray<Point> WireRoute(Point source, Point target, IReadOnlyList<Rect> cards, double lane = 0)
    {
        // An output straight above its input, with nothing in between, is one straight line.
        if (Math.Abs(source.X - target.X) < 0.01 && !cards.Any(card => Crosses(card, source, target)))
        {
            return [source, target];
        }

        var between = cards.Any(card => card.Top >= source.Y && card.Bottom <= target.Y);
        var run = target.Y - 6 - lane;
        var (sourceStub, targetStub) = !between && run - source.Y >= 4 ? (run - source.Y, target.Y - run) : (8.0, 8.0);
        var route = WireRouting.Route(Swap(source), Swap(target), [.. cards.Select(Swap)], 0, null, sourceStub, targetStub, new Thickness(2));
        return [source, .. route.Select(Swap), target];
    }

    private static bool Crosses(Rect card, Point source, Point target) =>
        card.Left < source.X && card.Right > source.X && card.Top < Math.Max(source.Y, target.Y) && card.Bottom > Math.Min(source.Y, target.Y);

    private static Point Swap(Point point) => new(point.Y, point.X);

    private static Rect Swap(Rect rect) => new(rect.Y, rect.X, rect.Height, rect.Width);

    /// <summary>
    /// The place a key moves to from (<paramref name="stage"/>, <paramref name="index"/>), or null. Up and Down go to the nearest card of
    /// the open stage above or below, Left and Right to the card before or after it in its stage.
    /// </summary>
    public static (int Stage, int Index)? Neighbor(FlowPlan plan, int stage, int index, FlowMove move)
    {
        var from = plan.Bands[stage].Cards[index];
        if (move is FlowMove.Left or FlowMove.Right)
        {
            var next = index + (move == FlowMove.Right ? 1 : -1);
            return next >= 0 && next < plan.Bands[stage].Cards.Length ? (stage, next) : null;
        }

        var step = move == FlowMove.Down ? 1 : -1;
        for (var other = stage + step; other >= 0 && other < plan.Bands.Length; other += step)
        {
            if (plan.Bands[other].Cards.Length > 0)
            {
                var near = plan.Bands[other].Cards.Select((card, at) => (Gap: Math.Abs(card.Center.X - from.Center.X), At: at)).MinBy(pair => pair.Gap);
                return (other, near.At);
            }
        }

        return null;
    }

    /// <summary>The density that shows the most stages from <paramref name="firstStage"/> in a <paramref name="viewHeight"/> high Map.</summary>
    public static FlowDensity DensityToFit(double mapWidth, double viewHeight, IReadOnlyList<FlowStage> stages, int firstStage)
    {
        int Shown(FlowDensity density)
        {
            var plan = Place(mapWidth, density, stages);
            var top = plan.Bands.Length > firstStage ? plan.Bands[firstStage].Header.Top : 0;
            return plan.Bands.Skip(firstStage).Count(band => (band.Cards.IsEmpty ? band.Header.Bottom : band.Cards[^1].Bottom) - top <= viewHeight);
        }

        return Shown(FlowDensity.Chip) > Shown(FlowDensity.Card) ? FlowDensity.Chip : FlowDensity.Card;
    }

    /// <summary>
    /// The stages in depth order. A node's depth is the longest path from a node with no predecessor, along the dependency connections,
    /// with a back edge of a depth-first walk ignored. Inside a stage the nodes stand by saved Y, then saved X.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TaskId>> Stages(Workflow workflow)
    {
        var dependencies = workflow.Connections.Where(connection => connection.Value.Blocks()).Select(connection => connection.Key).ToList();
        var next = dependencies.ToLookup(key => key.From, key => key.To);
        var hasPredecessor = dependencies.Select(key => key.To).ToHashSet();
        Dictionary<TaskId, bool> walked = [];
        HashSet<(TaskId From, TaskId To)> kept = [];

        void Walk(TaskId id)
        {
            walked[id] = false;
            foreach (var to in next[id].Where(to => !walked.TryGetValue(to, out var done) || done))
            {
                kept.Add((id, to));
                if (!walked.ContainsKey(to))
                {
                    Walk(to);
                }
            }

            walked[id] = true;
        }

        var ordered = workflow.Tasks.Keys.OrderBy(id => workflow.Positions[id].Y).ThenBy(id => workflow.Positions[id].X).ThenBy(id => id).ToList();
        foreach (var id in ordered.Where(id => !hasPredecessor.Contains(id)).Concat(ordered).Where(id => !walked.ContainsKey(id)))
        {
            if (!walked.ContainsKey(id))
            {
                Walk(id);
            }
        }

        var before = kept.ToLookup(edge => edge.To, edge => edge.From);
        Dictionary<TaskId, int> depths = [];
        int Depth(TaskId id)
        {
            if (!depths.TryGetValue(id, out var depth))
            {
                depths[id] = depth = before[id].Select(Depth).DefaultIfEmpty(-1).Max() + 1;
            }

            return depth;
        }

        return [.. ordered.GroupBy(Depth).OrderBy(stage => stage.Key).Select(stage => (IReadOnlyList<TaskId>)[.. stage])];
    }
}
