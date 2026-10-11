using Avalonia;

namespace IDevelop.Desktop.Canvas;

/// <summary>A card as the layout sees it: its saved position and the size its container shows now, ports included.</summary>
public readonly record struct CardBox(Point Location, Size Size);

/// <summary>
/// Where expanded cards leave room on screen (#94, ADR 0024). A column is the cards whose left edges lie within half a
/// collapsed card of the column's first card, and a column that holds a wider card moves every later column right by the
/// extra width. A card that grows pushes down every card below it that lies under it once the columns moved, in its column
/// or not, so each keeps its saved gap to the card above. The offsets are for the view only: the workflow keeps its
/// positions, and every offset is zero again once no card is expanded. Pure, so each rule has its own test.
/// </summary>
public static class CardLayout
{
    /// <summary>The offset of each card, in the order given.</summary>
    public static Vector[] Offsets(IReadOnlyList<CardBox> cards)
    {
        var shifts = new double[cards.Count];
        var shift = 0.0;
        foreach (var column in Columns(cards))
        {
            foreach (var index in column)
            {
                shifts[index] = shift;
            }

            shift += Math.Max(0, column.Max(index => cards[index].Size.Width) - WorkflowCanvasViewModel.TaskCardWidth);
        }

        // Top to bottom, each card goes below every card above it that it lies under where they show, by the gap it had to
        // that card's collapsed bottom, so a card moves by exactly what the cards above it grew.
        var tops = new double[cards.Count];
        var placed = new List<int>();
        foreach (var index in Enumerable.Range(0, cards.Count).OrderBy(index => cards[index].Location.Y))
        {
            var card = cards[index];
            var y = card.Location.Y;
            foreach (var other in placed)
            {
                var gap = card.Location.Y - cards[other].Location.Y - WorkflowCanvasViewModel.TaskCardHeight;
                if (gap >= 0 && Beneath(card, shifts[index], cards[other], shifts[other]))
                {
                    y = Math.Max(y, tops[other] + cards[other].Size.Height + gap);
                }
            }

            tops[index] = y;
            placed.Add(index);
        }

        return [.. Enumerable.Range(0, cards.Count).Select(index => new Vector(shifts[index], tops[index] - cards[index].Location.Y))];
    }

    /// <summary>
    /// The card shares some width with the other card where both show, their 10 px port gutters left out, so a card that
    /// grows down would cover it.
    /// </summary>
    private static bool Beneath(CardBox card, double shift, CardBox other, double otherShift)
    {
        const double Gutter = 10;
        var left = card.Location.X + shift + Gutter;
        var otherLeft = other.Location.X + otherShift + Gutter;
        return left < otherLeft + other.Size.Width - 2 * Gutter && otherLeft < left + card.Size.Width - 2 * Gutter;
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
