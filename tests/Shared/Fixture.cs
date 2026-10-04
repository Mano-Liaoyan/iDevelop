namespace IDevelop.TestSupport;

/// <summary>The recorded client output in tests/Shared/Fixtures, which each test project copies to its output.</summary>
internal static class Fixture
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name)).Replace("\r\n", "\n");

    public static IEnumerable<string> Lines(string name) => Text(name).Split('\n').Where(line => line.Length > 0);
}
