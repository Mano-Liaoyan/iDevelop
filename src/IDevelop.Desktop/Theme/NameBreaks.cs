using Avalonia.Data.Converters;

namespace IDevelop.Desktop.Theme;

/// <summary>Keeps a name's hyphenated token, such as 2023-Q4 or dual-write, on one line where the name wraps.</summary>
public static class NameBreaks
{
    private const string WordJoiner = "⁠";

    public static readonly IValueConverter KeepTokens = new FuncValueConverter<string?, string?>(Keep);

    internal static string? Keep(string? name) => name?.Replace("-", "-" + WordJoiner);
}
