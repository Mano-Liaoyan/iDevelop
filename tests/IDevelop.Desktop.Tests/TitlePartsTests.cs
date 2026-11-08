using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Tests;

/// <summary>A title must tell two nodes apart, also when it does not fit whole.</summary>
public sealed class TitlePartsTests
{
    /// <summary>The 50 node titles of the review fixture's large workflow.</summary>
    public static readonly string[] LargeTitles =
    [
        "Inventory the ledger tables", "Map the reporting consumers", "Measure the current query load", "Record the partition boundaries",
        "Create the partitioned parent table", "Add the monthly partition job", "Create the account and period indexes", "Add the foreign keys as NOT VALID",
        "Validate the foreign keys online", "Write the rollback migration",
        "Backfill ledger partition 2023-Q4", "Backfill ledger partition 2024-Q1", "Backfill ledger partition 2024-Q2", "Backfill ledger partition 2024-Q3",
        "Backfill ledger partition 2024-Q4", "Backfill ledger partition 2025-Q1", "Backfill ledger partition 2025-Q2", "Backfill ledger partition 2025-Q3",
        "Add the dual-write repository", "Mirror the invoice postings", "Mirror the refund postings", "Mirror the adjustment postings",
        "Compare the two tables nightly", "Alert on a mismatch above 0.01%",
        "Migrate the ledger read path onto the partitioned tables without breaking the nightly reconciliation export",
        "Move the account statement query", "Move the tax report query", "Move the revenue dashboard query", "Move the audit export",
        "Route strong reads to the primary", "Add the replica lag guard", "Load test the read path",
        "Freeze ledger writes for the drill", "Switch the writer to the partitioned table", "Verify the row counts", "Verify the balances per tenant",
        "Unfreeze ledger writes", "Run the cutover in staging", "Run the cutover in the EU region", "Run the cutover in the US region",
        "Remove the dual-write code", "Drop the old indexes", "Archive the old table", "Update the on-call runbook", "Update the data catalog",
        "Close the migration tickets", "Review the cutover evidence", "Audit access to the archive", "Review the runbook", "Review the cost report",
    ];

    [Fact]
    public void Titles_with_a_shared_start_keep_the_word_that_differs()
    {
        var keys = TitleParts.Keys(["Backfill ledger partition 2025-Q1", "Backfill ledger partition 2025-Q2", "Plan the rollout"]);

        Assert.Equal([new TitleKey(4, 1, 0, 0), new TitleKey(4, 1, 0, 0), new TitleKey(1, 1, 1, 2)], keys);
    }

    [Fact]
    public void A_title_that_another_one_repeats_has_no_part_that_differs()
    {
        var keys = TitleParts.Keys(["Plan the rollout", "Plan the rollout", "Plan the rollout now"]);

        Assert.Equal(new TitleKey(0, 0, 0, 0), keys[0]);
        Assert.Equal(new TitleKey(4, 1, 0, 0), keys[2]);
    }

    [Fact]
    public void Titles_that_differ_in_an_inner_word_keep_it()
    {
        var keys = TitleParts.Keys(["Mirror the refund postings", "Mirror the invoice postings"]);

        Assert.Equal([new TitleKey(3, 2, 2, 3), new TitleKey(3, 2, 2, 3)], keys);
    }

    [AvaloniaFact]
    public void A_cut_title_whose_start_is_shared_keeps_its_inner_word_that_differs()
    {
        var text = new EllipsisText { Whole = "Mirror the refund postings", Key = new TitleKey(3, 2, 2, 3), FontSize = 13, MaxLines = 1, TextWrapping = Avalonia.Media.TextWrapping.WrapWithOverflow };
        using var layout = new Avalonia.Media.TextFormatting.TextLayout("…the refund…", new Avalonia.Media.Typeface(text.FontFamily), 13, null);

        text.Measure(new Avalonia.Size(Math.Ceiling(layout.Width) + 3, double.PositiveInfinity));

        Assert.Equal("…the refund…", text.Text);
    }

    /// <summary>The title text's size, lines, and width in canvas units at each tier that the review pack shows, and the full card's title.</summary>
    public static TheoryData<double, int, double, bool> Tiers => new()
    {
        { 13, 2, 170, false },
        { 17, 2, 197, true },
        { 20, 2, 197, true },
        { 28, 3, 210, true },
        { 30, 3, 206, true },
        { 37, 3, 206, true },
        { 37, 2, 206, true },
        { 44, 2, 206, true },
        { 40, 2, 206, true },
        { 46, 2, 206, true },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Tiers))]
    public void Every_cut_title_of_the_large_fixture_reads_apart_from_every_other_title(double size, int lines, double width, bool far)
    {
        var keys = TitleParts.Keys(LargeTitles);

        var shown = LargeTitles.Zip(keys, (title, key) =>
        {
            var text = new EllipsisText
            {
                Whole = far ? Theme.NameBreaks.Keep(title) : title, Key = key, FontSize = size, FontWeight = Avalonia.Media.FontWeight.Medium, MaxLines = lines,
                TextWrapping = far ? Avalonia.Media.TextWrapping.WrapWithOverflow : Avalonia.Media.TextWrapping.Wrap,
            };
            text.Measure(new Avalonia.Size(width, double.PositiveInfinity));
            return Theme.NameBreaks.Loose(text.Text!);
        }).ToArray();

        var repeated = shown.GroupBy(text => text).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.True(repeated.Length == 0, string.Join(" | ", repeated));
    }

    [AvaloniaFact]
    public void A_cut_title_keeps_the_part_that_differs_and_as_much_of_its_start_as_fits()
    {
        var text = new EllipsisText { Whole = "Backfill ledger partition 2025-Q1", Key = new TitleKey(4, 1, 0, 0), FontSize = 13, MaxLines = 1, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(120, double.PositiveInfinity));

        Assert.StartsWith("…", text.Text);
        Assert.EndsWith(" 2025-Q1", text.Text);
        Assert.EndsWith(text.Text![1..], "Backfill ledger partition 2025-Q1");
    }

    [AvaloniaFact]
    public void A_kept_token_wider_than_a_line_breaks_at_its_hyphen_so_the_part_that_differs_still_leads()
    {
        var whole = Theme.NameBreaks.Keep("Backfill ledger partition 2025-Q1")!;
        var text = new EllipsisText { Whole = whole, Key = new TitleKey(4, 1, 0, 0), FontSize = 13, MaxLines = 2, TextWrapping = Avalonia.Media.TextWrapping.WrapWithOverflow };

        text.Measure(new Avalonia.Size(50, double.PositiveInfinity));

        Assert.Equal("…2025-Q1", text.Text);
    }

    [AvaloniaFact]
    public void The_backfill_partitions_of_the_large_fixture_read_apart_when_their_titles_are_cut()
    {
        string[] titles = [.. new[] { "2023-Q4", "2024-Q1", "2024-Q2", "2024-Q3", "2024-Q4", "2025-Q1", "2025-Q2", "2025-Q3" }
            .Select(quarter => "Backfill ledger partition " + quarter)];
        var keys = TitleParts.Keys(titles);

        var shown = titles.Zip(keys, (title, key) =>
        {
            var text = new EllipsisText { Whole = title, Key = key, FontSize = 13, MaxLines = 2, TextWrapping = Avalonia.Media.TextWrapping.WrapWithOverflow };
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
        var text = new EllipsisText { Whole = Title, Key = new TitleKey(0, 3, 0, 0), FontSize = 13, MaxLines = 1, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(Shown, new Avalonia.Media.Typeface(text.FontFamily), 13, null);

        text.Measure(new Avalonia.Size(Math.Ceiling(layout.Width) + 3, double.PositiveInfinity));

        Assert.Equal(Shown, text.Text);
    }

    [AvaloniaFact]
    public void A_plain_cut_stays_when_the_title_shares_only_its_first_word()
    {
        var text = new EllipsisText { Whole = "Add the usage_events schema now", Key = new TitleKey(2, 0, 0, 0), FontSize = 13, MaxLines = 1, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(120, double.PositiveInfinity));

        Assert.StartsWith("Add the", text.Text);
        Assert.EndsWith("…", text.Text);
    }

    [AvaloniaFact]
    public void A_title_that_fits_ignores_the_part_it_would_keep()
    {
        var text = new EllipsisText { Whole = "Review", Key = new TitleKey(1, 1, 0, 0), FontSize = 13, MaxLines = 2, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(400, double.PositiveInfinity));

        Assert.Equal("Review", text.Text);
    }

    [AvaloniaFact]
    public void A_kept_part_that_leaves_no_room_for_the_start_gives_way_to_a_cut_at_the_end()
    {
        var text = new EllipsisText { Whole = "Reconcile the adjustment postings by tenant", Key = new TitleKey(5, 4, 0, 0), FontSize = 13, MaxLines = 2, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        text.Measure(new Avalonia.Size(70, double.PositiveInfinity));

        Assert.StartsWith("Recon", text.Text);
        Assert.Single(text.Text!, letter => letter == '…');
        Assert.EndsWith("…", text.Text);
    }
}
