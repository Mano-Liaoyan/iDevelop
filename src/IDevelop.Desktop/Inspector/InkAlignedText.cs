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

    // The text draws shifted right by the space its last glyph keeps clear on its right.
    protected override void RenderTextLayout(DrawingContext context, Point origin) =>
        base.RenderTextLayout(context, origin + new Point(InkGap(), 0));

    private double InkGap() =>
        TextLayout.TextLines.LastOrDefault()?.TextRuns.OfType<ShapedTextRun>().LastOrDefault()?.GlyphRun is { InkBounds.Width: > 0 } run
            ? Math.Max(0, run.Bounds.Width - run.InkBounds.Right)
            : 0;
}
