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

        Assert.StartsWith("…", text.Text);
        Assert.EndsWith(" 2025-Q1", text.Text);
        Assert.EndsWith(text.Text![1..], "Backfill ledger partition 2025-Q1");
    }

    [AvaloniaFact]
    public void The_backfill_partitions_of_the_large_fixture_read_apart_when_their_titles_are_cut()
    {
        string[] titles = [.. new[] { "2023-Q4", "2024-Q1", "2024-Q2", "2024-Q3", "2024-Q4", "2025-Q1", "2025-Q2", "2025-Q3" }
            .Select(quarter => "Backfill ledger partition " + quarter)];
        var keeps = TitleParts.KeepFrom(titles);

        var shown = titles.Zip(keeps, (title, keep) =>
        {
            var text = new EllipsisText { Whole = title, KeepFrom = keep, FontSize = 13, MaxLines = 2, TextWrapping = Avalonia.Media.TextWrapping.WrapWithOverflow };
            text.Measure(new Avalonia.Size(90, double.PositiveInfinity));
            return text.Text!;
        }).ToArray();

        Assert.All(shown.Zip(titles), pair => Assert.True(pair.First.StartsWith('…') && pair.Second.EndsWith(pair.First[1..]), pair.First));
        Assert.Equal(titles.Length, shown.Distinct().Count());
    }

    [AvaloniaFact]
    public void A_cut_title_starts_with_an_ellipsis_and_keeps_the_whole_part_that_differs()
    {
        const string Title = "Move the account statement query";
        const string Shown = "…the account statement query";
        var text = new EllipsisText { Whole = Title, KeepFrom = "Move the ".Length, FontSize = 13, MaxLines = 1, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(Shown, new Avalonia.Media.Typeface(text.FontFamily), 13, null);

        text.Measure(new Avalonia.Size(Math.Ceiling(layout.Width) + 3, double.PositiveInfinity));

        Assert.Equal(Shown, text.Text);
    }

    [AvaloniaFact]
    public void A_plain_cut_stays_when_the_title_shares_only_its_first_word()
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

    [AvaloniaFact]
    public void A_kept_part_that_leaves_no_room_for_the_start_gives_way_to_a_cut_at_the_end()
    {
        var text = new EllipsisText { Whole = "Reconcile the adjustment postings by tenant", KeepFrom = "Reconcile ".Length, FontSize = 13, MaxLines = 2, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(70, double.PositiveInfinity));

        Assert.StartsWith("Recon", text.Text);
        Assert.Single(text.Text!, letter => letter == '…');
        Assert.EndsWith("…", text.Text);
    }
}
