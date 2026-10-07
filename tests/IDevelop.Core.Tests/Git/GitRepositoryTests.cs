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
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public void Unchanged_flagged_files_allow_capture_and_preserve_the_index(string flag)
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        f.Git("update-index", flag, "a.txt", "plan.txt");
        var repository = f.Open();
        var index = Read(repository.IndexPath(f.Folder));
        var before = File.ReadAllBytes(index);
        var capture = Read(repository.Capture(f.Folder));
        Assert.Equal(Read(repository.ReadCommit(a)).Tree.Hex, capture.Tree.Hex);
        Assert.Equal("A\n", f.Git("show", capture.Tree.Hex + ":a.txt"));
        Assert.Equal("approved\n", f.Git("show", capture.Tree.Hex + ":plan.txt"));
        Assert.Equal("", Encoding.UTF8.GetString(Read(repository.Status(f.Folder))));
        Assert.Equal(before, File.ReadAllBytes(index));
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    [InlineData("both")]
    public void Absent_flagged_files_allow_only_skip_worktree_capture(string flag)
    {
        using var f = new GitFixture();
        f.Diamond();
        if (flag == "both")
        {
            f.Git("update-index", "--assume-unchanged", "a.txt");
            f.Git("update-index", "--skip-worktree", "a.txt");
        }
        else f.Git("update-index", flag, "a.txt");
        File.Delete(f.PathOf("a.txt"));
        var repository = f.Open();
        var capture = repository.Capture(f.Folder);
        if (flag == "--skip-worktree")
        {
            var captured = Read(capture);
            Assert.Equal("A\n", f.Git("show", captured.Tree.Hex + ":a.txt"));
            Assert.Equal("", Encoding.UTF8.GetString(Read(repository.Status(f.Folder))));
        }
        else
        {
            var blocked = Assert.IsType<GitRead<GitCapture>.Failed>(capture);
            Assert.Equal("DirtyWorktree", blocked.Problem.ToString());
            Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", blocked.Detail);
        }
        Assert.False(File.Exists(f.PathOf("a.txt")));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Read(repository.ReadRef("refs/heads/main"))?.Hex);
    }

    [Fact]
    public void Flagged_blob_comparisons_apply_clean_filters_to_each_quoted_path()
    {
        using var f = new GitFixture();
        f.Diamond();
        f.Write(".gitattributes", "*.txt text\n");
        f.Write("clean one.txt", "one\n");
        f.Write("clean two.txt", "two\n");
        var commit = f.Commit("filtered");
        f.Git("update-index", "--assume-unchanged", "clean one.txt");
        f.Git("update-index", "--skip-worktree", "clean two.txt");
        f.Write("clean one.txt", "one\r\n");
        f.Write("clean two.txt", "two\r\n");
        var repository = f.Open();
        var index = Read(repository.IndexPath(f.Folder));
        var before = File.ReadAllBytes(index);
        var captured = Read(repository.Capture(f.Folder));
        Assert.Equal(Read(repository.ReadCommit(commit)).Tree, captured.Tree);
        Assert.Equal("one\n", f.Git("show", captured.Tree.Hex + ":clean one.txt"));
        Assert.Equal("two\n", f.Git("show", captured.Tree.Hex + ":clean two.txt"));
        Assert.Equal("one\r\n", File.ReadAllText(f.PathOf("clean one.txt")));
        Assert.Equal("two\r\n", File.ReadAllText(f.PathOf("clean two.txt")));
        Assert.Equal(before, File.ReadAllBytes(index));
    }

    [UnixFact]
    public void Flagged_executable_mode_changes_block_capture_with_the_existing_detail()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new GitFixture();
        f.Diamond();
        f.Git("update-index", "--assume-unchanged", "a.txt");
        File.SetUnixFileMode(f.PathOf("a.txt"), File.GetUnixFileMode(f.PathOf("a.txt")) | UnixFileMode.UserExecute);
        var blocked = Assert.IsType<GitRead<GitCapture>.Failed>(f.Open().Capture(f.Folder));
        Assert.Equal("DirtyWorktree", blocked.Problem.ToString());
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", blocked.Detail);
        Assert.Equal("A\n", File.ReadAllText(f.PathOf("a.txt")));
        Assert.Equal("100644 f70f10e4db19068f79bc43844b49f3eece45c4e8 0\ta.txt\n", f.Git("ls-files", "--stage", "a.txt"));
    }

    [UnixFact]
    public void Flagged_nonregular_paths_block_capture_with_the_existing_detail()
    {
        using var f = new GitFixture();
        f.Diamond();
        f.Git("update-index", "--skip-worktree", "a.txt");
        f.Write("target.txt", "A\n");
        File.Delete(f.PathOf("a.txt"));
        File.CreateSymbolicLink(f.PathOf("a.txt"), f.PathOf("target.txt"));
        var blocked = Assert.IsType<GitRead<GitCapture>.Failed>(f.Open().Capture(f.Folder));
        Assert.Equal("DirtyWorktree", blocked.Problem.ToString());
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", blocked.Detail);
        Assert.Equal("A\n", File.ReadAllText(f.PathOf("target.txt")));
    }

    [Fact]
    public void Symbolic_refs_are_refused_by_reads_and_snapshots()
    {
        using var f = new GitFixture();
        f.Diamond();
        f.Git("symbolic-ref", "refs/idp/r/base", "refs/heads/main");
        var repository = f.Open();
        var read = Assert.IsType<GitRead<CommitId?>.Failed>(repository.ReadRef("refs/idp/r/base"));
        Assert.Equal("UncertainOwnership", read.Problem.ToString());
        Assert.Equal("Ref refs/idp/r/base is symbolic to refs/heads/main.", read.Detail);
        var snapshot = Assert.IsType<GitRead<SortedDictionary<string, CommitId>>.Failed>(repository.RefSnapshot("refs/idp/"));
        Assert.Equal("UncertainOwnership", snapshot.Problem.ToString());
        Assert.Equal("Ref refs/idp/r/base is symbolic to refs/heads/main.", snapshot.Detail);
        Assert.Equal("refs/heads/main\n", f.Git("symbolic-ref", "refs/idp/r/base"));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3\n", f.Git("rev-parse", "refs/heads/main"));
    }

    [Fact]
    public void Ref_move_replaces_a_symbolic_ref_without_moving_its_foreign_target()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        f.Git("symbolic-ref", "refs/idp/r/base", "refs/heads/main");
        var repository = f.Open();
        Assert.IsType<RefMove.Moved>(repository.MoveRef(new("refs/idp/r/base", a, new("81ddb7c330112c7f16700ed002803a04b0bce693"))));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3\n", f.Git("rev-parse", "refs/heads/main"));
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", Read(repository.ReadRef("refs/idp/r/base"))?.Hex);
        Assert.Equal(1, f.Run(f.Folder, "symbolic-ref", "--quiet", "refs/idp/r/base").ExitCode);
    }

    [Theory]
    [InlineData("--assume-unchanged", "e8b087a53aed4161652f8e8f338181c127b2877911bf0f8b49f433f5c54b7a41")]
    [InlineData("--skip-worktree", "b92c6ccd0a05dfa8b133e036dbfa9d94d60f448ad45ca8062d92ce43fce4a359")]
    public void Index_flags_change_the_digest_and_block_capture_and_status(string flag, string digest)
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        f.Git("update-index", flag, "a.txt");
        Assert.Equal(digest, Read(repository.IndexDigest(f.Folder))?.Sha256);
        f.Write("a.txt", "hidden edit\n");
        var index = Read(repository.IndexPath(f.Folder));
        var bytes = File.ReadAllBytes(index);
        var capture = Assert.IsType<GitRead<GitCapture>.Failed>(repository.Capture(f.Folder));
        Assert.Equal("DirtyWorktree", capture.Problem.ToString());
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", capture.Detail);
        var status = Assert.IsType<GitRead<byte[]>.Failed>(repository.Status(f.Folder));
        Assert.Equal("DirtyWorktree", status.Problem.ToString());
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", status.Detail);
        Assert.Equal("hidden edit\n", File.ReadAllText(f.PathOf("a.txt")));
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", Read(repository.ReadRef("refs/heads/main"))?.Hex);
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public void Index_flags_after_capture_block_alignment_without_consuming_index_bytes(string flag)
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        f.Write("a.txt", "captured edit\n");
        var capture = Read(repository.Capture(f.Folder));
        f.Git("update-index", flag, "a.txt");
        var index = Read(repository.IndexPath(f.Folder));
        var bytes = File.ReadAllBytes(index);
        var aligned = Assert.IsType<IndexAlignment.Failed>(repository.AlignIndex(f.Folder, capture.IndexBefore, capture.Tree));
        Assert.Equal("The index hides changes to a.txt with assume-unchanged or skip-worktree.", aligned.Detail);
        Assert.Equal(bytes, File.ReadAllBytes(index));
        Assert.Equal("captured edit\n", File.ReadAllText(f.PathOf("a.txt")));
        Assert.Equal("A\n", f.Git("show", ":a.txt"));
    }

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
        Assert.Equal(new Digest("cf3a398a67ea241355d092c55c321d9463d19d14cc11595a01cd02a70c0f3f74"), capture.IndexBefore);
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
        Assert.Equal(new Digest("a7fe487cd90c6e3ebac781ec7c52916395e42706caf40b553aacc6496a528a2d"), unexpected.Observed);
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
    public void Ancestry_uses_real_parents_even_when_a_graft_claims_an_unrelated_base()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var below = new CommitId("81ddb7c330112c7f16700ed002803a04b0bce693");
        f.Git("checkout", "-q", "--detach", below.Hex);
        f.Write("b.txt", "B\n");
        var tip = f.Commit("b.txt");
        Assert.Equal("40abc7bebc8957e22d11b4c6b9603180652d95f1", tip.Hex);
        var repository = f.Open();
        Assert.Equal(new GitAncestry.No(), repository.IsAncestor(a, tip));
        Assert.Equal(new GitAncestry.Yes(), repository.IsAncestor(below, tip));
        File.WriteAllText(Path.Combine(repository.CommonDirectory, "info", "grafts"), tip.Hex + " " + a.Hex + "\n");
        Assert.Equal(new GitAncestry.No(), repository.IsAncestor(a, tip));
        Assert.Equal(new GitAncestry.Yes(), repository.IsAncestor(below, tip));
        Assert.Equal(new[] { "81ddb7c330112c7f16700ed002803a04b0bce693" }, Read(repository.ReadCommit(tip)).Parents.Select(parent => parent.Hex));
        var failed = Assert.IsType<GitAncestry.Failed>(repository.IsAncestor(new(new string('9', 40)), tip));
        Assert.Equal("fatal: Not a valid commit name 9999999999999999999999999999999999999999\n", failed.Detail);
    }

    [Fact]
    public void Git_tree_reads_real_commit_content_despite_replacement()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        f.Git("replace", a.Hex, "81ddb7c330112c7f16700ed002803a04b0bce693");
        Assert.Equal("100644 blob 367ff430540e7bc5c23d699de65ee5af6706b274\tplan.txt\0" +
            "100644 blob d8649da39ddf7910d29982e2f19cd9c0ff5ffe96\troot.txt\0" +
            "100644 blob f70f10e4db19068f79bc43844b49f3eece45c4e8\ta.txt", GitTree.ContentOutsideData(f.Folder, a.Hex));
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
        f.Git("read-tree", "--empty");
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
    public async Task Removal_rejects_a_fifo_without_opening_or_deleting_it()
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        var fifo = f.PathOf("pipe");
        Assert.Equal(0, Mkfifo(fifo, 0x180));
        var removal = Task.Run(() => repository.RemovePath(f.Folder, "pipe",
            new("be02c0270dc16cf866391d81369ce9b50c9bd1c9cb0834a5c2788c70b35ead2e"), GitPathType.RegularFile));
        try
        {
            Assert.Equal(PathRemoval.Unexpected, await removal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(File.Exists(fifo));
        }
        finally
        {
            var writer = Open(fifo, 1 | (OperatingSystem.IsMacOS() ? 4 : 0x800));
            if (writer >= 0) Close(writer);
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

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

    [Fact]
    public void Index_digest_ignores_stat_refresh_and_capture_preserves_real_index_bytes()
    {
        using var f = new GitFixture();
        f.Diamond();
        var repository = f.Open();
        var index = Read(repository.IndexPath(f.Folder));
        Assert.Equal("cf3a398a67ea241355d092c55c321d9463d19d14cc11595a01cd02a70c0f3f74", Read(repository.IndexDigest(f.Folder))?.Sha256);
        var before = File.ReadAllBytes(index);
        File.SetLastWriteTimeUtc(f.PathOf("plan.txt"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("", f.Git("--no-optional-locks", "status", "--porcelain"));
        f.Git("update-index", "--refresh");
        Assert.False(before.SequenceEqual(File.ReadAllBytes(index)));
        Assert.Equal("cf3a398a67ea241355d092c55c321d9463d19d14cc11595a01cd02a70c0f3f74", Read(repository.IndexDigest(f.Folder))?.Sha256);
        var refreshed = File.ReadAllBytes(index);
        f.Write("new.txt", "new\n");
        var captured = Read(repository.Capture(f.Folder));
        Assert.Equal("cf3a398a67ea241355d092c55c321d9463d19d14cc11595a01cd02a70c0f3f74", captured.IndexBefore?.Sha256);
        Assert.Equal("cf3a398a67ea241355d092c55c321d9463d19d14cc11595a01cd02a70c0f3f74", captured.IndexAfter?.Sha256);
        Assert.Equal(refreshed, File.ReadAllBytes(index));
        Assert.Equal("new\n", f.Git("show", captured.Tree.Hex + ":new.txt"));
        Assert.Equal("A\n", File.ReadAllText(f.PathOf("a.txt")));
    }
}
