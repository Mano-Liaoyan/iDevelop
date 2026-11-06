namespace IDevelop.Desktop.Canvas;

/// <summary>Finds the part of each title that tells it from the most similar other title.</summary>
public static class TitleParts
{
    /// <summary>
    /// For each title, where the word starts that differs from the title that shares the longest start with it. That is 0 for a
    /// title that shares no first word, and for a title that another one repeats or merely continues, which keeps no part to show.
    /// </summary>
    public static int[] KeepFrom(IReadOnlyList<string> titles)
    {
        var order = Enumerable.Range(0, titles.Count).OrderBy(index => titles[index], StringComparer.OrdinalIgnoreCase).ToArray();
        var keeps = new int[titles.Count];
        for (var at = 0; at < order.Length; at++)
        {
            var title = titles[order[at]];
            var shared = Math.Max(
                at > 0 ? Shared(title, titles[order[at - 1]]) : 0,
                at < order.Length - 1 ? Shared(title, titles[order[at + 1]]) : 0);
            keeps[order[at]] = shared < title.Length ? WordStart(title, shared) : 0;
        }

        return keeps;
    }

    private static int Shared(string first, string second)
    {
        var length = 0;
        while (length < first.Length && length < second.Length && char.ToUpperInvariant(first[length]) == char.ToUpperInvariant(second[length]))
        {
            length++;
        }

        return length;
    }

    private static int WordStart(string title, int at)
    {
        if (char.IsWhiteSpace(title[at]))
        {
            return at + 1;
        }

        while (at > 0 && !char.IsWhiteSpace(title[at - 1]))
        {
            at--;
        }

        return at;
    }
}
