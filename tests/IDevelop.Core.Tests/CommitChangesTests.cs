using IDevelop.Git;

namespace IDevelop.Core.Tests;

public sealed class CommitChangesTests
{
    [Fact]
    public void Numstat_gives_each_file_its_line_counts_and_a_binary_file_none() =>
        Assert.Equal(
            ["src/a.cs +12 -3", "logo.png + -"],
            CommitChanges.Parse("12\t3\tsrc/a.cs\n-\t-\tlogo.png\n").Select(change => $"{change.Path} +{change.Added} -{change.Deleted}"));

    [Fact]
    public void A_diff_starts_at_its_first_hunk_and_keeps_to_its_limit()
    {
        var diff = "diff --git a/f b/f\nindex 1..2\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-old\n+new\n";
        Assert.Equal(["@@ -1 +1 @@", "-old"], CommitChanges.Hunks(diff, 2));
    }
}
