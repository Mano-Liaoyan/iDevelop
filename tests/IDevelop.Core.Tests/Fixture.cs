namespace IDevelop.Core.Tests;

internal static class Fixture
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name)).Replace("\r\n", "\n");

    public static IEnumerable<string> Lines(string name) => Text(name).Split('\n').Where(line => line.Length > 0);
}
