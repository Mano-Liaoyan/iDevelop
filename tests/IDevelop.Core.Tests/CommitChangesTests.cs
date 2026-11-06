using IDevelop.Git;

namespace IDevelop.Core.Tests;

public sealed class CommitChangesTests
{
    [Fact]
    public void Numstat_gives_each_file_its_line_counts_and_a_binary_file_none() =>
        Assert.Equal(
            ["src/a.cs +12 -3", "logo.png + -"],
            CommitChanges.Parse("12\t3\tsrc/a.cs\n-\t-\tlogo.png\n").Select(change => $"{change.Path} +{change.Added} -{change.Deleted}"));
}
