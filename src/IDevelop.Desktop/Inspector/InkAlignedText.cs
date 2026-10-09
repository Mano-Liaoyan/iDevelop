using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// Text whose drawing, not its advance, ends at its right edge, such as a count that ends where the glyphs beside it end
/// drawing. A glyph's own side spacing would otherwise leave a "1" a few pixels short of a "4".
/// </summary>
public sealed class InkAlignedText : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void RenderTextLayout(DrawingContext context, Point origin) =>
        base.RenderTextLayout(context, origin + new Point(InkGap(origin.X), 0));

    // How far the text's drawing ends before the box's right edge: the space its last glyph keeps clear on its right, and
    // the part of a pixel that layout rounding added to the box's width.
    private double InkGap(double left)
    {
        if (TextLayout.TextLines.LastOrDefault() is not { } line
            || line.TextRuns.OfType<ShapedTextRun>().LastOrDefault()?.GlyphRun is not { InkBounds.Width: > 0 } run)
        {
            return 0;
        }

        var inkRight = left + line.Start + line.WidthIncludingTrailingWhitespace - (run.Bounds.Width - run.InkBounds.Right);
        return Math.Max(0, Bounds.Width - Padding.Right - inkRight);
    }
}
