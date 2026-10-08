using System.Text;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

public sealed class JoinPolicyTests
{
    private const string JoinRef = "refs/heads/idp/93f23689/join/c67f2fc3";
    private const string Renamed = "changed 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\n";

    private static Workflow Diamond() => Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U)), T, U), C, U);

    private static Materializer Joins(PreparationFixture f, Action<string>? probe = null, IReadOnlyDictionary<string, string>? environment = null) =>
        MergeJoins.Open(f.Git.Folder, f.Store, new QuiescentBoundary(), new Clock(), environment ?? f.Git.Environment, probe);

    private static ValueTask<Preparation> Prepare(PreparationFixture f, OperationId operation, Action<string>? probe = null) =>
        Joins(f, probe).Prepare(f.Lease(U), operation, new AttemptCause.Initial());

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
        await f.Close(ready);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch.Attempt));
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
        Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, operation, new RunEvent.Blocked(invalid))).Reason.Problem.ToString());
    }

    [Theory]
    [InlineData("home")]
    [InlineData("xdg")]
    public async Task Global_conflict_style_does_not_change_conflicted_blob_contents(string location)
    {
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        await ConflictingSources(f);
        var folder = Path.GetDirectoryName(f.Git.Folder)!;
        var home = Directory.CreateDirectory(Path.Combine(folder, "home")).FullName;
        var xdg = Directory.CreateDirectory(Path.Combine(folder, "xdg")).FullName;
        var config = location == "home" ? Path.Combine(home, ".gitconfig") : Path.Combine(xdg, "git", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        var marker = Path.Combine(folder, "driver-ran");
        File.WriteAllText(config, "[merge]\nconflictStyle = diff3\ndefault = keep\n");
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "--file", config, "merge.keep.driver",
            "echo driver > \"" + marker.Replace('\\', '/') + "\"; exit 0").ExitCode);
        var environment = new Dictionary<string, string>(f.Git.Environment) { ["HOME"] = home, ["XDG_CONFIG_HOME"] = xdg };
        environment.Remove("GIT_CONFIG_GLOBAL");
        var blocked = Assert.IsType<Preparation.Blocked>(await Joins(f, environment: environment)
            .Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial()));
        Assert.Equal("FanInConflict", blocked.Block.Problem.ToString());
        Assert.Equal(new[] { "settings.txt" }, blocked.Block.Conflict!.Paths);
        var tree = Encoding.UTF8.GetString(Evidence(f, blocked.Block.Conflict.Stdout)).Split('\0')[0];
        Assert.Equal("<<<<<<< c9f977277f76f1173ed36a15c0454b88ef2caff2\nb=1\n=======\nc=1\n>>>>>>> c1d5e59b5a21d0fe2661cdededaf476ddaa9b1ba\n",
            f.Git.Git("show", tree + ":settings.txt"));
        Assert.False(File.Exists(marker));
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
        await f.Close(b);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(b.Execution.Location.Owner.Task), f.Op(), b.Execution.Launch.Attempt));
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
        await f.Close(again);
        Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(again.Execution.Location.Owner.Task), f.Op(), again.Execution.Launch.Attempt));
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
        Executable.Write(shim, "#!/bin/sh\nmerge=0\nfor arg do\n[ \"$arg\" = merge-tree ] && merge=1\ndone\n'" + realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\nstatus=$?\n[ \"$merge\" = 1 ] && printf 'another Git build\\n' >&2\nexit $status\n");
        var environment = new Dictionary<string, string>(f.Git.Environment) { ["PATH"] = bin + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH") };
        var second = Assert.IsType<Preparation.Blocked>(await Joins(f, environment: environment).Prepare(f.Lease(U), operation, new AttemptCause.Initial()));
        Assert.Equal("FanInConflict", second.Block.Problem.ToString());
        Assert.Equal("another Git build\n", Encoding.UTF8.GetString(Evidence(f, second.Block.Conflict!.Stderr)));
        Assert.Equal(Evidence(f, first.Block.Conflict!.Stdout), Evidence(f, second.Block.Conflict.Stdout));
        Assert.NotEqual(first.Block.Conflict.Stdout.RelativePath, second.Block.Conflict.Stdout.RelativePath);
    }

    private static IReadOnlyDictionary<string, string> GitShim(PreparationFixture f, string script)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var realGit = CommandResolver.Create((System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;
        var bin = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "test-bin")).FullName;
        var shim = Path.Combine(bin, "git");
        Executable.Write(shim, "#!/bin/sh\n" + script + "\nexec '" + realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\n");
        return new Dictionary<string, string>(f.Git.Environment)
        {
            ["PATH"] = bin + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH"),
        };
    }

    private static IReadOnlyDictionary<string, string> GitVersion(PreparationFixture f, string version) =>
        GitShim(f, "for arg do last=$arg; done\nif [ \"$last\" = version ]; then printf '%s\\n' '" + version + "'; exit 0; fi");

    private static async Task CleanSources(PreparationFixture f)
    {
        await Write(f, T, ("b.txt", "B\n"));
        await Write(f, C, ("c.txt", "C\n"));
    }

    private static void CleanContent(Preparation.Ready ready)
    {
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
    }

    [UnixFact]
    public async Task Git_239_single_input_successor_prepares_literal_source_content()
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        await Write(f, T, ("b.txt", "B\n"));
        var ready = Assert.IsType<Preparation.Ready>(await Joins(f, environment: GitVersion(f, "git version 2.39.5 (Apple Git-154)"))
            .Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial()));
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
    }

    [UnixFact]
    public async Task Git_239_join_blocks_with_its_literal_version_without_a_ref_or_plan()
    {
        using var f = new PreparationFixture(Diamond());
        await CleanSources(f);
        var blocked = Assert.IsType<Preparation.Blocked>(await Joins(f, environment: GitVersion(f, "git version 2.39.5 (Apple Git-154)"))
            .Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial()));
        Assert.Equal(MaterializationProblem.GitVersionUnsupported, blocked.Block.Problem);
        Assert.Equal("Joins need Git 2.43 or later. Installed: git version 2.39.5 (Apple Git-154).", blocked.Block.Detail);
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(JoinRef)));
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Join>());
    }

    [UnixFact]
    public async Task Git_242_blocks_the_same_content_join_that_git_243_prepares()
    {
        using var f = new PreparationFixture(Diamond(), configureBase: RenameBase);
        await Write(f, T, ("x.txt", Renamed));
        await Write(f, C, ("x.txt", "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nchanged 10\n"));
        var blocked = Assert.IsType<Preparation.Blocked>(await Joins(f, environment: GitVersion(f, "git version 2.42.0"))
            .Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial()));
        Assert.Equal(MaterializationProblem.GitVersionUnsupported, blocked.Block.Problem);
        Assert.Equal("Joins need Git 2.43 or later. Installed: git version 2.42.0.", blocked.Block.Detail);
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(JoinRef)));
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Join>());
        var ready = Assert.IsType<Preparation.Ready>(await Joins(f, environment: GitVersion(f, "git version 2.43.0"))
            .Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial()));
        Assert.Equal("changed 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nchanged 10\n", File.ReadAllText(Path.Combine(ready.Checkout, "x.txt")));
    }

    [UnixTheory]
    [InlineData("GIT_COMMON_DIR")]
    [InlineData("GIT_CONFIG_COUNT")]
    [InlineData("GIT_CONFIG_PARAMETERS")]
    [InlineData("GIT_CONFIG_SYSTEM")]
    [InlineData("GIT_CONFIG_GLOBAL")]
    public async Task Inherited_git_configuration_cannot_run_a_driver_or_publish_its_ref(string variable)
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(Diamond(), configureBase: git =>
        {
            git.Write(".gitattributes", "settings.txt merge=keep\n");
            return Settings(git);
        });
        await ConflictingSources(f);
        var folder = Path.GetDirectoryName(f.Git.Folder)!;
        var marker = Path.Combine(folder, "driver-ran");
        var script = Path.Combine(folder, "driver.sh");
        Assert.DoesNotContain("'", script);
        Executable.Write(script, "#!/bin/sh\ntouch \"" + marker + "\"\ngit update-ref refs/heads/driver-wrote " + f.Read().Base.Commit.Hex + "\nexit 0\n");
        var command = "\"" + script + "\"";
        var environment = new Dictionary<string, string>(f.Git.Environment);
        switch (variable)
        {
            case "GIT_COMMON_DIR":
                f.Git.Git("config", "merge.keep.driver", command);
                environment[variable] = f.Git.Open().CommonDirectory;
                break;
            case "GIT_CONFIG_COUNT":
                environment[variable] = "1";
                environment["GIT_CONFIG_KEY_0"] = "merge.keep.driver";
                environment["GIT_CONFIG_VALUE_0"] = command;
                break;
            case "GIT_CONFIG_PARAMETERS":
                environment[variable] = "'merge.keep.driver'='" + command + "'";
                break;
            default:
                var config = Path.Combine(folder, "driver.config");
                Assert.Equal(0, f.Git.Run(f.Git.Folder, "config", "--file", config, "merge.keep.driver", command).ExitCode);
                environment[variable] = config;
                if (variable == "GIT_CONFIG_SYSTEM") environment["GIT_CONFIG_NOSYSTEM"] = "0";
                break;
        }
        var blocked = Assert.IsType<Preparation.Blocked>(await Joins(f, environment: environment)
            .Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial()));
        Assert.Equal(MaterializationProblem.FanInConflict, blocked.Block.Problem);
        Assert.Equal(new[] { "settings.txt" }, blocked.Block.Conflict!.Paths);
        Assert.False(File.Exists(marker));
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef("refs/heads/driver-wrote")));
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(JoinRef)));
    }

    [UnixTheory]
    [InlineData("idevelop")]
    [InlineData("merges")]
    public async Task A_linked_scratch_component_blocks_a_join_without_deleting_outside_files(string component)
    {
        using var f = new PreparationFixture(Diamond());
        await CleanSources(f);
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "outside")).FullName;
        var journal = Path.Combine(Directory.CreateDirectory(Path.Combine(outside, "123-0123456789abcdef0123456789abcdef")).FullName, "journal.json");
        File.WriteAllText(journal, "{\"journal\":\"keep\"}\n");
        File.WriteAllText(Path.Combine(outside, "runs.txt"), "keep runs\n");
        var idevelop = Path.Combine(f.Git.Open().CommonDirectory, "idevelop");
        var link = component == "idevelop" ? idevelop : Path.Combine(idevelop, "merges");
        if (Directory.Exists(link)) Directory.Delete(link, recursive: true);
        Directory.CreateSymbolicLink(link, outside);
        var blocked = Assert.IsType<Preparation.Blocked>(await Prepare(f, f.Op()));
        Assert.Equal("GitFailed", blocked.Block.Problem.ToString());
        Assert.Equal("The merge scratch folder " + link + " is a link.", blocked.Block.Detail);
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(JoinRef)));
        Assert.Equal("{\"journal\":\"keep\"}\n", File.ReadAllText(journal));
        Assert.Equal("keep runs\n", File.ReadAllText(Path.Combine(outside, "runs.txt")));
    }

    [UnixFact]
    public async Task Scratch_cleanup_leaves_unexpected_entries_without_following_nested_links()
    {
        using var f = new PreparationFixture(Diamond());
        await CleanSources(f);
        var root = Path.GetDirectoryName(f.Git.Folder)!;
        var nestedOutside = Directory.CreateDirectory(Path.Combine(root, "nested-outside")).FullName;
        var linkedOutside = Directory.CreateDirectory(Path.Combine(root, "linked-outside")).FullName;
        File.WriteAllText(Path.Combine(nestedOutside, "keep.txt"), "keep nested target\n");
        File.WriteAllText(Path.Combine(linkedOutside, "keep.txt"), "keep linked target\n");
        var merges = Directory.CreateDirectory(Path.Combine(f.Git.Open().CommonDirectory, "idevelop", "merges")).FullName;
        var scratch = Directory.CreateDirectory(Path.Combine(merges, "123-0123456789abcdef0123456789abcdef")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(scratch, "escape"), nestedOutside);
        Directory.CreateSymbolicLink(Path.Combine(merges, "456-fedcba9876543210fedcba9876543210"), linkedOutside);
        var notes = Directory.CreateDirectory(Path.Combine(merges, "notes")).FullName;
        File.WriteAllText(Path.Combine(notes, "keep.txt"), "keep notes\n");
        File.WriteAllText(Path.Combine(merges, "README"), "keep readme\n");
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        CleanContent(ready);
        Assert.Equal(new[] { "123-0123456789abcdef0123456789abcdef", "456-fedcba9876543210fedcba9876543210", "README", "notes" },
            Directory.GetFileSystemEntries(merges).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "escape" }, Directory.GetFileSystemEntries(scratch).Select(Path.GetFileName));
        Assert.NotNull(new DirectoryInfo(Path.Combine(scratch, "escape")).LinkTarget);
        Assert.Equal("keep nested target\n", File.ReadAllText(Path.Combine(nestedOutside, "keep.txt")));
        Assert.Equal("keep linked target\n", File.ReadAllText(Path.Combine(linkedOutside, "keep.txt")));
        Assert.Equal("keep notes\n", File.ReadAllText(Path.Combine(notes, "keep.txt")));
        Assert.Equal("keep readme\n", File.ReadAllText(Path.Combine(merges, "README")));
    }

    [UnixFact]
    public async Task An_orphaned_content_merge_after_scratch_cleanup_changes_no_refs_or_execution_files()
    {
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U), Writer(D)), T, U), C, U), T, D), C, D);
        using var f = new PreparationFixture(workflow, configureBase: git =>
        {
            git.Write("m.txt", "1\n2\n3\n4\n5\n6\n7\n8\n");
            return git.Commit("m");
        });
        await Write(f, T, ("m.txt", "ONE\n2\n3\n4\n5\n6\n7\n8\n"));
        await Write(f, C, ("m.txt", "1\n2\n3\n4\n5\n6\n7\nEIGHT\n"));
        var state = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "orphan-state")).FullName;
        var realGit = CommandResolver.Create((System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;
        var environment = GitShim(f, "STATE='" + state.Replace("'", "'\\''", StringComparison.Ordinal) + "'\nexport STATE\n" +
            "for arg do\nif [ \"$arg\" = merge-tree ] && mkdir \"$STATE/first\" 2>/dev/null; then\n" +
            "mkdir -p \"$GIT_DIR/info\"\nmkfifo \"$GIT_DIR/info/attributes\"\n" +
            "sh -c '\"$0\" \"$@\" > \"$STATE/orphan.out\" 2> \"$STATE/orphan.err\"; echo $? > \"$STATE/orphan.exit\"' '" + realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\" < /dev/null > /dev/null 2>&1 &\n" +
            "( exec 3> \"$GIT_DIR/info/attributes\"; : > \"$STATE/paused\"; while [ ! -e \"$STATE/release\" ]; do sleep 0.05; done ) < /dev/null > /dev/null 2>&1 &\n" +
            "while [ ! -e \"$STATE/paused\" ]; do sleep 0.05; done\nrm \"$GIT_DIR/info/attributes\"\nfi\ndone");
        var merges = Path.Combine(f.Git.Open().CommonDirectory, "idevelop", "merges");
        var exit = Path.Combine(state, "orphan.exit");
        try
        {
            var operation = f.Op();
            var ready = Assert.IsType<Preparation.Ready>(await Joins(f, environment: environment)
                .Prepare(f.Lease(U), operation, new AttemptCause.Initial()));
            Assert.Equal("ONE\n2\n3\n4\n5\n6\n7\nEIGHT\n", File.ReadAllText(Path.Combine(ready.Checkout, "m.txt")));
            Assert.Equal("95b1feceb5015d66a187cd67ef90bc60e434deb7", f.Git.Git("rev-parse", ready.Execution.Location.AttemptBase.Hex + "^{tree}").Trim());
            Assert.False(File.Exists(exit));
            Assert.Empty(Directory.GetFileSystemEntries(merges));
            var other = Assert.IsType<Preparation.Ready>(await Joins(f).Prepare(f.Lease(D), f.Op(), new AttemptCause.Initial()));
            Assert.Equal("ONE\n2\n3\n4\n5\n6\n7\nEIGHT\n", File.ReadAllText(Path.Combine(other.Checkout, "m.txt")));
            var refs = f.Git.Git("for-each-ref");
            var execution = Path.Combine(f.Git.Folder, ".idp");
            var paths = Directory.GetFiles(execution, "*", SearchOption.AllDirectories);
            var locks = paths.Where(path => Path.GetFileName(path) is "run.lock" or "control.lock").ToArray();
            Assert.Equal(new[]
            {
                "attempts/00000000-0000-0000-0000-000000000002/run.lock",
                "attempts/00000000-0000-0000-0000-000000000003/run.lock",
                "attempts/00000000-0000-0000-0000-000000000004/run.lock",
                "attempts/00000000-0000-0000-0000-000000000005/run.lock",
                "runs/00000000-0000-0000-0000-000000000001/00000000-0000-0000-0000-000000000010/control.lock",
            }, locks.Select(path => Path.GetRelativePath(execution, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
            foreach (var path in locks) Assert.Equal(0, new FileInfo(path).Length);
            var files = paths.Except(locks).ToDictionary(path => Path.GetRelativePath(execution, path), File.ReadAllBytes, StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(state, "release"), "");
            await WaitForOrphan();
            Assert.Equal("0\n", File.ReadAllText(exit));
            Assert.Equal("95b1feceb5015d66a187cd67ef90bc60e434deb7", File.ReadAllText(Path.Combine(state, "orphan.out")).Split('\0')[0]);
            Assert.Equal("", File.ReadAllText(Path.Combine(state, "orphan.err")));
            Assert.Equal(refs, f.Git.Git("for-each-ref"));
            Assert.Equal(paths.Select(path => Path.GetRelativePath(execution, path)).Order(StringComparer.Ordinal), Directory.GetFiles(execution, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(execution, path)).Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(execution, path)));
            foreach (var path in locks) Assert.Equal(0, new FileInfo(path).Length);
            Assert.Empty(Directory.GetFileSystemEntries(merges));
            Assert.Equal(0, f.Git.Run(f.Git.Folder, "fsck", "--strict", "--no-dangling").ExitCode);
            Assert.Equal(ready, await Prepare(f, operation));
        }
        finally
        {
            File.WriteAllText(Path.Combine(state, "release"), "");
            if (Directory.Exists(Path.Combine(state, "first"))) await WaitForOrphan();
        }

        async Task WaitForOrphan()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(exit) && DateTime.UtcNow < deadline) await System.Threading.Tasks.Task.Delay(50);
            Assert.True(File.Exists(exit), "The orphaned merge did not finish within 60 seconds.");
        }
    }

    [UnixFact]
    public async Task Unexpected_scratch_entries_keep_a_join_ready_and_stay_after_a_restart()
    {
        if (OperatingSystem.IsWindows()) return;
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U), Writer(D)), T, U), C, U), T, D), C, D);
        using var f = new PreparationFixture(workflow);
        await CleanSources(f);
        var listing = Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "merge-dirs");
        var environment = GitShim(f, "for arg do\nif [ \"$arg\" = merge-tree ]; then mkdir -p \"$GIT_DIR/held\"; : > \"$GIT_DIR/held/f\"; chmod 500 \"$GIT_DIR/held\"; printf '%s\\n' \"$GIT_DIR\" >> '" + listing + "'; fi\ndone");
        var merges = Path.Combine(f.Git.Open().CommonDirectory, "idevelop", "merges");
        try
        {
            var ready = Assert.IsType<Preparation.Ready>(await Joins(f, environment: environment)
                .Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial()));
            CleanContent(ready);
            var leftover = Assert.Single(Directory.GetDirectories(merges));
            Assert.StartsWith(System.Environment.ProcessId + "-", Path.GetFileName(leftover));
            File.SetUnixFileMode(Path.Combine(leftover, "held"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var restarted = Assert.IsType<Preparation.Ready>(await Joins(f).Prepare(f.Lease(D), f.Op(), new AttemptCause.Initial()));
            CleanContent(restarted);
            Assert.Equal(new[] { leftover }, Directory.GetFileSystemEntries(merges));
            Assert.Equal(new[] { "held" }, Directory.GetFileSystemEntries(leftover).Select(Path.GetFileName));
        }
        finally
        {
            if (File.Exists(listing))
                foreach (var scratch in File.ReadAllLines(listing))
                {
                    if (!Directory.Exists(scratch)) continue;
                    var held = Path.Combine(scratch, "held");
                    if (Directory.Exists(held)) File.SetUnixFileMode(held, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Directory.Delete(scratch, recursive: true);
                }
        }
    }

    [UnixTheory]
    [InlineData("HEAD")]
    [InlineData("config")]
    [InlineData("info/attributes")]
    [InlineData("refs")]
    [InlineData("info")]
    public async Task A_link_at_an_expected_scratch_entry_is_removed_without_following_it(string entry)
    {
        using var f = new PreparationFixture(Diamond());
        await CleanSources(f);
        using var dead = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("true") { UseShellExecute = false })!;
        await dead.WaitForExitAsync();
        var scratch = Path.Combine(f.Git.Open().CommonDirectory, "idevelop", "merges", $"{dead.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(scratch, "refs"));
        Directory.CreateDirectory(Path.Combine(scratch, "info"));
        File.WriteAllText(Path.Combine(scratch, "HEAD"), "ref: refs/heads/none\n");
        File.WriteAllText(Path.Combine(scratch, "config"), "[core]\nbare = true\nrepositoryformatversion = 0\n");
        var link = Path.Combine(scratch, entry);
        var outside = Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "outside");
        var content = outside;
        if (entry is "refs" or "info")
        {
            content = Path.Combine(Directory.CreateDirectory(outside).FullName, entry == "info" ? "attributes" : "heads");
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, outside);
        }
        else File.Delete(link);
        File.WriteAllText(content, "keep outside\n");
        if (entry is not ("refs" or "info")) File.CreateSymbolicLink(link, outside);
        var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
        CleanContent(ready);
        Assert.Equal("keep outside\n", File.ReadAllText(content));
        Assert.False(Path.Exists(scratch));
    }

    [UnixFact]
    public async Task A_join_removes_scratch_state_owned_by_an_exited_process()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(Diamond());
        await CleanSources(f);
        using var dead = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("true") { UseShellExecute = false })!;
        await dead.WaitForExitAsync();
        var merges = Path.Combine(f.Git.Open().CommonDirectory, "idevelop", "merges");
        var leftover = Path.Combine(merges, $"{dead.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(leftover, "refs"));
        File.WriteAllText(Path.Combine(leftover, "HEAD"), "ref: refs/heads/none\n");
        File.WriteAllText(Path.Combine(leftover, "config"), "[core]\nbare = true\nrepositoryformatversion = 0\n");
        var stuck = Path.Combine(merges, $"{dead.Id}-{Guid.NewGuid():N}");
        var held = Directory.CreateDirectory(Path.Combine(stuck, "held")).FullName;
        File.WriteAllText(Path.Combine(held, "f"), "");
        File.SetUnixFileMode(held, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var ready = Assert.IsType<Preparation.Ready>(await Prepare(f, f.Op()));
            CleanContent(ready);
            Assert.False(Directory.Exists(leftover));
            Assert.Equal(new[] { stuck }, Directory.GetDirectories(merges));
        }
        finally
        {
            File.SetUnixFileMode(held, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [UnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clean_and_conflicted_joins_use_repository_scratch_and_leave_it_empty(bool conflict)
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(Diamond(), configureBase: Settings);
        if (conflict) await ConflictingSources(f);
        else await CleanSources(f);
        var listing = Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "merge-dirs");
        var environment = GitShim(f, "for arg do\n[ \"$arg\" = merge-tree ] && printf '%s\\n' \"$GIT_DIR\" >> '" + listing + "'\ndone");
        var temporary = Directory.GetDirectories(Path.GetTempPath(), "idevelop-merge-*");
        var outcome = await Joins(f, environment: environment).Prepare(f.Lease(U), f.Op(), new AttemptCause.Initial());
        if (conflict)
        {
            var blocked = Assert.IsType<Preparation.Blocked>(outcome);
            Assert.Equal(MaterializationProblem.FanInConflict, blocked.Block.Problem);
            Assert.Equal(new[] { "settings.txt" }, blocked.Block.Conflict!.Paths);
        }
        else CleanContent(Assert.IsType<Preparation.Ready>(outcome));
        var merges = Path.Combine(f.Git.Open().CommonDirectory, "idevelop", "merges");
        var scratch = Assert.Single(File.ReadAllLines(listing));
        Assert.Equal(merges, Path.GetDirectoryName(scratch));
        Assert.StartsWith(System.Environment.ProcessId + "-", Path.GetFileName(scratch));
        Assert.Empty(Directory.GetDirectories(merges));
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "idevelop-merge-*").Except(temporary));
    }
}
