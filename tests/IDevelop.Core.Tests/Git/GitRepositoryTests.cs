using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Git.GitFixture;

namespace IDevelop.Core.Tests.Git;

[Collection(ProcessCollection.Name)]
public sealed class GitRepositoryTests
{
    [Theory]
    [InlineData("git version 2.55.0", 2, 55, 0)]
    [InlineData("git version 2.39.5 (Apple Git-154)", 2, 39, 5)]
    [InlineData("git version 2.47.1.windows.1", 2, 47, 1)]
    public void Supported_versions_parse_vendor_suffixes(string text, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), GitRepository.ParseVersion(text));
        Assert.Null(GitRepository.CheckVersion(text));
    }

    [Theory]
    [InlineData("git version 2.38.1")]
    [InlineData("not a Git version")]
    public void Unsupported_or_unknown_version_is_refused(string text)
    {
        var refused = Assert.IsType<RepositoryOpen.Refused>(GitRepository.CheckVersion(text));
        Assert.Equal(MaterializationProblem.GitVersionUnsupported, refused.Problem);
        Assert.Equal(text, refused.Detail);
    }

    [Fact]
    public void Open_requires_a_toplevel_and_discovers_linked_worktree_identity()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        Assert.Equal(f.Git("rev-parse", "--show-toplevel").Trim(), repository.ProjectFolder);
        Assert.Equal(f.Git("rev-parse", "--path-format=absolute", "--git-common-dir").Trim(), repository.CommonDirectory);
        Directory.CreateDirectory(f.PathOf("sub"));
        var refused = Assert.IsType<RepositoryOpen.Refused>(GitRepository.Open(f.PathOf("sub"), f.Environment));
        Assert.Equal(MaterializationProblem.NotRepositoryRoot, refused.Problem);
        Assert.Contains(repository.ProjectFolder, refused.Detail);
        Assert.Equal(0, repository.AddWorktree(".worktrees/r/t", "idp/r/task/t", a).ExitCode);
        var linked = f.Open(f.PathOf(".worktrees/r/t"));
        Assert.Equal(repository.CommonDirectory, linked.CommonDirectory);
        Assert.Equal(f.Run(f.PathOf(".worktrees/r/t"), "rev-parse", "--show-toplevel").Text.Trim(), linked.ProjectFolder);
        Assert.True(File.Exists(f.PathOf(".worktrees/r/t/.git")));
        Assert.Equal(f.Git("rev-parse", "--path-format=absolute", "--git-path", "index").Trim(), Read(repository.IndexPath(f.Folder)));
        Assert.Equal(f.Run(linked.ProjectFolder, "rev-parse", "--path-format=absolute", "--git-path", "index").Text.Trim(), Read(repository.IndexPath(linked.ProjectFolder)));
    }

    [Fact]
    public void Open_refuses_a_nonrepository()
    {
        using var f = new GitFixture(initialize: false);
        var refused = Assert.IsType<RepositoryOpen.Refused>(GitRepository.Open(f.Folder, f.Environment));
        Assert.Equal(MaterializationProblem.NotRepositoryRoot, refused.Problem);
        Assert.Contains("not a git repository", refused.Detail);
    }

    [Fact]
    public void Excludes_preserve_existing_bytes_and_apply_in_both_checkouts()
    {
        using var f = new GitFixture();
        f.Diamond();
        f.Write("src/a.txt", "ordinary\n");
        var a = f.Commit("source");
        var repository = f.Open();
        var exclude = Path.Combine(repository.CommonDirectory, "info", "exclude");
        File.WriteAllBytes(exclude, Encoding.UTF8.GetBytes("keep-me"));
        repository.EnsureExcluded();
        var bytes = File.ReadAllBytes(exclude);
        Assert.Equal("keep-me\n/.worktrees/\n/.idp/inputs/\n/.idp/outbox/\n", Encoding.UTF8.GetString(bytes));
        repository.EnsureExcluded();
        Assert.Equal(bytes, File.ReadAllBytes(exclude));
        Assert.Equal(0, repository.AddWorktree(".worktrees/r/t", "idp/r/task/t", a).ExitCode);
        foreach (var checkout in new[] { f.Folder, f.PathOf(".worktrees/r/t") })
        {
            Assert.Equal(0, repository.CheckIgnore(checkout, ".idp/inputs/x/report.md").ExitCode);
            Assert.Equal(0, repository.CheckIgnore(checkout, ".idp/outbox/a/manifest.json").ExitCode);
            Assert.Equal(0, repository.CheckIgnore(checkout, ".worktrees/r/t/file").ExitCode);
            Assert.Equal(1, repository.CheckIgnore(checkout, "src/a.txt").ExitCode);
            Assert.Equal(["src/a.txt"], Read(repository.TrackedFiles(checkout, "src")).ToArray());
        }
    }

    [Fact]
    public void Fixed_recipe_over_captured_tree_recreates_the_literal_commit()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        f.Write("a.txt", "A captured\n");
        f.Write("new.txt", "new\n");
        var repository = f.Open();
        var capture = Read(repository.Capture(f.Folder));
        Assert.Equal("c6809faa4972f0ec56cec88ce6a6b841c8694660", capture.Tree.Hex);
        var recipe = new CommitRecipe(capture.Tree, [a], "captured\n\nIDP-Run: fixture\n", "E2 <e2@example.test>", "E2 <e2@example.test>",
            DateTimeOffset.Parse("2026-10-07T00:00:00Z"));
        Assert.Equal("5135dc4ae170e2e50ce912b0adddc4abf43d2c6d", Read(repository.CreateCommit(recipe)).Hex);
        Assert.Equal("5135dc4ae170e2e50ce912b0adddc4abf43d2c6d", Read(repository.CreateCommit(recipe)).Hex);
        Assert.Equal("commit", Read(repository.ObjectType("5135dc4ae170e2e50ce912b0adddc4abf43d2c6d")));
        var commit = Read(repository.ReadCommit(new("5135dc4ae170e2e50ce912b0adddc4abf43d2c6d")));
        Assert.Equal("c6809faa4972f0ec56cec88ce6a6b841c8694660", commit.Tree.Hex);
        Assert.Equal(["adfe40b30c176fb407933286f51d15ea9b54cdc3"], commit.Parents.Select(parent => parent.Hex));
    }

    [Fact]
    public void Recipe_preserves_ordered_parents_and_distinct_identities_at_an_equivalent_instant()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        f.Write("a.txt", "A captured\n");
        f.Write("new.txt", "new\n");
        var recipe = new CommitRecipe(Read(repository.Capture(f.Folder)).Tree, [a, new("7c64b20d5be53b5c1a291863ef191aa28f6f4d51")],
            "ordered\n", "Writer <writer@example.test>", "Publisher <publisher@example.test>", DateTimeOffset.Parse("2026-10-07T02:00:00+02:00"));
        var commit = Read(repository.CreateCommit(recipe));
        Assert.Equal("88a42f9848acad2c7d4fb2d65ddc2c0102a16e70", commit.Hex);
        Assert.Equal(["adfe40b30c176fb407933286f51d15ea9b54cdc3", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51"],
            Read(repository.ReadCommit(commit)).Parents.Select(parent => parent.Hex));
    }

    [Fact]
    public void Ref_moves_adopt_the_target_and_report_an_observed_conflict()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        var change = new RefChange("refs/idp/r/base", null, a);
        Assert.IsType<RefMove.Moved>(repository.MoveRef(change));
        Assert.IsType<RefMove.AlreadyAtTarget>(repository.MoveRef(change));
        var conflict = Assert.IsType<RefMove.Conflict>(repository.MoveRef(new(change.Ref,
            new("7c64b20d5be53b5c1a291863ef191aa28f6f4d51"), new("81ddb7c330112c7f16700ed002803a04b0bce693"))));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", conflict.Observed?.Hex);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Read(repository.ReadRef(change.Ref))?.Hex);
        Assert.Null(Read(repository.ReadRef("refs/idp/missing/base")));
        var refs = Read(repository.RefSnapshot("refs/idp/", "refs/heads/idp/"));
        Assert.Equal(["refs/idp/r/base"], refs.Keys);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", refs["refs/idp/r/base"].Hex);
        Assert.Equal(["r"], RunLayout.UsedRunKeys(refs));
    }

    [Fact]
    public void Capture_includes_edits_deletions_and_nonignored_additions_without_changing_the_index()
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        File.AppendAllText(Path.Combine(repository.CommonDirectory, "info", "exclude"), "\nignored.txt\n");
        f.Write("a.txt", "changed\n");
        File.Delete(f.PathOf("plan.txt"));
        f.Write("untracked.txt", "new\n");
        f.Write("ignored.txt", "cache\n");
        f.Write(".idp/inputs/x/report.md", "execution\n");
        f.Write(".worktrees/nested/file", "nested\n");
        f.Write("extra/file", "excluded\n");
        var index = Read(repository.IndexPath(f.Folder));
        var before = File.ReadAllBytes(index);
        var capture = Read(repository.Capture(f.Folder, "extra"));
        Assert.Equal(before, File.ReadAllBytes(index));
        Assert.Equal(capture.IndexBefore, capture.IndexAfter);
        Assert.Equal(new Digest(Convert.ToHexStringLower(SHA256.HashData(before))), capture.IndexBefore);
        Assert.Equal("100644 blob 5ea2ed416fbd4a4cbe227b75fe255dd7fa6bd4d6\ta.txt\n" +
            "100644 blob d8649da39ddf7910d29982e2f19cd9c0ff5ffe96\troot.txt\n" +
            "100644 blob 3e757656cf36eca53338e520d134963a44f793f8\tuntracked.txt\n", f.Git("ls-tree", "-r", capture.Tree.Hex));
        Assert.Equal([".idp/inputs/x/report.md", ".worktrees/nested/file", "extra/file", "untracked.txt"], Read(repository.UntrackedFiles(f.Folder)).ToArray());
        Assert.Equal("cache\n", File.ReadAllText(f.PathOf("ignored.txt")));
    }

    [Fact]
    public void Alignment_consumes_staged_state_without_rewriting_working_files_and_adopts_on_retry()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        f.Write("a.txt", "A captured\n");
        f.Write("new.txt", "new\n");
        var capture = Read(repository.Capture(f.Folder));
        var recipe = new CommitRecipe(capture.Tree, [a], "captured\n\nIDP-Run: fixture\n", "E2 <e2@example.test>", "E2 <e2@example.test>",
            DateTimeOffset.Parse("2026-10-07T00:00:00Z"));
        var commit = Read(repository.CreateCommit(recipe));
        Assert.IsType<RefMove.Moved>(repository.MoveRef(new("refs/heads/main", a, commit)));
        Assert.IsType<IndexAlignment.Aligned>(repository.AlignIndex(f.Folder, capture.IndexBefore, capture.Tree));
        Assert.Equal("", Encoding.UTF8.GetString(Read(repository.Status(f.Folder))));
        Assert.Equal("A captured\n", File.ReadAllText(f.PathOf("a.txt")));
        Assert.Equal("new\n", File.ReadAllText(f.PathOf("new.txt")));
        Assert.IsType<IndexAlignment.AlreadyAligned>(repository.AlignIndex(f.Folder, capture.IndexBefore, capture.Tree));
    }

    [Fact]
    public void Unexpected_index_bytes_are_preserved_even_when_write_tree_would_cache_the_tree()
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        f.Write("a.txt", "changed\n");
        var capture = Read(repository.Capture(f.Folder));
        f.Write("a.txt", "staged afterward\n");
        f.Git("add", "a.txt");
        var index = Read(repository.IndexPath(f.Folder));
        var bytes = File.ReadAllBytes(index);
        var unexpected = Assert.IsType<IndexAlignment.Unexpected>(repository.AlignIndex(f.Folder, capture.IndexBefore, capture.Tree));
        Assert.Equal(new Digest(Convert.ToHexStringLower(SHA256.HashData(bytes))), unexpected.Observed);
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Equal("staged afterward\n", File.ReadAllText(f.PathOf("a.txt")));
        Assert.Equal("M  a.txt\0", Encoding.UTF8.GetString(Read(repository.Status(f.Folder))));
    }

    [Fact]
    public void Failed_worktree_creation_preserves_the_directory_and_allows_branch_adoption_elsewhere()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        f.Write("occupied/keep", "mine\n");
        Assert.NotEqual(0, repository.AddWorktree("occupied", "idp/r/task/t", a).ExitCode);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Read(repository.ReadRef("refs/heads/idp/r/task/t"))?.Hex);
        Assert.Equal(0, repository.AddWorktreeForExistingBranch(".worktrees/r/t", "idp/r/task/t").ExitCode);
        Assert.Equal("mine\n", File.ReadAllText(f.PathOf("occupied/keep")));
        var checkout = f.PathOf(".worktrees/r/t");
        Assert.Equal("refs/heads/idp/r/task/t", Read(repository.SymbolicHead(checkout)));
        Assert.Equal(0, repository.LockWorktree(".worktrees/r/t", "owner r/t").ExitCode);
        Assert.Equal(0, repository.LockWorktree(".worktrees/r/t", "owner r/t").ExitCode);
        Assert.NotEqual(0, repository.LockWorktree(".worktrees/r/t", "another owner").ExitCode);
        var worktrees = Read(repository.Worktrees());
        Assert.Equal(2, worktrees.Length);
        var linked = Assert.Single(worktrees, worktree => worktree.Branch == "refs/heads/idp/r/task/t");
        Assert.Equal(f.Run(checkout, "rev-parse", "--show-toplevel").Text.Trim(), linked.Path);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", linked.Head?.Hex);
        Assert.True(linked.Locked);
        Assert.Equal("owner r/t", linked.LockReason);
        Assert.Equal(0, repository.InitializeSubmodules(checkout).ExitCode);
        f.Write("a.txt", "linked capture\n", checkout);
        Assert.Equal("linked capture\n", f.Run(checkout, "show", Read(repository.Capture(checkout)).Tree.Hex + ":a.txt").Text);
    }

    [Fact]
    public void Revision_and_ancestry_reads_distinguish_absence_detachment_and_failure()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        var root = new CommitId("7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Read(repository.ResolveCommit("HEAD"))?.Hex);
        Assert.Null(Read(repository.ResolveCommit("missing")));
        Assert.Null(Read(repository.ResolveCommit("HEAD:root.txt")));
        Assert.IsType<GitAncestry.Yes>(repository.IsAncestor(root, a));
        Assert.IsType<GitAncestry.No>(repository.IsAncestor(a, root));
        Assert.IsType<GitAncestry.Failed>(repository.IsAncestor(new(new string('9', 40)), a));
        Assert.Equal("refs/heads/main", Read(repository.SymbolicHead(f.Folder)));
        f.Git("checkout", "--detach", "-q", a.Hex);
        Assert.Null(Read(repository.SymbolicHead(f.Folder)));
        Assert.Equal(["a.txt", "plan.txt", "root.txt"], Read(repository.TrackedFiles(f.Folder)).ToArray());
        Assert.Empty(Read(repository.UnmergedEntries(f.Folder)));
        var failed = Assert.IsType<GitRead<string>.Failed>(repository.ObjectType(new string('9', 40)));
        Assert.Equal(MaterializationProblem.GitFailed, failed.Problem);
        Assert.Contains("could not get object info", failed.Detail);
    }

    [Fact]
    public void Unmerged_entries_preserve_literal_stages()
    {
        using var f = new GitFixture();
        f.Diamond();
        f.Git("checkout", "-q", "-b", "other");
        f.Write("a.txt", "right\n");
        f.Commit("right");
        f.Git("checkout", "-q", "main");
        f.Write("a.txt", "left\n");
        f.Commit("left");
        Assert.Equal(1, f.Run(f.Folder, "merge", "--no-edit", "other").ExitCode);
        var repository = f.Open();
        Assert.Equal(new[]
        {
            new StageEntry("100644", "f70f10e4db19068f79bc43844b49f3eece45c4e8", 1, "a.txt"),
            new StageEntry("100644", "45cf141ba67d59203f02a54f03162f3fcef57830", 2, "a.txt"),
            new StageEntry("100644", "c376d892e8b105bd712d06ec5162b5f31ce949c3", 3, "a.txt"),
        }, Read(repository.UnmergedEntries(f.Folder)));
        Assert.Equal("UU a.txt\0", Encoding.UTF8.GetString(Read(repository.Status(f.Folder))));
    }

    [Fact]
    public void Invalid_index_blocks_capture_and_preserves_the_real_bytes()
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        var path = Read(repository.IndexPath(f.Folder));
        File.WriteAllBytes(path, [68, 73, 82, 67]);
        var failed = Assert.IsType<GitRead<GitCapture>.Failed>(repository.Capture(f.Folder));
        Assert.Equal(MaterializationProblem.GitFailed, failed.Problem);
        Assert.Contains("index file smaller than expected", failed.Detail);
        Assert.Equal(new byte[] { 68, 73, 82, 67 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Failed_ref_write_with_an_unchanged_expected_ref_is_an_operational_failure()
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        var failed = Assert.IsType<RefMove.Failed>(repository.MoveRef(new("refs/idp/r/base", null, new(new string('9', 40)))));
        Assert.Contains("nonexistent object", failed.Detail);
        Assert.IsType<RefMove.Moved>(repository.MoveRef(new("refs/idp/r/base", null, new("adfe40b30c176fb407933286f51d15ea9b54cdc3"))));
    }

    [Fact]
    public void An_absent_index_has_no_digest_and_capture_leaves_it_absent()
    {
        using var f = new GitFixture();
        var repository = f.Open();
        f.Write("root.txt", "root\n");
        Assert.Null(Read(repository.IndexDigest(f.Folder)));
        var capture = Read(repository.Capture(f.Folder));
        Assert.Null(capture.IndexBefore);
        Assert.Null(capture.IndexAfter);
        Assert.Equal("root\n", f.Git("show", capture.Tree.Hex + ":root.txt"));
        Assert.False(File.Exists(Read(repository.IndexPath(f.Folder))));
        File.WriteAllBytes(Read(repository.IndexPath(f.Folder)), []);
        Assert.Equal(new Digest("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), Read(repository.IndexDigest(f.Folder)));
    }

    [Fact]
    public void Mutation_lock_is_exclusive_across_linked_checkouts_and_is_never_deleted()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        Assert.Equal(0, repository.AddWorktree(".worktrees/r/t", "idp/r/task/t", a).ExitCode);
        var linked = f.Open(f.PathOf(".worktrees/r/t"));
        using (var held = repository.TakeMutationLock())
        {
            Assert.NotNull(held);
            Assert.Null(linked.TakeMutationLock());
        }
        using var taken = linked.TakeMutationLock();
        Assert.NotNull(taken);
        Assert.True(File.Exists(Path.Combine(repository.CommonDirectory, "idevelop", "mutation.lock")));
    }

    [UnixFact]
    public void Metadata_times_out_while_a_worktree_scan_uses_its_longer_limit()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new GitFixture();
        f.Diamond();
        var realGit = CommandResolver.Create((System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;
        var bin = Directory.CreateDirectory(Path.Combine(f.Open().CommonDirectory, "test-bin")).FullName;
        var environment = new Dictionary<string, string>(f.Environment)
        {
            ["PATH"] = bin + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH"),
        };
        var limits = new GitLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var repository = Assert.IsType<RepositoryOpen.Opened>(GitRepository.Open(f.Folder, environment, limits)).Repository;
        var shim = Path.Combine(bin, "git");
        File.WriteAllText(shim, "#!/bin/sh\nsleep 3\nexec '" + realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var failed = Assert.IsType<GitRead<CommitId?>.Failed>(repository.ReadRef("refs/heads/main"));
        Assert.Equal("GitFailed", failed.Problem.ToString());
        Assert.Equal("Git timed out.", failed.Detail);
        f.Write("new.txt", "new\n");
        Assert.Equal("?? new.txt\0", Encoding.UTF8.GetString(Read(repository.Status(f.Folder))));
    }

    [UnixFact]
    public void Removal_preserves_symlink_targets()
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        f.Write("target/file", "unfinished\n");
        File.CreateSymbolicLink(f.PathOf("link"), f.PathOf("target/file"));
        Directory.CreateSymbolicLink(f.PathOf("via"), f.PathOf("target"));
        var content = new Digest("be02c0270dc16cf866391d81369ce9b50c9bd1c9cb0834a5c2788c70b35ead2e");
        Assert.Equal(PathRemoval.Unexpected, repository.RemovePath(f.Folder, "link", content, GitPathType.RegularFile));
        Assert.Equal(PathRemoval.Unexpected, repository.RemovePath(f.Folder, "via/file", content, GitPathType.RegularFile));
        Assert.Equal("unfinished\n", File.ReadAllText(f.PathOf("target/file")));
    }

    [Fact]
    public void Removal_checks_type_and_bytes_and_reset_leaves_uncaptured_paths_in_place()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        f.Write("new.txt", "unfinished\n");
        var digest = new Digest("be02c0270dc16cf866391d81369ce9b50c9bd1c9cb0834a5c2788c70b35ead2e");
        Assert.Equal(PathRemoval.Unexpected, repository.RemovePath(f.Folder, "new.txt", digest, GitPathType.Directory));
        Assert.Equal(PathRemoval.Unexpected, repository.RemovePath(f.Folder, "new.txt", new(new string('0', 64)), GitPathType.RegularFile));
        Assert.Equal("unfinished\n", File.ReadAllText(f.PathOf("new.txt")));
        Assert.Equal(PathRemoval.Removed, repository.RemovePath(f.Folder, "new.txt", digest, GitPathType.RegularFile));
        Assert.Equal(PathRemoval.Absent, repository.RemovePath(f.Folder, "new.txt", digest, GitPathType.RegularFile));
        f.Write("folder/keep", "mine\n");
        Assert.Equal(PathRemoval.Unexpected, repository.RemovePath(f.Folder, "folder", digest, GitPathType.RegularFile));
        Assert.Equal(PathRemoval.Unexpected, repository.RemovePath(f.Folder, "../outside", digest, GitPathType.RegularFile));
        Assert.Equal("mine\n", File.ReadAllText(f.PathOf("folder/keep")));
        f.Write("a.txt", "changed\n");
        var salvage = Read(repository.Capture(f.Folder));
        Assert.Equal("changed\n", f.Git("show", salvage.Tree.Hex + ":a.txt"));
        Assert.Equal("78471d56e16a5aeb662aff8461f6882fb3c4c76d", salvage.Tree.Hex);
        var saved = Read(repository.CreateCommit(new(salvage.Tree, [a], "salvage\n", "E2 <e2@example.test>", "E2 <e2@example.test>",
            DateTimeOffset.Parse("2026-10-07T00:00:00Z"))));
        Assert.Equal("3e82c1ec96d334dc6e07fcb174b64369c5175e8b", saved.Hex);
        Assert.IsType<RefMove.Moved>(repository.MoveRef(new("refs/idp/r/salvage/t/019a9d2e-0000-7000-8000-000000000001", null, saved)));
        Assert.Equal("3e82c1ec96d334dc6e07fcb174b64369c5175e8b", Read(repository.ReadRef("refs/idp/r/salvage/t/019a9d2e-0000-7000-8000-000000000001"))?.Hex);
        Assert.Equal(0, repository.ResetCheckout(f.Folder, a).ExitCode);
        Assert.Equal("A\n", File.ReadAllText(f.PathOf("a.txt")));
        Assert.Equal("mine\n", File.ReadAllText(f.PathOf("folder/keep")));
    }
}
