using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

[Collection(IDevelop.TestSupport.ProcessCollection.Name)]
public sealed class ReportReuseTests
{
    [Fact]
    public void Explicit_matching_standalone_reuse_copies_one_report_and_source()
    {
        using var f = new RunFixtures();
        var commit = Repository(f.Project);
        f.Approve(commit);
        WriteStandalone(f.Project, commit);
        var source = new AttemptSource.Standalone(T, new(Id(90)));
        var confirmation = f.Op();
        var first = Assert.IsType<RunDecision.Created>(f.Store.ReuseReport(W, Run, f.Op(), T, source, commit, confirmation));
        var repeated = Assert.IsType<RunDecision.Existing>(f.NewStore().ReuseReport(W, Run, f.Op(), T, source, commit, confirmation));
        Assert.Equal(new ResultId(Id(102)), Assert.IsType<RunEvent.ResultAccepted>(first.Event).Result.Id);
        Assert.Equal(new ResultId(Id(102)), Assert.IsType<RunEvent.ResultAccepted>(repeated.Event).Result.Id);
        var record = f.Read();
        Assert.Equal([new ResultId(Id(102))], record.Results.Select(result => result.Id));
        Assert.Equal("Checked.", record.CurrentResults[T].Report);
        Assert.Equal(source, Assert.IsType<ResultOrigin.Reused>(record.CurrentResults[T].Origin).Source);
        Assert.Equal(2, record.Sequence);
        Assert.Equal("*.tmp\nattempts/\nruns/\n", File.ReadAllText(Path.Combine(f.Project, ".idp", ".gitignore")));
        Assert.Equal(RunProblem.StartConflict, Problem(f.Store.Reserve(W, Run, f.Op(), T, new(V1), new AttemptCause.Initial(), commit, "")));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("definition")]
    [InlineData("code")]
    [InlineData("handoff")]
    [InlineData("missingObject")]
    [InlineData("continued")]
    [InlineData("inputs")]
    [InlineData("prompt")]
    [InlineData("writer")]
    public void Reuse_rejects_missing_or_nonmatching_evidence_even_with_confirmation(string change)
    {
        using var f = new RunFixtures();
        var sourceCommit = Repository(f.Project);
        WriteStandalone(f.Project, sourceCommit, change);
        var receivingCommit = sourceCommit;
        if (change == "code")
        {
            File.WriteAllText(Path.Combine(f.Project, "src", "a.cs"), "changed");
            receivingCommit = Commit(f.Project);
        }
        f.Approve(receivingCommit);
        Assert.Equal(RunProblem.ReuseUnverifiable, Problem(f.Store.ReuseReport(W, Run, f.Op(), T,
            new(T, new(Id(90))), change == "missingObject" ? new(new string('9', 40)) : receivingCommit, f.Op())));
        Assert.Equal(V1, f.Read().Revision.Id.Sha256);
        Assert.Equal(1, f.Read().Sequence);
    }

    [Fact]
    public void Tracked_idp_content_is_filtered_from_all_three_tree_comparisons()
    {
        using var f = new RunFixtures();
        var first = Repository(f.Project);
        Directory.CreateDirectory(Path.Combine(f.Project, ".idp"));
        File.WriteAllText(Path.Combine(f.Project, ".idp", "workflow.json"), "first");
        var second = Commit(f.Project);
        File.WriteAllText(Path.Combine(f.Project, ".idp", "workflow.json"), "second");
        var third = Commit(f.Project);
        var match = ReportReuse.CompareTrees(f.Project, Tree(f.Project, first), Tree(f.Project, second), third);
        Assert.Equal(TreeComparison.ContentMatch, match.Comparison);
        File.WriteAllText(Path.Combine(f.Project, "src", "a.cs"), "different");
        var different = Commit(f.Project);
        Assert.Equal(TreeComparison.ReuseUnverifiable, ReportReuse.CompareTrees(f.Project, Tree(f.Project, first), Tree(f.Project, second),
            different).Comparison);
        f.Approve(different);
        WriteStandalone(f.Project, first);
        Assert.Equal(RunProblem.ReuseUnverifiable, Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), different, f.Op())));
    }

    [Fact]
    public void Reuse_requires_confirmation_and_keeps_the_source_log_bytes()
    {
        using var f = new RunFixtures();
        var commit = Repository(f.Project);
        f.Approve(commit);
        WriteStandalone(f.Project, commit);
        var path = Path.Combine(AttemptLog.FolderOf(DataFolder.Attempts(f.Project), T, new(Id(90))), "events.jsonl");
        var before = File.ReadAllBytes(path);
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), commit, default)));
        Assert.IsType<RunDecision.Created>(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), commit, f.Op()));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("Checked.", f.Read().CurrentResults[T].Report);
    }

    private static void WriteStandalone(string project, CommitId commit, string change = "")
    {
        var definition = Task(model: change == "definition" ? "m2" : "m1");
        var tree = Tree(project, commit);
        using var log = AttemptLog.Create(DataFolder.Attempts(project), new AttemptEvent.Requested(At, new(Id(90)), T,
            "Plan", definition.Execution!, change == "prompt" ? "Different" : "Inspect", "codex", [])
        {
            StandaloneCapture = change == "old" ? null : new(definition, change == "inputs" ? "Declared" : ""),
            Conversation = ConversationMode.Autonomous,
            Tree = tree,
            ReadOnly = change != "writer",
            Continues = change == "continued" ? new(new(Id(89)), "session-1") : null,
        });
        log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Checked.")));
        log.Append(new AttemptEvent.Exited(At, 0, "") { Tree = tree });
        if (change == "handoff")
        {
            log.Append(new AttemptEvent.HandedToTerminal(At, project, "codex resume session-1"));
        }
    }

    private static CommitId Repository(string folder)
    {
        Git(folder, "init", "--quiet");
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllText(Path.Combine(folder, "src", "a.cs"), "original");
        return Commit(folder);
    }

    private static CommitId Commit(string folder)
    {
        Git(folder, "add", "--all");
        Git(folder, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet",
            "-m", "Fixture");
        return new(Git(folder, "rev-parse", "HEAD").Trim());
    }

    private static string Tree(string folder, CommitId commit) => Git(folder, "rev-parse", commit.Hex + "^{tree}").Trim();

    private static string Git(string folder, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), "Git did not exit.");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", error);
        return output;
    }
}
