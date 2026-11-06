using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// Where a scrolled view rests so that its top edge lies between two lines of text. A view that rests in a line would show
/// that line cut through its letters.
/// </summary>
internal static class WholeLine
{
    /// <summary>
    /// The nearest height to <paramref name="y"/>, in <paramref name="content"/>'s own units, that no line of text crosses:
    /// above the crossed lines, or below them when <paramref name="down"/> says the view moves down.
    /// </summary>
    public static double Rest(Control content, double y, bool down)
    {
        var lines = Crossed(content, -content.Bounds.Y, y).ToList();
        return lines.Count == 0 ? y : down ? lines.Max(line => line.Bottom) : lines.Min(line => line.Top);
    }

    // Only a child that reaches the height can hold a line there, so most of the tree is never walked.
    private static IEnumerable<(double Top, double Bottom)> Crossed(Visual visual, double origin, double y)
    {
        var top = origin + visual.Bounds.Y;
        if (!visual.IsVisible || y <= top || y >= top + visual.Bounds.Height)
        {
            return [];
        }

        if (visual is TextBlock text)
        {
            return Lines(text, top).Where(line => line.Top + Tolerance < y && y < line.Bottom - Tolerance);
        }

        return visual.GetVisualChildren().SelectMany(child => Crossed(child, top, y));
    }

    private static IEnumerable<(double Top, double Bottom)> Lines(TextBlock text, double top)
    {
        var at = top + text.Padding.Top;
        foreach (var line in text.TextLayout.TextLines)
        {
            yield return (at, at + line.Height);
            at += line.Height;
        }
    }

    // An edge this close to a line's own edge cuts no letter.
    private const double Tolerance = 0.5;
}
