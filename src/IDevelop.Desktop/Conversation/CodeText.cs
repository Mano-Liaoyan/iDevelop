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
    private int _columns = -1;

    public CodeText(string source, string? language)
    {
        _source = source;
        _language = language;
        Show(source);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>
    /// The source with each line over <paramref name="columns"/> broken at its last space or comma that fits, or at the limit
    /// when it has neither. Each continued line starts at the source line's own indent plus <see cref="Continuation"/>.
    /// </summary>
    internal static string Wrapped(string source, int columns)
    {
        var shown = new StringBuilder();
        foreach (var (line, at) in source.Split('\n').Select((line, at) => (line, at)))
        {
            if (at > 0)
            {
                shown.Append('\n');
            }

            var indent = line[..(line.Length - line.TrimStart().Length)] + Continuation;
            var room = columns;
            var left = line;
            while (left.Length > room && room > 0)
            {
                var cut = left.LastIndexOfAny([' ', ','], room - 1, room) + 1;
                cut = cut > 0 && left[..cut].Trim().Length > 0 ? cut : room;
                shown.Append(left[..cut].TrimEnd()).Append('\n').Append(indent);
                left = left[cut..].TrimStart();
                room = columns - indent.Length;
            }

            shown.Append(left);
        }

        return shown.ToString();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
        {
            var columns = (int)(availableSize.Width / CharacterWidth());
            if (columns != _columns && columns > 0)
            {
                _columns = columns;
                Show(Wrapped(_source, columns));
            }
        }

        return base.MeasureOverride(availableSize);
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
