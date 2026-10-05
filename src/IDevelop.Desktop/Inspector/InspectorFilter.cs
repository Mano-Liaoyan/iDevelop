namespace IDevelop.Desktop.Inspector;

/// <summary>How the inspector's filter matches a row label or a section title, as Godot's inspector does.</summary>
public static class InspectorFilter
{
    /// <summary>
    /// True for an empty query, a case-insensitive substring of <paramref name="text"/>, or the query's letters in
    /// order with gaps, so "rsn" finds Reasoning. Spaces in the query are ignored.
    /// </summary>
    public static bool Matches(string? query, string? text)
    {
        var letters = (query ?? "").Where(letter => !char.IsWhiteSpace(letter)).ToArray();
        if (letters.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Contains(query!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var next = 0;
        foreach (var letter in text)
        {
            if (char.ToUpperInvariant(letter) == char.ToUpperInvariant(letters[next]) && ++next == letters.Length)
            {
                return true;
            }
        }

        return false;
    }
}
