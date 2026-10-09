using Avalonia;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Tests;

/// <summary>Where expanded cards leave room on screen (ADR 0024), a pure rule over each card's saved position and shown size.</summary>
public sealed class CardLayoutTests
{
    private static CardBox Card(double x, double y, double height = WorkflowCanvasViewModel.TaskCardHeight, bool wide = false) =>
        new(new Point(x, y), new Size(wide ? WorkflowCanvasViewModel.ExpandedCardWidth : WorkflowCanvasViewModel.TaskCardWidth, height));

    [Fact]
    public void Without_an_expanded_card_no_card_moves_even_where_saved_cards_overlap()
    {
        Vector[] offsets = CardLayout.Offsets([Card(60, 90), Card(60, 120), Card(360, 90), Card(380, 100), Card(660, 400)]);

        Assert.All(offsets, offset => Assert.Equal(default, offset));
    }

    [Fact]
    public void An_expanded_card_pushes_the_cards_below_it_in_its_column_down_by_what_it_grew_keeping_their_gaps()
    {
        Vector[] offsets = CardLayout.Offsets([Card(360, 90), Card(360, 210, 300, wide: true), Card(360, 330), Card(360, 500)]);

        Assert.Equal(
            [new Vector(0, 0), new Vector(0, 0), new Vector(0, 300 - 64), new Vector(0, 300 - 64)],
            offsets);
    }

    [Fact]
    public void A_column_with_a_wider_card_moves_every_later_column_right_by_the_extra_width_and_no_earlier_one()
    {
        Vector[] offsets = CardLayout.Offsets([Card(60, 210), Card(360, 210, 300, wide: true), Card(660, 150), Card(960, 150), Card(400, 600)]);

        // The card at 400 joins the wider card's column, so it moves down, not right.
        var extra = WorkflowCanvasViewModel.ExpandedCardWidth - WorkflowCanvasViewModel.TaskCardWidth;
        Assert.Equal([new Vector(0, 0), new Vector(0, 0), new Vector(extra, 0), new Vector(extra, 0), new Vector(0, 300 - 64)], offsets);
    }

    [Fact]
    public void Two_expanded_cards_in_one_column_stack_with_their_saved_gaps()
    {
        Vector[] offsets = CardLayout.Offsets([Card(360, 90, 250, wide: true), Card(360, 210, 300, wide: true), Card(360, 330)]);

        Assert.Equal([new Vector(0, 0), new Vector(0, 250 - 64), new Vector(0, 250 - 64 + 300 - 64)], offsets);
    }

    [Fact]
    public void Cards_join_a_column_while_their_left_edges_lie_within_half_a_collapsed_card_of_its_first()
    {
        var half = WorkflowCanvasViewModel.TaskCardWidth / 2;
        Vector[] offsets = CardLayout.Offsets([Card(360, 90, 200, wide: true), Card(360 + half - 1, 400), Card(360 + half, 400)]);

        Assert.Equal(new Vector(0, 200 - 64), offsets[1]);
        Assert.Equal(new Vector(WorkflowCanvasViewModel.ExpandedCardWidth - WorkflowCanvasViewModel.TaskCardWidth, 0), offsets[2]);
    }
}
