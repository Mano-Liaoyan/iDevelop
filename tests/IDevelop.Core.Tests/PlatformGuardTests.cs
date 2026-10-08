using System.Text.RegularExpressions;

namespace IDevelop.Core.Tests;

public sealed class PlatformGuardTests
{
    [Fact]
    public void Platform_exclusions_are_reported_as_skips_instead_of_successful_early_returns()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "IDevelop.Core.Tests.csproj")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        var guard = new Regex(@"if \([^\n]*OperatingSystem\.Is\w+\(\)[^\n]*\)\s*return;");
        var matches = Directory.EnumerateFiles(folder.FullName, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .SelectMany(file =>
            {
                var source = File.ReadAllText(file);
                return guard.Matches(source).Select(match =>
                    $"{Path.GetRelativePath(folder.FullName, file)}:{source[..match.Index].Count(character => character == '\n') + 1}");
            }).ToArray();
        Assert.True(matches.SequenceEqual(Array.Empty<string>()), string.Join(Environment.NewLine, matches));
    }
}
