using System.Collections.Immutable;
using System.Text;
using IDevelop.Execution;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests.Git;

[Collection(ProcessCollection.Name)]
public sealed class MergeTreeTests
{
    [Fact]
    public void Committer_timestamps_preserve_argument_order_and_count()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var environment = new Dictionary<string, string>(f.Environment) { ["GIT_COMMITTER_DATE"] = "2026-10-07T00:02:00Z" };
        Assert.Equal(0, f.Run(f.Folder, environment, "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-q", "-m", "b").ExitCode);
        var b = new CommitId(f.Git("rev-parse", "HEAD").Trim());
        environment["GIT_COMMITTER_DATE"] = "2026-10-07T00:01:00Z";
        Assert.Equal(0, f.Run(f.Folder, environment, "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-q", "-m", "c").ExitCode);
        var c = new CommitId(f.Git("rev-parse", "HEAD").Trim());
        var repository = f.Open();
        Assert.Equal(new[] { new DateTimeOffset(2026, 10, 7, 0, 2, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 7, 0, 1, 0, TimeSpan.Zero) }, GitFixture.Read(repository.CommitterTimestamps([b, a, c])));
        Assert.Equal("Git returned a different committer timestamp count.",
            Assert.IsType<GitRead<ImmutableArray<DateTimeOffset>>.Failed>(repository.CommitterTimestamps([a, a])).Detail);
    }

    [Fact]
    public void Clean_merge_returns_the_literal_tree()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        f.Git("checkout", "-q", "-b", "b", a.Hex);
        f.Write("b.txt", "B\n");
        var b = f.Commit("b");
        f.Git("checkout", "-q", "-b", "c", a.Hex);
        f.Write("c.txt", "C\n");
        var c = f.Commit("c");
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", b.Hex);
        Assert.Equal("7025720b8121cfd45a182b2ead881a0c0461beb0", c.Hex);
        Assert.Equal("0f9ebdad5c1d87d0be67f3e280cb51f62545a42a", Assert.IsType<TreeMerge.Clean>(f.Open().MergeTrees(b, c)).Tree.Hex);
    }

    [Fact]
    public void Conflict_returns_literal_stages_messages_and_raw_diagnostics()
    {
        using var f = new GitFixture();
        f.Diamond();
        f.Write("settings.txt", "0\n");
        var a = f.Commit("settings");
        f.Git("checkout", "-q", "-b", "b", a.Hex);
        f.Write("settings.txt", "b=1\n");
        var b = f.Commit("b");
        f.Git("checkout", "-q", "-b", "c", a.Hex);
        f.Write("settings.txt", "c=1\n");
        var c = f.Commit("c");
        var conflict = Assert.IsType<TreeMerge.Conflicted>(f.Open().MergeTrees(b, c));
        Assert.Equal(new[] { new StageEntry("100644", "573541ac9702dd3969c9bc859d2b91ec1f7e6e56", 1, "settings.txt"), new StageEntry("100644", "2e9833d602b8042bdd0e77f3a31aca12944ec21d", 2, "settings.txt"),
            new StageEntry("100644", "986c6e7523fb2a807d8e3526a127e1ced5bc44e4", 3, "settings.txt") }, conflict.Stages);
        Assert.Equal(new[] { "Auto-merging", "CONFLICT (contents)" }, conflict.Messages.Select(message => message.Type));
        Assert.Equal(new[] { "Auto-merging settings.txt\n", "CONFLICT (content): Merge conflict in settings.txt\n" }, conflict.Messages.Select(message => message.Text));
        Assert.Equal(new[] { "settings.txt", "settings.txt" }, conflict.Messages.SelectMany(message => message.Paths));
        Assert.Contains("CONFLICT (content): Merge conflict in settings.txt\n", Encoding.UTF8.GetString(conflict.Stdout));
        Assert.Equal("", conflict.Stderr);
    }

    [Fact]
    public void Unknown_revision_is_a_failure_even_when_git_exits_one()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var result = f.Open().MergeTrees(a, new("1111111111111111111111111111111111111111"));
        Assert.Equal("merge-tree: 1111111111111111111111111111111111111111 - not something we can merge\n", Assert.IsType<TreeMerge.Failed>(result).Detail);
    }

    [Fact]
    public void Merge_configuration_reads_empty_and_configured_values()
    {
        using var f = new GitFixture();
        f.Git("config", "--unset", "core.autocrlf");
        var repository = f.Open();
        Assert.Equal(Array.Empty<ConfigEntry>(), GitFixture.Read(repository.MergeConfig()));
        f.Git("config", "merge.conflictStyle", "diff3");
        Assert.Equal(new[] { new ConfigEntry("merge.conflictstyle", "diff3") }, GitFixture.Read(repository.MergeConfig()));
    }
}
