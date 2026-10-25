using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace IDevelop.Nodes;

/// <summary>
/// Which implementers behind a Merge node get each of its review's findings (ADR 0041). A finding goes to the one
/// implementer whose change touched its file. A finding whose file several implementers touched, or none did, or that names
/// no file, goes to every implementer, and so does every finding while an implementer's files cannot be read.
/// </summary>
public static partial class FindingRouting
{
    /// <summary>
    /// Each implementer's findings, in the order of <paramref name="touched"/>, which holds each implementer's touched files
    /// from the repository's top, or null when they could not be read.
    /// </summary>
    public static ImmutableArray<ImmutableArray<Finding>> Route(IReadOnlyList<Finding> findings, IReadOnlyList<ImmutableHashSet<string>?> touched)
    {
        var routed = touched.Select(_ => ImmutableArray.CreateBuilder<Finding>()).ToArray();
        foreach (var finding in findings)
        {
            var file = File(finding.Location);
            var owners = file is null || touched.Any(files => files is null)
                ? []
                : Enumerable.Range(0, touched.Count).Where(index => Touches(touched[index]!, file)).ToArray();
            foreach (var index in owners.Length == 1 ? owners : Enumerable.Range(0, touched.Count).ToArray())
            {
                routed[index].Add(finding);
            }
        }

        return [.. routed.Select(builder => builder.ToImmutable())];
    }

    /// <summary>
    /// The file a finding's location names, such as <c>src/Calc.cs</c> for <c>src/Calc.cs:12</c>, with forward slashes and
    /// no leading <c>./</c>, or null when it names none.
    /// </summary>
    public static string? File(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var file = Position().Replace(location.Trim().Replace('\\', '/'), "");
        while (file.StartsWith("./", StringComparison.Ordinal)) file = file[2..];
        file = file.TrimStart('/');
        return file.Length == 0 ? null : file;
    }

    /// <summary>
    /// Whether a change that touched <paramref name="files"/> touched <paramref name="file"/>: the same path, or one that ends
    /// with it, as a location relative to a project folder below the repository's top reads.
    /// </summary>
    private static bool Touches(ImmutableHashSet<string> files, string file) =>
        files.Contains(file) || files.Any(path => path.EndsWith("/" + file, StringComparison.Ordinal));

    /// <summary>A line, a line range, or a column after the file, such as <c>:12</c>, <c>:12-14</c>, <c>:12:5</c>, or <c>#L12</c>.</summary>
    [GeneratedRegex(@"(:\d+(-\d+)?(:\d+)?|#L\d+(-L?\d+)?)$")]
    private static partial Regex Position();
}
