namespace IDevelop.Desktop.Theme;

/// <summary>
/// The words of a title that tell it from every other title in its workflow, as counts of the title's space-separated words.
/// <see cref="Head"/> is how many first words no other title starts with, <see cref="Tail"/> how many last words no other title
/// ends with, and <see cref="MiddleFrom"/> to <see cref="MiddleTo"/> the inner words that no other title holds. Each is 0 when the
/// title has no such words. A cut title that keeps one of these parts reads apart from every other title (ADR 0069).
/// Word counts stay true for the title's zoomed out text, whose hyphenated tokens join without a space.
/// </summary>
public readonly record struct TitleKey(int Head, int Tail, int MiddleFrom, int MiddleTo)
{
    /// <summary>The start of each space-separated word of <paramref name="text"/>.</summary>
    internal static int[] WordStarts(string text)
    {
        var starts = new List<int>();
        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] != ' ' && (at == 0 || text[at - 1] == ' '))
            {
                starts.Add(at);
            }
        }

        return [.. starts];
    }

    /// <summary>The end of the word that starts at <paramref name="start"/>.</summary>
    internal static int WordEnd(string text, int start)
    {
        var end = text.IndexOf(' ', start);
        return end < 0 ? text.Length : end;
    }
}
