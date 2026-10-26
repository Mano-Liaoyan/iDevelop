using System.Globalization;
using System.Text;
using IDevelop.Clients;
using IDevelop.Git;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Git.GitFixture;

namespace IDevelop.Core.Tests.Git;

public sealed class GitRepositoryTests
{
    [Fact]
    public void Open_finds_the_toplevel_and_discovers_linked_worktree_identity()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        Assert.Equal(f.Git("rev-parse", "--show-toplevel").Trim(), repository.ProjectFolder);
        Assert.Equal(repository.Root, repository.ProjectFolder);
        Assert.Equal("", repository.Prefix);
        Assert.Equal(f.Git("rev-parse", "--path-format=absolute", "--git-common-dir").Trim(), repository.CommonDirectory);
        Assert.Equal(0, repository.AddWorktree(".worktrees/r/t", "idp/r/task/t", a).ExitCode);
        var linked = f.Open(f.PathOf(".worktrees/r/t"));
        Assert.Equal(repository.CommonDirectory, linked.CommonDirectory);
        Assert.Equal(f.Run(f.PathOf(".worktrees/r/t"), "rev-parse", "--show-toplevel").Text.Trim(), linked.ProjectFolder);
        Assert.True(File.Exists(f.PathOf(".worktrees/r/t/.git")));
    }

    [Fact]
    public void A_folder_below_the_toplevel_opens_with_its_prefix_and_keeps_its_worktrees_in_itself()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var project = Directory.CreateDirectory(f.PathOf("services/api")).FullName;
        var repository = f.Open(project);
        Assert.Equal(f.Git("rev-parse", "--show-toplevel").Trim(), repository.Root);
        Assert.Equal("services/api/", repository.Prefix);
        Assert.Equal(project, repository.ProjectFolder);

        Assert.Equal(0, repository.AddWorktree(".worktrees/r/t", "idp/r/task/t", a).ExitCode);
        var checkout = Path.Combine(project, ".worktrees", "r", "t");
        Assert.True(File.Exists(Path.Combine(checkout, "a.txt")));
        Assert.Contains(Read(repository.Worktrees()), worktree => Path.GetFullPath(worktree) == checkout);
        Assert.Equal(Path.Combine(checkout, "services", "api"), repository.ProjectIn(checkout));

        repository.EnsureExcluded();
        Assert.Equal(0, f.Run(project, "check-ignore", "-q", "--", ".worktrees/").ExitCode);
        Assert.NotEqual(0, f.Run(f.Folder, "check-ignore", "-q", "--", ".worktrees/").ExitCode);
    }

    [Fact]
    public void Open_refuses_a_nonrepository()
    {
        using var f = new GitFixture(initialize: false);
        var refused = Assert.IsType<RepositoryOpen.Refused>(GitRepository.Open(f.Folder, f.Environment));
        Assert.True(refused.InNoWorkTree);
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
        File.WriteAllBytes(exclude, "keep-me"u8);
        repository.EnsureExcluded();
        var bytes = File.ReadAllBytes(exclude);
        Assert.Equal("keep-me\n/.worktrees/\n", Encoding.UTF8.GetString(bytes));
        repository.EnsureExcluded();
        Assert.Equal(bytes, File.ReadAllBytes(exclude));
        Assert.Equal(0, repository.AddWorktree(".worktrees/r/t", "idp/r/task/t", a).ExitCode);
        foreach (var checkout in new[] { f.Folder, f.PathOf(".worktrees/r/t") })
        {
            Assert.Equal(0, f.Run(checkout, "check-ignore", "-q", "--", ".worktrees/r/t/file").ExitCode);
            Assert.Equal(1, f.Run(checkout, "check-ignore", "-q", "--", "src/a.txt").ExitCode);
            Assert.Equal("src/a.txt", f.Run(checkout, "ls-files", "--", "src").Text.Trim());
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
        Assert.Equal("c6809faa4972f0ec56cec88ce6a6b841c8694660", capture.Hex);
        var recipe = new CommitRecipe(capture, [a], "captured\n\nIDP-Run: fixture\n", "E2 <e2@example.test>", "E2 <e2@example.test>",
            DateTimeOffset.Parse("2026-10-07T00:00:00Z"));
        Assert.Equal("5135dc4ae170e2e50ce912b0adddc4abf43d2c6d", Read(repository.CreateCommit(recipe)).Hex);
        Assert.Equal("5135dc4ae170e2e50ce912b0adddc4abf43d2c6d", Read(repository.CreateCommit(recipe)).Hex);
        Assert.Equal("commit", f.Git("cat-file", "-t", "5135dc4ae170e2e50ce912b0adddc4abf43d2c6d").Trim());
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
        var recipe = new CommitRecipe(Read(repository.Capture(f.Folder)), [a, new("7c64b20d5be53b5c1a291863ef191aa28f6f4d51")],
            "ordered\n", "Writer <writer@example.test>", "Publisher <publisher@example.test>", DateTimeOffset.Parse("2026-10-07T02:00:00+02:00"));
        var commit = Read(repository.CreateCommit(recipe));
        Assert.Equal("88a42f9848acad2c7d4fb2d65ddc2c0102a16e70", commit.Hex);
        Assert.Equal(["adfe40b30c176fb407933286f51d15ea9b54cdc3", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51"],
            Read(repository.ReadCommit(commit)).Parents.Select(parent => parent.Hex));
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
        var index = f.Git("rev-parse", "--path-format=absolute", "--git-path", "index").Trim();
        var before = File.ReadAllBytes(index);
        var capture = Read(repository.Capture(f.Folder));
        Assert.Equal(before, File.ReadAllBytes(index));
        Assert.Equal("100644 blob 5ea2ed416fbd4a4cbe227b75fe255dd7fa6bd4d6\ta.txt\n" +
            "100644 blob d8649da39ddf7910d29982e2f19cd9c0ff5ffe96\troot.txt\n" +
            "100644 blob 3e757656cf36eca53338e520d134963a44f793f8\tuntracked.txt\n", f.Git("ls-tree", "-r", capture.Hex));
        Assert.Equal("nested\n", File.ReadAllText(f.PathOf(".worktrees/nested/file")));
        Assert.Equal("cache\n", File.ReadAllText(f.PathOf("ignored.txt")));
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
        Assert.Equal(["81ddb7c330112c7f16700ed002803a04b0bce693"], Read(repository.ReadCommit(tip)).Parents.Select(parent => parent.Hex));
        var failed = Assert.IsType<GitAncestry.Failed>(repository.IsAncestor(new(new string('9', 40)), tip));
        Assert.Equal("fatal: Not a valid commit name 9999999999999999999999999999999999999999\n", failed.Detail);
    }

    [UnixFact]
    public void A_timed_out_call_returns_and_stops_feeding_a_child_that_outlives_Git_and_keeps_its_output()
    {
        using var f = new GitFixture();
        f.Diamond();
        var (bin, environment) = ShimBin(f);
        var child = Path.Combine(bin, "child");
        Executable.Write(Path.Combine(bin, "git"), $"#!/bin/sh\n(while echo tick; do sleep 0.1; done) &\necho $! > {Quote(child)}\n");
        var limits = new GitLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = GitRepository.Run(["status"], f.Folder, GitOperation.Metadata, limits, environment);
        elapsed.Stop();
        var id = int.Parse(File.ReadAllText(child), CultureInfo.InvariantCulture);
        try
        {
            Assert.Equal((-1, "Git timed out."), (result.ExitCode, result.Stderr));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"The call took {elapsed.Elapsed}.");
            Assert.True(Exits(id, TimeSpan.FromSeconds(5)), "The child that outlived Git still writes into a pipe that iDevelop keeps open.");
        }
        finally
        {
            Kill(id);
        }
    }

    [UnixFact]
    public void A_timed_out_call_stops_a_hook_child_that_outlived_its_parent_and_never_writes()
    {
        using var f = new GitFixture();
        var (bin, environment) = ShimBin(f);
        var child = Path.Combine(bin, "child");
        Executable.Write(Path.Combine(bin, "git"), $"#!/bin/sh\n(sleep 300 & echo $! > {Quote(child)})\nexec sleep 60\n");
        var limits = new GitLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = GitRepository.Run(["status"], f.Folder, GitOperation.Metadata, limits, environment);
        elapsed.Stop();
        var id = int.Parse(File.ReadAllText(child), CultureInfo.InvariantCulture);
        try
        {
            Assert.Equal((-1, "Git timed out."), (result.ExitCode, result.Stderr));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"The call took {elapsed.Elapsed}.");
            Assert.True(Exits(id, TimeSpan.FromSeconds(5)), "The hook's child outlived the timed-out Git call.");
        }
        finally
        {
            Kill(id);
        }
    }

    [UnixFact]
    public void A_call_refuses_a_git_that_PATH_does_not_name_instead_of_searching_elsewhere()
    {
        using var f = new GitFixture();
        var empty = Path.Combine(Path.GetDirectoryName(f.Folder)!, "empty-bin");
        Directory.CreateDirectory(empty);
        var environment = new Dictionary<string, string>(f.Environment) { ["PATH"] = empty };

        var result = GitRepository.Run(["version"], f.Folder, GitOperation.Metadata, GitLimits.Default, environment);

        Assert.Equal((-1, "", "git was not found on PATH."), (result.ExitCode, result.Text, result.Stderr));
        Assert.Equal("git was not found on PATH.", Assert.IsType<RepositoryOpen.Refused>(GitRepository.Open(f.Folder, environment)).Detail);
    }

    [UnixFact]
    public void A_call_that_ends_on_its_own_leaves_a_hook_child_running()
    {
        using var f = new GitFixture();
        var (bin, environment) = ShimBin(f);
        var child = Path.Combine(bin, "child");
        Executable.Write(Path.Combine(bin, "git"),
            $"#!/bin/sh\n(sleep 300 >/dev/null 2>&1 </dev/null & echo $! > {Quote(child)})\nexec {Quote(RealGit)} \"$@\"\n");
        var result = GitRepository.Run(["rev-parse", "--is-inside-work-tree"], f.Folder, GitOperation.Metadata, GitLimits.Default, environment);
        var id = int.Parse(File.ReadAllText(child), CultureInfo.InvariantCulture);
        try
        {
            Assert.Equal((0, "true\n"), (result.ExitCode, result.Text));
            Assert.False(Exits(id, TimeSpan.FromMilliseconds(500)), "A call that ended on its own stopped what its hook started.");
        }
        finally
        {
            Kill(id);
        }
    }

    [UnixFact]
    public void A_Git_that_exits_without_reading_its_input_keeps_its_error()
    {
        using var f = new GitFixture();
        var (bin, environment) = ShimBin(f);
        Executable.Write(Path.Combine(bin, "git"), "#!/bin/sh\necho 'fatal: boom' >&2\nexit 128\n");
        var limits = new GitLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var result = GitRepository.Run(["update-index"], f.Folder, GitOperation.Worktree, limits, environment, new byte[8 << 20]);
        Assert.Equal((-1, "fatal: boom\nBroken pipe"), (result.ExitCode, result.Stderr));
    }

    [UnixFact]
    public void A_timed_out_call_that_Git_never_read_input_for_says_it_timed_out()
    {
        using var f = new GitFixture();
        var (bin, environment) = ShimBin(f);
        Executable.Write(Path.Combine(bin, "git"), "#!/bin/sh\nexec sleep 60\n");
        var limits = new GitLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var result = GitRepository.Run(["update-index"], f.Folder, GitOperation.Worktree, limits, environment, new byte[8 << 20]);
        Assert.Equal((-1, "Git timed out."), (result.ExitCode, result.Stderr));
    }

    [UnixFact]
    public void A_timed_out_call_keeps_what_Git_wrote_once_a_process_outside_its_tree_lets_go_of_the_pipes()
    {
        using var f = new GitFixture();
        var (bin, environment) = ShimBin(f);
        Executable.Write(Path.Combine(bin, "git"), "#!/bin/sh\necho 'waiting for the lock' >&2\n(sleep 2 &)\nexec sleep 60\n");
        var limits = new GitLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var result = GitRepository.Run(["status"], f.Folder, GitOperation.Metadata, limits, environment);
        Assert.Equal((-1, "waiting for the lock\nGit timed out."), (result.ExitCode, result.Stderr));
    }

    [UnixFact]
    public void Capture_returns_its_tree_when_the_temporary_index_lock_cannot_be_deleted()
    {
        using var f = new GitFixture();
        f.Diamond();
        f.Write("a.txt", "C\n");
        var (bin, environment) = ShimBin(f);
        var lockPath = Path.Combine(bin, "lock");
        Executable.Write(Path.Combine(bin, "git"),
            $"#!/bin/sh\n{Quote(RealGit)} \"$@\"\nstatus=$?\ncase \" $* \" in *\" write-tree \"*) mkdir \"$GIT_INDEX_FILE.lock\" && printf %s \"$GIT_INDEX_FILE.lock\" > {Quote(lockPath)};; esac\nexit $status\n");
        var repository = Assert.IsType<RepositoryOpen.Opened>(GitRepository.Open(f.Folder, environment)).Repository;
        try
        {
            var capture = Read(repository.Capture(f.Folder));
            Assert.Equal("C\n", f.Git("show", capture.Hex + ":a.txt"));
        }
        finally
        {
            if (File.Exists(lockPath)) Directory.Delete(File.ReadAllText(lockPath));
        }
    }

    private static string RealGit => CommandResolver.Create((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;

    private static (string Bin, Dictionary<string, string> Environment) ShimBin(GitFixture f)
    {
        var bin = Directory.CreateDirectory(Path.Combine(f.Open().CommonDirectory, "test-bin")).FullName;
        return (bin, new Dictionary<string, string>(f.Environment)
        {
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        });
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool Exits(int id, TimeSpan patience)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            return process.WaitForExit(patience);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void Kill(int id)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            process.Kill();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
    }

    [Fact]
    public void Capture_takes_the_work_tree_and_a_reset_leaves_untracked_paths()
    {
        using var f = new GitFixture();
        var a = f.Diamond();
        var repository = f.Open();
        f.Write("folder/keep", "mine\n");
        f.Write("a.txt", "changed\n");
        var capture = Read(repository.Capture(f.Folder));
        Assert.Equal("changed\n", f.Git("show", capture.Hex + ":a.txt"));
        Assert.Equal("78471d56e16a5aeb662aff8461f6882fb3c4c76d", capture.Hex);
        var saved = Read(repository.CreateCommit(new(capture, [a], "salvage\n", "E2 <e2@example.test>", "E2 <e2@example.test>",
            DateTimeOffset.Parse("2026-10-07T00:00:00Z"))));
        Assert.Equal("3e82c1ec96d334dc6e07fcb174b64369c5175e8b", saved.Hex);
        Assert.Equal(0, repository.ResetCheckout(f.Folder, a).ExitCode);
        Assert.Equal("A\n", File.ReadAllText(f.PathOf("a.txt")));
        Assert.Equal("mine\n", File.ReadAllText(f.PathOf("folder/keep")));
    }

}
