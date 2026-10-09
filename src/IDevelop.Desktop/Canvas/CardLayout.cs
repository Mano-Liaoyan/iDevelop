using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>A card as the layout sees it: its saved position and the size its container shows now, ports included.</summary>
public readonly record struct CardBox(Point Location, Size Size);

/// <summary>
/// Where expanded cards leave room on screen (#94, ADR 0024). A column is the cards whose left edges lie within half a
/// collapsed card of the column's first card. A card that grows pushes the cards below it in its column down, so each keeps
/// its saved gap to the card above, and a column that holds a wider card moves every later column right by the extra width.
/// The offsets are for the view only: the workflow keeps its positions, and every offset is zero again once no card is
/// expanded. Pure, so each rule has its own test.
/// </summary>
public static class CardLayout
{
    /// <summary>The offset of each card, in the order given.</summary>
    public static Vector[] Offsets(IReadOnlyList<CardBox> cards)
    {
        var offsets = new Vector[cards.Count];
        var shift = 0.0;
        foreach (var column in Columns(cards))
        {
            double? bottom = null;
            CardBox? above = null;
            foreach (var index in column.OrderBy(index => cards[index].Location.Y))
            {
                var card = cards[index];
                var y = card.Location.Y;
                if (bottom is { } floor && above is { } previous)
                {
                    // The gap the two cards have while collapsed, so a card moves by exactly what the cards above it grew.
                    var gap = card.Location.Y - previous.Location.Y - WorkflowCanvasViewModel.TaskCardHeight;
                    y = Math.Max(y, floor + gap);
                }

                offsets[index] = new Vector(shift, y - card.Location.Y);
                bottom = y + card.Size.Height;
                above = card;
            }

            shift += Math.Max(0, column.Max(index => cards[index].Size.Width) - WorkflowCanvasViewModel.TaskCardWidth);
        }

        return offsets;
    }

    /// <summary>The cards' indexes grouped into columns, left to right.</summary>
    private static List<List<int>> Columns(IReadOnlyList<CardBox> cards)
    {
        var columns = new List<List<int>>();
        foreach (var index in Enumerable.Range(0, cards.Count).OrderBy(index => cards[index].Location.X).ThenBy(index => cards[index].Location.Y))
        {
            if (columns.Count > 0 && cards[index].Location.X - cards[columns[^1][0]].Location.X < WorkflowCanvasViewModel.TaskCardWidth / 2)
            {
                columns[^1].Add(index);
            }
            else
            {
                columns.Add([index]);
            }
        }

        return columns;
    }
}
