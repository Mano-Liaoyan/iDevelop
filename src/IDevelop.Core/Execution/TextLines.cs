namespace IDevelop.Execution;

/// <summary>The lines of a client's output or message that hold more than white space.</summary>
internal static class TextLines
{
    /// <summary>Without their line ends, which can be \n or \r\n.</summary>
    public static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0);

    public static string? FirstLine(string text) => Lines(text).Select(line => line.Trim()).FirstOrDefault();

    public static string? LastLine(string text) => Lines(text).Select(line => line.Trim()).LastOrDefault();
}
