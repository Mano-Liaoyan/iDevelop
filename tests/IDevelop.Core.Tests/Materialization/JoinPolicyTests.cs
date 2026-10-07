using System.Text;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class JoinPolicyTests
{
    private const string JoinRef = "refs/heads/idp/93f23689/join/c67f2fc3";
    private const string Renamed = "changed 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\n";

    private static Workflow Diamond() => Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U)), T, U), C, U);

    private static Materializer Joins(PreparationFixture f, Action<string>? probe = null, IReadOnlyDictionary<string, string>? environment = null) =>
        MergeJoins.Open(f.Git.Folder, f.Store, new QuiescentBoundary(), new Clock(), environment ?? f.Git.Environment, probe);

    private static ValueTask<Preparation> Prepare(PreparationFixture f, OperationId operation, Action<string>? probe = null) =>
        Joins(f, probe).Prepare(W, f.RunId, operation, U, new AttemptCause.Initial());

    private static void Commit(PreparationFixture f, string checkout, params (string Path, string? Text)[] edits)
    {
        foreach (var (path, text) in edits)
        {
            if (text is null) Assert.Equal(0, f.Git.Run(checkout, "rm", "-q", path).ExitCode);
            else f.Git.Write(path, text, checkout);
        }
        Assert.Equal(0, f.Git.Run(checkout, "add", "--all").ExitCode);
        Assert.Equal(0, f.Git.Run(checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", edits[0].Path).ExitCode);
    }

    private static async Task Write(PreparationFixture f, TaskId task, params (string Path, string? Text)[] edits)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(task));
        Commit(f, ready.Checkout, edits);
        f.Close(ready);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt));
    }

    private static CommitId Settings(GitFixture git)
    {
        git.Write("settings.txt", "0\n");
        return git.Commit("settings");
    }

    private static CommitId RenameBase(GitFixture git)
    {
        git.Write("x.txt", "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\n");
        return git.Commit("x");
    }

    private static async Task ConflictingSources(PreparationFixture f)
    {
        await Write(f, T, ("settings.txt", "b=1\n"));
        await Write(f, C, ("settings.txt", "c=1\n"));
    }

    private static byte[] Evidence(PreparationFixture f, EvidenceFile file) =>
        RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder, file.RelativePath, file.Content, file.ByteLength);

    [Fact]
    public async Task Repository_merge_settings_changed_after_planning_preserve_the_planned_join()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: RenameBase);
        await Write(f, T, ("x.txt", null), ("y.txt", "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\n"));
        await Write(f, C, ("x.txt", Renamed));
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await Prepare(f, operation, point =>
        {
            if (point == "journal.join-plan.after") throw new Crash();
        }));
        var planned = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Join>()).Commit;
        f.Git.Git("config", "merge.renames", "false");
        f.Git.Git("config", "merge.directoryRenames", "true");
        f.Git.Git("config", "merge.conflictStyle", "diff3");
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, operation));
        Assert.Equal(planned, ready.Execution.Location.AttemptBase);
        Assert.Equal(Renamed, File.ReadAllText(Path.Combine(ready.Checkout, "y.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "x.txt")));
    }

    [Fact]
    public async Task Repository_settings_changed_after_conflict_publication_repeat_identical_evidence()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        await ConflictingSources(f);
        var operation = f.Op();
        var first = Assert.IsType<Preparation.Blocked>(await Prepare(f, operation));
        Assert.Equal("FanInConflict", first.Block.Problem.ToString());
        f.Git.Git("config", "merge.conflictStyle", "diff3");
        f.Git.Git("config", "merge.renames", "false");
        f.Git.Git("config", "merge.directoryRenames", "true");
        var second = Assert.IsType<Preparation.Blocked>(await Prepare(f, operation));
        Assert.Equal("FanInConflict", second.Block.Problem.ToString());
        Assert.Equal(Evidence(f, first.Block.Conflict!.Stdout), Evidence(f, second.Block.Conflict!.Stdout));
        Assert.Equal(Evidence(f, first.Block.Conflict.Stderr), Evidence(f, second.Block.Conflict.Stderr));
        Assert.Equal(RunJournal.Canonical(first.Block), RunJournal.Canonical(second.Block));
        var tree = Encoding.UTF8.GetString(Evidence(f, second.Block.Conflict.Stdout)).Split('\0')[0];
        Assert.DoesNotContain("|||||||", f.Git.Git("show", tree + ":settings.txt"));
        Assert.Equal(new[] { "settings.txt" }, second.Block.Conflict.Paths);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("info")]
    public async Task Untracked_and_info_attributes_cannot_resolve_a_join_conflict(string location)
    {
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        await ConflictingSources(f);
        if (location == "project") f.Git.Write(".gitattributes", "settings.txt merge=union\n");
        else
        {
            f.Git.Write(".git/info/attributes", "settings.txt merge=keep\n");
            f.Git.Git("config", "merge.keep.driver", "true");
        }
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, f.Op()));
        Assert.Equal("FanInConflict", blocked.Block.Problem.ToString());
        Assert.Equal(new[] { "settings.txt" }, blocked.Block.Conflict!.Paths);
    }

    [Fact]
    public async Task Conflict_evidence_records_the_fixed_policy_and_the_committed_attribute_source()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        await ConflictingSources(f);
        f.Git.Git("config", "merge.conflictStyle", "diff3");
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, f.Op()));
        Assert.Equal("FanInConflict", blocked.Block.Problem.ToString());
        var evidence = blocked.Block.Conflict!;
        Assert.Equal(new[]
        {
            new ConfigEntry("core.attributesFile", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            new ConfigEntry("merge.conflictStyle", "merge"),
            new ConfigEntry("merge.renames", "true"),
            new ConfigEntry("merge.directoryRenames", "conflict"),
            new ConfigEntry("merge.renormalize", "false"),
        }, evidence.MergeConfig);
        Assert.Equal("2a6facb70b2b7d4a23d103ddf1701e457bda8368", evidence.AttributeSource.Hex);
        var operation = f.Op();
        var invalid = blocked.Block with { Operation = operation, Conflict = evidence with { AttributeSource = new("not-a-commit") } };
        Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(W, f.RunId, operation, new RunEvent.Blocked(invalid))).Reason.Problem.ToString());
    }

    [Fact]
    public async Task Global_conflict_style_does_not_change_conflicted_blob_contents()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        await ConflictingSources(f);
        File.WriteAllText(f.Git.Environment["GIT_CONFIG_GLOBAL"], "[merge]\nconflictStyle = diff3\n");
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, f.Op()));
        Assert.Equal("FanInConflict", blocked.Block.Problem.ToString());
        var tree = Encoding.UTF8.GetString(Evidence(f, blocked.Block.Conflict!.Stdout)).Split('\0')[0];
        var blob = f.Git.Git("show", tree + ":settings.txt");
        Assert.Contains("\nb=1\n=======\nc=1\n", blob);
        Assert.DoesNotContain("|||||||", blob);
    }

    [Fact]
    public async Task Committed_run_base_union_attribute_merges_both_literal_lines()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: git =>
        {
            git.Write("settings.txt", "0\n");
            git.Write(".gitattributes", "settings.txt merge=union\n");
            return git.Commit("settings");
        });
        await ConflictingSources(f);
        f.Git.Write(".gitattributes", "settings.txt -merge\n");
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        Assert.Equal("b=1\nc=1\n", File.ReadAllText(Path.Combine(ready.Checkout, "settings.txt")));
    }

    [Fact]
    public async Task Attributes_committed_by_a_source_do_not_change_the_run_base_policy()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        await Write(f, T, ("settings.txt", "b=1\n"), (".gitattributes", "settings.txt merge=union\n"));
        await Write(f, C, ("settings.txt", "c=1\n"));
        f.Git.Git("checkout", "-q", "--detach", f.Read().CurrentResults[T].Code is CodeOutput.Produced produced ? produced.Code.Commit.Hex : throw new InvalidOperationException());
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, f.Op()));
        Assert.Equal("FanInConflict", blocked.Block.Problem.ToString());
        Assert.Equal(new[] { "settings.txt" }, blocked.Block.Conflict!.Paths);
    }

    [Fact]
    public async Task Writer_merge_configuration_does_not_change_a_siblings_join()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: RenameBase);
        var b = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Commit(f, b.Checkout, ("x.txt", null), ("y.txt", "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\n"));
        Assert.Equal(0, f.Git.Run(b.Checkout, "config", "merge.renames", "false").ExitCode);
        f.Close(b);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), b.Execution.Launch.Attempt));
        await Write(f, C, ("x.txt", Renamed));
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        Assert.Equal(Renamed, File.ReadAllText(Path.Combine(ready.Checkout, "y.txt")));
    }

    [Theory]
    [InlineData("journal.plan.after")]
    [InlineData("git.join-merge-1.before")]
    [InlineData("git.join-commit.after")]
    public async Task Stale_operation_cannot_rewind_a_fresh_join(string point)
    {
        using var f = new PreparationFixture(Diamond());
        await Write(f, T, ("b.txt", "B\n"));
        await Write(f, C, ("c.txt", "C\n"));
        var stale = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await Prepare(f, stale, step => { if (step == point) throw new Crash(); }));
        var old = f.Read().CurrentResults[T];
        var again = Assert.IsType<Preparation.Ready>(await f.Prepare(T, cause: new AttemptCause.Continue(((ResultOrigin.Executed)old.Origin).Attempt, f.Op())));
        Commit(f, again.Checkout, ("b.txt", "B again\n"));
        f.Close(again);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), again.Execution.Launch.Attempt));
        var fresh = f.Op();
        var current = Assert.IsType<Preparation.Ready>(await Prepare(f, fresh));
        var retry = await Prepare(f, stale);
        Assert.Equal("991c91ce7fcec2c34784c56385034d187447fb91", GitFixture.Read(f.Git.Open().ReadRef(JoinRef))?.Hex);
        var replay = Assert.IsType<Preparation.Blocked>(retry);
        Assert.Equal("InputUnavailable", replay.Block.Problem.ToString());
        Assert.Equal("A join source is no longer its task's current result.", replay.Block.Detail);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef("refs/heads/main"))?.Hex);
        Assert.Equal("B again\n", File.ReadAllText(Path.Combine(current.Checkout, "b.txt")));
        Assert.Equal(current, await Prepare(f, fresh));
    }

    [UnixFact]
    public async Task Changed_git_diagnostics_on_a_conflict_retry_publish_new_evidence()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        await ConflictingSources(f);
        var operation = f.Op();
        var first = Assert.IsType<Preparation.Blocked>(await Prepare(f, operation));
        Assert.Equal("FanInConflict", first.Block.Problem.ToString());
        var realGit = CommandResolver.Create((System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;
        var bin = Directory.CreateDirectory(Path.Combine(f.Git.Open().CommonDirectory, "test-bin")).FullName;
        var shim = Path.Combine(bin, "git");
        File.WriteAllText(shim, "#!/bin/sh\nmerge=0\nfor arg do\n[ \"$arg\" = merge-tree ] && merge=1\ndone\n'" + realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\nstatus=$?\n[ \"$merge\" = 1 ] && printf 'another Git build\\n' >&2\nexit $status\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>(f.Git.Environment) { ["PATH"] = bin + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH") };
        var second = Assert.IsType<Preparation.Blocked>(await Joins(f, environment: environment).Prepare(W, f.RunId, operation, U, new AttemptCause.Initial()));
        Assert.Equal("FanInConflict", second.Block.Problem.ToString());
        Assert.Equal("another Git build\n", Encoding.UTF8.GetString(Evidence(f, second.Block.Conflict!.Stderr)));
        Assert.Equal(Evidence(f, first.Block.Conflict!.Stdout), Evidence(f, second.Block.Conflict.Stdout));
        Assert.NotEqual(first.Block.Conflict.Stdout.RelativePath, second.Block.Conflict.Stdout.RelativePath);
    }
}
