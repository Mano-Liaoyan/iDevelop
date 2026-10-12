using System.Text.RegularExpressions;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests;

public sealed class PlatformGuardTests
{
    private static readonly Regex Guard = new(@"if \([^\n]*OperatingSystem\.Is\w+\(\)[^\n]*\)\s*return;");

    [Fact]
    public void Platform_exclusions_are_reported_as_skips_instead_of_successful_early_returns()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "IDevelop.Core.Tests.csproj")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        var matches = EarlyReturns(folder.FullName);
        Assert.True(matches.SequenceEqual(Array.Empty<string>()), string.Join(Environment.NewLine, matches));
    }

    // Other tests write scratch projects with .cs files under bin while the scan runs, and delete them again (#86).
    [Fact]
    public void The_scan_reads_the_project_sources_and_never_its_build_output()
    {
        using var temp = new TempFolder();
        var project = temp.Create("project");
        foreach (var folder in new[] { "Runs", "bin", "obj" })
        {
            Directory.CreateDirectory(Path.Combine(project, folder, "Release"));
            // Split, so the scan of this file finds no early return in the literal.
            File.WriteAllText(Path.Combine(project, folder, "Release", "Scratch.cs"), "if (OperatingSystem." + "IsWindows()) return;\n");
        }

        Assert.Equal([Path.Combine("Runs", "Release", "Scratch.cs") + ":1"], EarlyReturns(project));
    }

    /// <summary>Each early return on a platform check in the project's sources, as "file:line". The build output stays out.</summary>
    private static string[] EarlyReturns(string project) =>
        [.. Directory.EnumerateFiles(project, "*.cs")
            .Concat(Directory.EnumerateDirectories(project)
                .Where(folder => Path.GetFileName(folder) is not ("bin" or "obj"))
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)))
            .Order(StringComparer.Ordinal)
            .SelectMany(file =>
            {
                var source = File.ReadAllText(file);
                return Guard.Matches(source).Select(match =>
                    $"{Path.GetRelativePath(project, file)}:{source[..match.Index].Count(character => character == '\n') + 1}");
            })];
}
