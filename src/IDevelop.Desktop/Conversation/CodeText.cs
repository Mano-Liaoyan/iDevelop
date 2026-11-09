using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IDevelop.Desktop.Conversation;

/// <summary>
/// A code block's text. A source line too long for the block continues on an indented line, so the eye still finds where
/// each source line starts. The shown text follows the block's width, and Copy always takes the source.
/// </summary>
public sealed class CodeText : SelectableTextBlock
{
    /// <summary>The mark that starts a continued line, past the indent of its source line, so a wrap never reads as a new source line.</summary>
    private const string Continuation = "  ↪ ";

    private const string Probe = "0000000000";

    private readonly string _source;
    private readonly string? _language;
    private readonly int _reservedFirstLine;
    private int _columns = -1;

    /// <param name="reservedFirstLine">The columns that the corner's tools take from the first line.</param>
    public CodeText(string source, string? language, int reservedFirstLine = 0)
    {
        _source = source;
        _language = language;
        _reservedFirstLine = reservedFirstLine;
        Show(source);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>
    /// The source with each line over <paramref name="columns"/> broken at its last space or comma that fits, then at the last
    /// other mark between tokens, and at the limit only when a single token is too long. Each continued line starts at the
    /// source line's own indent plus <see cref="Continuation"/>. The first line stays <paramref name="reservedFirstLine"/> short of the limit.
    /// </summary>
    internal static string Wrapped(string source, int columns, int reservedFirstLine = 0)
    {
        var shown = new StringBuilder();
        foreach (var (line, at) in source.Split('\n').Select((line, at) => (line, at)))
        {
            if (at > 0)
            {
                shown.Append('\n');
            }

            var indent = line[..(line.Length - line.TrimStart().Length)] + Continuation;
            var room = at == 0 && columns > reservedFirstLine ? columns - reservedFirstLine : columns;
            var left = line;
            while (left.Length > room && room > 0)
            {
                var cut = CutAt(left, room);
                shown.Append(left[..cut].TrimEnd()).Append('\n').Append(indent);
                left = left[cut..].TrimStart();
                room = columns - indent.Length;
            }

            shown.Append(left);
        }

        return shown.ToString();
    }

    // Where a line breaks within room. A space or comma comes first, then the end of any other mark, so that no name splits unless it is longer than a line.
    private static int CutAt(string line, int room)
    {
        var soft = line.LastIndexOfAny([' ', ','], room - 1, room) + 1;
        if (soft > 0 && line[..soft].Trim().Length > 0)
        {
            return soft;
        }

        for (var at = room - 1; at > 0; at--)
        {
            if (!char.IsLetterOrDigit(line[at]) && line[at] != '_' && !char.IsWhiteSpace(line[at]))
            {
                return at + 1;
            }
        }

        return room;
    }

    // The first measure wraps at the width it is offered. After that only the arranged width re-wraps: a measure pass can offer several widths in a row,
    // and re-wrapping on each would invalidate the layout that measures it until the layout gives up.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_columns < 0 && double.IsFinite(availableSize.Width))
        {
            Wrap(availableSize.Width);
        }

        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Wrap(finalSize.Width);
        return base.ArrangeOverride(finalSize);
    }

    private void Wrap(double width)
    {
        var columns = (int)(width / CharacterWidth());
        if (columns != _columns && columns > 0)
        {
            _columns = columns;
            Show(Wrapped(_source, columns, _reservedFirstLine));
        }
    }

    private double CharacterWidth()
    {
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(Probe, new Typeface(FontFamily, FontStyle, FontWeight), FontSize, Foreground);
        return Math.Max(1, layout.Width / Probe.Length);
    }

    private void Show(string text)
    {
        if (CodeColors.Runs(text, _language) is { } runs)
        {
            Inlines = [.. runs];
        }
        else
        {
            Text = text;
        }
    }
}
