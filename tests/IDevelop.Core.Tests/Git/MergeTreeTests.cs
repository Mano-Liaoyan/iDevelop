using System.Collections.Immutable;
using System.Text;
using System.Runtime.InteropServices;
using IDevelop.Execution;
using IDevelop.TestSupport;

namespace IDevelop.Core.Tests.Git;

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
    public void A_named_merge_base_replays_only_the_change_after_it()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        f.Write("f.txt", "1\n");
        var start = f.Commit("start");
        f.Git("checkout", "-q", "-b", "old", start.Hex);
        f.Write("f.txt", "2\n");
        var old = f.Commit("old");
        f.Git("checkout", "-q", "-b", "work", old.Hex);
        f.Write("g.txt", "G\n");
        var work = f.Commit("work");
        f.Git("checkout", "-q", "-b", "updated", start.Hex);
        f.Write("h.txt", "H\n");
        var updated = f.Commit("updated");
        var repository = f.Open();
        var replayed = Assert.IsType<TreeMerge.Clean>(repository.MergeTrees(updated, work, a, mergeBase: old)).Tree;
        Assert.Equal(["1\n", "G\n", "H\n"], new[] { "f.txt", "g.txt", "h.txt" }.Select(file => f.Git("show", $"{replayed.Hex}:{file}")));
        // Git's own merge base would also carry the old base's f.txt.
        var merged = Assert.IsType<TreeMerge.Clean>(repository.MergeTrees(updated, work, a)).Tree;
        Assert.Equal("2\n", f.Git("show", $"{merged.Hex}:f.txt"));
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
        Assert.Equal("0f9ebdad5c1d87d0be67f3e280cb51f62545a42a", Assert.IsType<TreeMerge.Clean>(f.Open().MergeTrees(b, c, a)).Tree.Hex);
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
        var conflict = Assert.IsType<TreeMerge.Conflicted>(f.Open().MergeTrees(b, c, a));
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
        var result = f.Open().MergeTrees(a, new("1111111111111111111111111111111111111111"), a);
        Assert.Equal("merge-tree: 1111111111111111111111111111111111111111 - not something we can merge\n", Assert.IsType<TreeMerge.Failed>(result).Detail);
    }

    [Fact]
    public void Depth_one_clone_merges_descendants_using_its_shallow_boundary()
    {
        using var source = new GitFixture();
        source.Diamond();
        using var f = new GitFixture(initialize: false);
        Assert.Equal(0, source.Run(source.Folder, "clone", "-q", "--depth", "1", new Uri(source.Folder + Path.DirectorySeparatorChar).AbsoluteUri, f.Folder).ExitCode);
        var a = new CommitId(f.Git("rev-parse", "HEAD").Trim());
        f.Git("checkout", "-q", "-b", "b", a.Hex);
        f.Write("b.txt", "B\n");
        var b = f.Commit("b");
        f.Git("checkout", "-q", "-b", "c", a.Hex);
        f.Write("c.txt", "C\n");
        var c = f.Commit("c");
        Assert.Equal("true\n", f.Git("rev-parse", "--is-shallow-repository"));
        Assert.Equal("0f9ebdad5c1d87d0be67f3e280cb51f62545a42a", Assert.IsType<TreeMerge.Clean>(f.Open().MergeTrees(b, c, a)).Tree.Hex);
    }

    [Fact]
    public void Sha256_repository_merges_to_a_literal_64_hex_tree()
    {
        using var f = new GitFixture(initialize: false);
        f.Git("init", "-q", "--object-format=sha256", "-b", "main");
        f.Write("root.txt", "root\n");
        var a = f.Commit("root");
        f.Git("checkout", "-q", "-b", "b", a.Hex);
        f.Write("b.txt", "B\n");
        var b = f.Commit("b");
        f.Git("checkout", "-q", "-b", "c", a.Hex);
        f.Write("c.txt", "C\n");
        var c = f.Commit("c");
        Assert.Equal("7c1c23fd2551de118a1d46a5957ece38f87069dd50603ad4a6ac420b888ca245", Assert.IsType<TreeMerge.Clean>(f.Open().MergeTrees(b, c, a)).Tree.Hex);
    }

    [UnixFact]
    public void Clean_merge_with_a_non_utf8_path_returns_the_literal_tree()
    {
        using var f = new GitFixture();
        f.Diamond();
        WriteRaw("0\n1\n2\n3\n4\n5\n6\n7\n8\n");
        var a = f.Commit("raw");
        f.Git("checkout", "-q", "-b", "b", a.Hex);
        WriteRaw("b=1\n1\n2\n3\n4\n5\n6\n7\n8\n");
        var b = f.Commit("b");
        f.Git("checkout", "-q", "-b", "c", a.Hex);
        WriteRaw("0\n1\n2\n3\n4\n5\n6\n7\nc=1\n");
        var c = f.Commit("c");
        Assert.Equal(0, Unlink([.. Encoding.UTF8.GetBytes(f.Folder + "/k"), 255, .. Encoding.UTF8.GetBytes(".txt"), 0]));
        Assert.Equal("7e41f9122663f203d727f7b2eeeba497522df4ba", Assert.IsType<TreeMerge.Clean>(f.Open().MergeTrees(b, c, a)).Tree.Hex);

        void WriteRaw(string text)
        {
            byte[] path = [.. Encoding.UTF8.GetBytes(f.Folder + "/k"), 255, .. Encoding.UTF8.GetBytes(".txt"), 0];
            var file = Fopen(path, "wb");
            Assert.NotEqual(IntPtr.Zero, file);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                Assert.Equal((nuint)bytes.Length, Fwrite(bytes, 1, (nuint)bytes.Length, file));
            }
            finally { Assert.Equal(0, Fclose(file)); }
        }
    }

    [DllImport("libc", EntryPoint = "unlink")]
    private static extern int Unlink(byte[] path);

    [DllImport("libc", EntryPoint = "fopen")]
    private static extern IntPtr Fopen(byte[] path, [MarshalAs(UnmanagedType.LPStr)] string mode);

    [DllImport("libc", EntryPoint = "fwrite")]
    private static extern nuint Fwrite(byte[] bytes, nuint size, nuint count, IntPtr file);

    [DllImport("libc", EntryPoint = "fclose")]
    private static extern int Fclose(IntPtr file);
}
