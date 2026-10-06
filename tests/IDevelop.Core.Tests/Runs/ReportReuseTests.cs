using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using IDevelop.Execution;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

[Collection(IDevelop.TestSupport.ProcessCollection.Name)]
public sealed class ReportReuseTests
{
    [Fact]
    public async Task A_report_from_ProjectRuns_standalone_capture_can_be_reused_with_its_recorded_source()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task(model: "gpt-6-sol")));
        using var temp = new TempFolder();
        var commit = Repository(f.Project);
        var fakes = new FakeClients(temp.Create("bin"));
        FakeAgents.Install(fakes, ClientId.Codex,
            FakeAgents.Fresh(ClientId.Codex).Replay(Fixture.Path("codex-success.jsonl")));
        await using var runs = ProjectRuns.Open(f.Project, await fakes.DiscoverAsync());
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (runs.Active.IsEmpty && runs.Latest.GetValueOrDefault(T) is { Status: not AttemptStatus.Running } attempt)
            {
                settled.TrySetResult(attempt);
            }
        };

        var started = Assert.IsType<StartResult.Started>(runs.Start(f.Workflow.Tasks[T]));
        var attempt = await settled.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal((started.Attempt.Id, AttemptStatus.Succeeded, "DONE"), (attempt.Id, attempt.Status, attempt.Result));
        f.Approve(commit);

        Assert.IsType<RunDecision.Created>(f.Store.ReuseReport(W, Run, f.Op(), T,
            new AttemptSource.Standalone(T, started.Attempt.Id), f.Op()));

        var result = f.Read().CurrentResults[T];
        Assert.Equal("DONE", result.Report);
        Assert.Equal(new AttemptSource.Standalone(T, started.Attempt.Id), Assert.IsType<ResultOrigin.Reused>(result.Origin).Source);
    }

    [Fact]
    public void Explicit_matching_standalone_reuse_copies_one_report_and_source()
    {
        using var f = new RunFixtures();
        var commit = Repository(f.Project);
        f.Approve(commit);
        WriteStandalone(f.Project, commit);
        var source = new AttemptSource.Standalone(T, new(Id(90)));
        var confirmation = f.Op();
        var first = Assert.IsType<RunDecision.Created>(f.Store.ReuseReport(W, Run, f.Op(), T, source, confirmation));
        var repeated = Assert.IsType<RunDecision.Existing>(f.NewStore().ReuseReport(W, Run, f.Op(), T, source, confirmation));
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
        if (change == "missingObject")
        {
            receivingCommit = new(new string('9', 40));
        }
        f.Approve(receivingCommit);
        Assert.Equal(RunProblem.ReuseUnverifiable, Problem(f.Store.ReuseReport(W, Run, f.Op(), T,
            new(T, new(Id(90))), f.Op())));
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
        Assert.Equal(RunProblem.ReuseUnverifiable, Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), f.Op())));
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
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), default)));
        Assert.IsType<RunDecision.Created>(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), f.Op()));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("Checked.", f.Read().CurrentResults[T].Report);
    }

    [Fact]
    public void Reuse_rejects_a_log_copied_under_another_attempt_id()
    {
        using var f = new RunFixtures();
        var commit = Repository(f.Project);
        f.Approve(commit);
        WriteStandalone(f.Project, commit);
        var original = StandalonePath(f.Project);
        var copied = AttemptLog.FolderOf(DataFolder.Attempts(f.Project), T, new(Id(91)));
        Directory.CreateDirectory(copied);
        File.Copy(original, Path.Combine(copied, "events.jsonl"));
        Assert.Equal(RunProblem.ReuseUnverifiable,
            Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(91))), f.Op())));
    }

    [Fact]
    public void Reuse_rejects_a_log_request_naming_another_task()
    {
        using var f = new RunFixtures();
        var commit = Repository(f.Project);
        f.Approve(commit);
        WriteStandalone(f.Project, commit);
        ChangeRequest(f.Project, request => request["task"] = "00000000-0000-0000-0000-000000000003");
        Assert.Equal(RunProblem.ReuseUnverifiable,
            Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), f.Op())));
    }

    [Fact]
    public void Reuse_uses_the_approved_base_instead_of_a_caller_chosen_commit()
    {
        using var f = new RunFixtures();
        var first = Repository(f.Project);
        WriteStandalone(f.Project, first);
        File.WriteAllText(Path.Combine(f.Project, "src", "a.cs"), "later");
        var later = Commit(f.Project);
        f.Approve(later);
        Assert.Equal(RunProblem.ReuseUnverifiable,
            Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), f.Op())));
        Assert.Equal(later, f.Read().Base.Commit);
    }

    [Fact]
    public void Unparseable_capture_keeps_standalone_history_but_rejects_reuse()
    {
        using var f = new RunFixtures();
        var commit = Repository(f.Project);
        f.Approve(commit);
        WriteStandalone(f.Project, commit);
        ChangeRequest(f.Project, request => request["standaloneCapture"] =
            JsonNode.Parse("""{"definition":{},"inputs":""}"""));
        var record = AttemptLog.ReadAttempt(DataFolder.Attempts(f.Project), T, new(Id(90)));
        Assert.Equal(AttemptStatus.Succeeded, record?.Status);
        Assert.Equal("Checked.", record?.Result);
        Assert.Equal(RunProblem.ReuseUnverifiable,
            Problem(f.Store.ReuseReport(W, Run, f.Op(), T, new(T, new(Id(90))), f.Op())));
    }

    private static string StandalonePath(string project) =>
        Path.Combine(AttemptLog.FolderOf(DataFolder.Attempts(project), T, new(Id(90))), "events.jsonl");

    private static void ChangeRequest(string project, Action<JsonNode> change)
    {
        var path = StandalonePath(project);
        var lines = File.ReadAllLines(path);
        var request = JsonNode.Parse(lines[0])!;
        change(request);
        lines[0] = request.ToJsonString();
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    private static void WriteStandalone(string project, CommitId commit, string change = "")
    {
        var definition = Task(model: change == "definition" ? "m2" : "m1");
        var tree = Tree(project, commit);
        using var log = AttemptLog.Create(DataFolder.Attempts(project), new AttemptEvent.Requested(At, new(Id(90)), T,
            "Plan", definition.Execution!, change == "prompt" ? "Different" : "Inspect", "codex", [])
        {
            StandaloneCapture = change == "old" ? null : JsonSerializer.SerializeToElement(
                new StandaloneCapture(definition, change == "inputs" ? "Declared" : ""), RunJournal.Options),
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
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
