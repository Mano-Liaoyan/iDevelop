using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

/// <summary>A title must tell two nodes apart, also when it does not fit whole.</summary>
public sealed class TitlePartsTests
{
    [Fact]
    public void Titles_with_a_shared_start_keep_the_word_that_differs()
    {
        var keeps = TitleParts.KeepFrom(["Backfill ledger partition 2025-Q1", "Backfill ledger partition 2025-Q2", "Plan the rollout"]);

        Assert.Equal(["Backfill ledger partition ".Length, "Backfill ledger partition ".Length, 0], keeps);
    }

    [Fact]
    public void A_title_that_another_one_repeats_or_merely_continues_keeps_nothing()
    {
        Assert.Equal([0, 0, "Review ".Length], TitleParts.KeepFrom(["Review", "Review", "Review the plan"]));
    }

    [AvaloniaFact]
    public void A_cut_title_keeps_the_part_that_differs_and_as_much_of_its_start_as_fits()
    {
        var text = new EllipsisText { Whole = "Backfill ledger partition 2025-Q1", KeepFrom = "Backfill ledger partition ".Length, FontSize = 13, MaxLines = 1, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(120, double.PositiveInfinity));

        Assert.EndsWith("… 2025-Q1", text.Text);
        Assert.True(text.Text!.Length > "… 2025-Q1".Length);
    }

    [AvaloniaFact]
    public void A_plain_cut_stays_when_it_leaves_the_word_that_differs_in_view()
    {
        var text = new EllipsisText { Whole = "Add the usage_events schema now", KeepFrom = "Add ".Length, FontSize = 13, MaxLines = 1, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(120, double.PositiveInfinity));

        Assert.StartsWith("Add the", text.Text);
        Assert.EndsWith("…", text.Text);
    }

    [AvaloniaFact]
    public void A_title_that_fits_ignores_the_part_it_would_keep()
    {
        var text = new EllipsisText { Whole = "Review", KeepFrom = 3, FontSize = 13, MaxLines = 2, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(400, double.PositiveInfinity));

        Assert.Equal("Review", text.Text);
    }
}
