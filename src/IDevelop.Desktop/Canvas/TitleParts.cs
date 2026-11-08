using IDevelop.Desktop.Theme;

namespace IDevelop.Desktop.Canvas;

/// <summary>Finds the words of each title that tell it from every other title in the workflow.</summary>
public static class TitleParts
{
    /// <summary>
    /// For each title, the fewest first words, the fewest last words, and the shortest run of inner words that no other title
    /// starts with, ends with, or holds. A title that another one repeats has none of them.
    /// </summary>
    public static TitleKey[] Keys(IReadOnlyList<string> titles) => [.. titles.Select((title, index) => Key(title, [.. titles.Where((_, other) => other != index)]))];

    private static TitleKey Key(string title, string[] others)
    {
        var starts = TitleKey.WordStarts(title);
        var count = starts.Length;
        var head = Enumerable.Range(1, count).FirstOrDefault(words =>
            !others.Any(other => other.StartsWith(title[..TitleKey.WordEnd(title, starts[words - 1])], StringComparison.OrdinalIgnoreCase)));
        var tail = Enumerable.Range(1, Math.Max(0, count - 1)).FirstOrDefault(words =>
            !others.Any(other => other.EndsWith(title[starts[count - words]..], StringComparison.OrdinalIgnoreCase)));
        for (var length = 1; length <= count - 2; length++)
        {
            for (var from = 1; from + length < count; from++)
            {
                var span = title[starts[from]..TitleKey.WordEnd(title, starts[from + length - 1])];
                if (!others.Any(other => other.Contains(span, StringComparison.OrdinalIgnoreCase)))
                {
                    return new(head, tail, from, from + length);
                }
            }
        }

        return new(head, tail, 0, 0);
    }
}
