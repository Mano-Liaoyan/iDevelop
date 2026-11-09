using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Theme;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Each text that can lose words fits its box, wraps in it, or ends in an ellipsis: at each zoom tier for a card's title, and
/// at the narrowest and widest inspector for a result's heading and commit.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class TextFitTests
{
    private const string LongWord = "Add the usage_events_partitioned schema";

    /// <summary>One zoom in each tier, the 69% of a full inspector among them.</summary>
    public static TheoryData<double> Zooms => [1.0, 0.7, 0.55, 0.4, 0.276, 0.25, 0.3];

    [AvaloniaTheory]
    [MemberData(nameof(Zooms))]
    public void A_card_title_with_a_long_word_fits_its_card_or_ends_in_an_ellipsis(double zoom)
    {
        using var f = new NodeFixture(Task(TestTasks.Design, LongWord, 105), Task(TestTasks.Build, "Add the usage_events schema", 400));
        var shell = f.Open();
        foreach (var step in new[] { 1.0, 0.45, 0.42, 0.4, 0.38, 0.36, 0.35, 0.34, 0.33, 0.32, 0.31, 0.3, 0.29, 0.285, 0.28, zoom })
        {
            shell.Window.ViewModel.Canvas!.ViewportZoom = step;
            shell.Render();
        }

        foreach (var title in new[] { LongWord, "Add the usage_events schema" })
        {
            foreach (var text in shell.Node(title).GetVisualDescendants().OfType<EllipsisText>().Where(text => text.IsEffectivelyVisible))
            {
                AssertFits(text);
            }
        }
    }

    /// <summary>The Output card's heading row at widths from under the narrowest inspector's card to the widest's.</summary>
    [AvaloniaTheory]
    [InlineData(140)]
    [InlineData(200)]
    [InlineData(232)]
    [InlineData(260)]
    [InlineData(470)]
    public void A_results_commit_stays_whole_in_the_output_card(double width)
    {
        var heading = new TextBlock { Text = "Result 12 · Current", FontSize = 13, FontWeight = FontWeight.SemiBold };
        var commit = new TextBlock { Text = "Commit acc31d9", FontSize = 12, FontFamily = new FontFamily("monospace") };
        var row = new SpillRow { MinLeadWidth = double.MaxValue, LineSpacing = 2, ItemsAlignment = SpillAlignment.Start, Width = width, Children = { heading, commit } };
        var window = new Window { Content = row, Width = 600, Height = 200 };
        window.Show();
        try
        {
            foreach (var text in new[] { heading, commit })
            {
                var box = text.Bounds;
                Assert.True(box.X >= 0 && box.Right <= width + 0.5, $"'{text.Text}' spans {box.X:0.#} to {box.Right:0.#} in a {width} px row.");
                Assert.True(box.Width >= text.DesiredSize.Width - 0.5, $"'{text.Text}' has {box.Width:0.#} of its {text.DesiredSize.Width:0.#} px.");
            }

            Assert.True(commit.Bounds.Top >= heading.Bounds.Bottom || commit.Bounds.Left >= heading.Bounds.Right, "The commit overlaps the heading.");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Text that a box shows in full or cut with an ellipsis: no line is wider than the box, and the lines stay in it.</summary>
    internal static void AssertFits(TextBlock text)
    {
        // The window at 128% rounds a box to whole device pixels, so the box can lose up to one 1/1.28 px from what it measured.
        var width = text.Bounds.Width - text.Padding.Left - text.Padding.Right - 1 / 1.28;
        var shown = text.Text ?? "";
        using var layout = new TextLayout(shown, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            text.Foreground, textWrapping: text.TextWrapping, maxWidth: Math.Max(1, width), lineHeight: text.LineHeight);
        Assert.True(layout.TextLines.All(line => line.Width <= width),
            $"'{shown}' has a line wider than its {width:0.#} px box at {text.FontSize} px.");
        if (text.MaxLines > 0)
        {
            Assert.True(layout.TextLines.Count <= text.MaxLines, $"'{shown}' takes {layout.TextLines.Count} lines of {text.MaxLines}.");
        }

        if (text is EllipsisText { Whole: { } whole } && whole != shown)
        {
            Assert.Contains("…", shown);
        }

        // The box itself stays in its parent, so nothing clips the text.
        if (text.GetVisualParent() is Visual parent)
        {
            Assert.True(text.Bounds.Right <= parent.Bounds.Width + 0.5, $"'{shown}' ends at {text.Bounds.Right:0.#} in a parent {parent.Bounds.Width:0.#} wide.");
        }
    }
}
