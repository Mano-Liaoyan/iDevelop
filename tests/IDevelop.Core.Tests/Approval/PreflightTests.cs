using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalFixture;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using Connections = IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>What Run Workflow shows before anything runs (E3d.1).</summary>
public sealed class PreflightTests
{
    [Fact]
    public async Task A_clean_project_previews_its_snapshot_settings_and_inputs_with_HEAD_as_the_only_base()
    {
        var workflow = Chain();
        await using var f = new ApprovalFixture(workflow);
        f.Git.Write($".idp/workflows/{workflow.Id}.json", "{}\n");
        await f.Open();

        var preview = f.Preflight();

        Assert.Equal(workflow, preview.Revision.Snapshot);
        Assert.Equal(Revision.Capture(workflow).Id, preview.Revision.Id);
        Assert.StartsWith("git version ", Assert.IsType<PreflightGit.Ready>(preview.Git).Version);
        Assert.Equal(Head, preview.Base!.Head);
        Assert.Equal("refs/heads/main", preview.Base.Branch);
        Assert.Empty(preview.Base.Changed);
        Assert.Equal<BaseChoice>([BaseChoice.Head], preview.Choices);
        Assert.Empty(preview.Gaps);
        Assert.Null(preview.Active);
        Assert.Equal([A, B, X], preview.Tasks.Select(task => task.Task));
        var a = preview.Tasks[0];
        Assert.Equal(("A", WorkKind.Agent, AgentAccess.Edit, true), (a.Title, a.Kind, a.Access, a.Root));
        Assert.Equal(new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high" }, a.Settings);
        Assert.Equal(ConversationMode.Autonomous, a.Conversation);
        Assert.Empty(a.Inputs);
        var b = preview.Tasks[1];
        Assert.False(b.Root);
        Assert.Equal<PreflightInput>([new(A, ConnectionKind.Dependency)], b.Inputs);
        Assert.Equal((AgentAccess.ReadOnly, true), (preview.Tasks[2].Access, preview.Tasks[2].Root));
        Assert.Equal(new WorktreePolicy(".worktrees", "idp/"), preview.Worktrees);
        Assert.False(Directory.Exists(Path.Combine(f.Project, ".idp", "runs")));
    }

    private static OperationId Command(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000f{n:D3}"));

    /// <summary>Confirming shows the refusal, and nothing is recorded or launched.</summary>
    private static async Task AssertRefused(ApprovalFixture f, RunPreflight preview, BaseChoice choice, ProjectRuns? window = null)
    {
        var refused = Assert.IsType<WorkflowStart.Refused>(await (window ?? f.Runs).StartWorkflow(f.Workflow, new(preview, choice, Command(1))).WaitAsync(Bound));
        Assert.Equal(ApprovalProblem.NotConfirmable, refused.Problem);
        Assert.Empty(f.ApprovedRuns());
        Assert.Empty(ChangedContentTests.Intents(f.Project));
        Assert.Equal(0, f.TotalLaunches);
    }

    [Fact]
    public async Task Configuration_gaps_name_their_tasks_and_keep_the_workflow_from_being_approved()
    {
        var workflow = Graph([Agent(A) with { Execution = null }, Agent(B).WithField("brief", " ")!,
            Agent(X) with { Execution = new(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro" } },
            new TaskDefinition(C, BuiltInBlueprints.Approval) { Title = "Sign off" }], (A, B), (B, C));
        await using var f = new ApprovalFixture(workflow);
        await f.Open();

        var preview = f.Preflight();

        Assert.Equal<PreflightGap>([new PreflightGap.Task(A, new StartProblem.NoAgent()), new PreflightGap.Task(B, new StartProblem.FieldMissing("Brief")),
            new PreflightGap.Task(X, new StartProblem.ClientMissing(ClientId.Pi, "No pi command was found on PATH."))], preview.Gaps);
        Assert.Empty(preview.Choices);
        Assert.Equal((WorkKind.Person, (ExecutionSettings?)null), (preview.Tasks[2].Kind, preview.Tasks[2].Settings));
        await AssertRefused(f, preview, BaseChoice.Head);
    }

    [UnixFact]
    public async Task A_join_needs_Git_2_43_while_a_single_input_does_not()
    {
        await using var f = new ApprovalFixture(Graph([Agent(A), Agent(B), Agent(C), Agent(D)], (A, B), (A, C), (B, D), (C, D)));
        await f.Open();
        f.Runs.GitEnvironment = GitVersion(f, "git version 2.42.0");

        var preview = f.Preflight();

        Assert.Equal<PreflightGap>([new PreflightGap.Join(D, "Joins need Git 2.43 or later. Installed: git version 2.42.0.")], preview.Gaps);
        f.Workflow = Graph([Agent(A), Agent(B)], (A, B));
        Assert.Empty(f.Preflight().Gaps);
    }

    [UnixFact]
    public async Task Git_older_than_2_39_is_a_gap()
    {
        await using var f = new ApprovalFixture(Chain());
        await f.Open();
        f.Runs.GitEnvironment = GitVersion(f, "git version 2.38.1");

        var preview = f.Preflight();

        Assert.Equal(new PreflightGit.Refused(MaterializationProblem.GitVersionUnsupported, "git version 2.38.1"), preview.Git);
        Assert.Null(preview.Base);
        Assert.Equal<PreflightGap>([new PreflightGap.Git(MaterializationProblem.GitVersionUnsupported, "git version 2.38.1")], preview.Gaps);
        await AssertRefused(f, preview, BaseChoice.Head);
    }

    [Fact]
    public async Task A_folder_inside_a_repository_is_not_its_root()
    {
        await using var f = new ApprovalFixture(Chain());
        await f.Open();
        var inner = Directory.CreateDirectory(Path.Combine(f.Project, "inner")).FullName;
        var window = ProjectRuns.Open(inner, await f.Fakes.DiscoverAsync());
        await using (window)
        {
            window.GitEnvironment = f.Git.Environment;
            var preview = window.Preflight(f.Workflow);

            var refused = Assert.IsType<PreflightGit.Refused>(preview.Git);
            Assert.Equal(MaterializationProblem.NotRepositoryRoot, refused.Problem);
            Assert.Equal(Path.GetFullPath(f.Project), Path.GetFullPath(refused.Detail));
            Assert.Single(preview.Gaps);
            await AssertRefused(f, preview, BaseChoice.Head, window);
        }
    }

    [Fact]
    public async Task A_project_without_a_commit_is_a_gap()
    {
        await using var f = new ApprovalFixture(Chain());
        f.GitText("checkout", "-q", "--orphan", "fresh");
        await f.Open();

        var preview = f.Preflight();

        Assert.Null(preview.Base);
        Assert.Equal<PreflightGap>([new PreflightGap.NoCommit()], preview.Gaps);
        await AssertRefused(f, preview, BaseChoice.Snapshot);
    }

    [Fact]
    public async Task An_unmerged_index_offers_only_HEAD()
    {
        await using var f = new ApprovalFixture(Chain());
        f.GitText("checkout", "-q", "-b", "side");
        f.Git.Write("plan.txt", "side\n");
        f.Git.Commit("side");
        f.GitText("checkout", "-q", "main");
        f.Git.Write("plan.txt", "main\n");
        f.Git.Commit("main");
        Assert.NotEqual(0, f.Git.Run(f.Project, "merge", "-q", "side").ExitCode);
        await f.Open();

        var preview = f.Preflight();

        Assert.Equal<string>(["plan.txt"], preview.Base!.Unmerged);
        Assert.Equal<string>(["plan.txt"], preview.Base.Changed);
        Assert.Equal<BaseChoice>([BaseChoice.Head], preview.Choices);
        await AssertRefused(f, preview, BaseChoice.Snapshot);
    }

    [Fact]
    public async Task Submodules_are_listed_with_their_commits()
    {
        await using var f = new ApprovalFixture(Chain());
        using var module = new GitFixture();
        module.Write("m.txt", "m\n");
        var commit = module.Commit("m");
        f.GitText("-c", "protocol.file.allow=always", "submodule", "add", "-q", module.Folder, "m");
        f.Git.Commit("module");
        await f.Open();

        var preview = f.Preflight();

        Assert.Equal<PreflightSubmodule>([new("m", commit, SubmoduleState.Current)], preview.Base!.Submodules);
        Assert.Empty(preview.Base.Changed);
    }

    [Fact]
    public async Task A_matching_standalone_report_is_listed_for_each_base_it_matches()
    {
        await using var f = new ApprovalFixture(Chain());
        f.Answer(X, Reports(X));
        await f.Open();
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runs.Changed += (_, _) =>
        {
            if (f.Runs.Active.IsEmpty && f.Runs.Latest.GetValueOrDefault(X) is { Status: AttemptStatus.Succeeded } done) settled.TrySetResult(done);
        };
        Assert.IsType<StartResult.Started>(f.Runs.Start(f.Workflow.Tasks[X]));
        var attempt = await settled.Task.WaitAsync(Bound);
        f.Git.Write("notes.txt", "notes\n");

        var preview = f.Preflight();

        Assert.Equal<PreflightReport>([new(X, attempt.Id, "X ready.\n", [BaseChoice.Head])], preview.Reusable);
        f.Workflow = Connections.Connect(f.Workflow, A, X, ConnectionKind.Context);
        Assert.Empty(f.Preflight().Reusable);
    }

    [Fact]
    public async Task A_sparse_checkout_without_one_of_its_files_cannot_be_previewed()
    {
        await using var f = new ApprovalFixture(Chain());
        f.GitText("update-index", "--skip-worktree", "plan.txt");
        File.Delete(f.Git.PathOf("plan.txt"));
        await f.Open();

        var preview = f.Preflight();

        Assert.Null(preview.Base);
        Assert.Equal<PreflightGap>([new PreflightGap.Git(MaterializationProblem.DirtyWorktree,
            "The index marks plan.txt skip-worktree, and the work tree has no such file.")], preview.Gaps);
        await AssertRefused(f, preview, BaseChoice.Head);
    }

    [Fact]
    public async Task A_damaged_run_record_of_the_workflow_is_a_gap()
    {
        await using var f = new ApprovalFixture(Chain());
        var run = Guid.Parse("00000000-0000-0000-0000-0000000000dd");
        var folder = Directory.CreateDirectory(Path.Combine(f.Project, ".idp", "runs", f.Workflow.Id.ToString(), run.ToString("D"))).FullName;
        File.WriteAllText(Path.Combine(folder, "events.jsonl"), "{}\n");
        await f.Open();

        var preview = f.Preflight();

        Assert.Equal<PreflightGap>([new PreflightGap.Records($"Run {run:D}: InvalidData.")], preview.Gaps);
        var refused = Assert.IsType<WorkflowStart.Refused>(await f.Runs.StartWorkflow(f.Workflow, new(preview, BaseChoice.Head, Command(2))).WaitAsync(Bound));
        Assert.Equal(ApprovalProblem.NotConfirmable, refused.Problem);
        Assert.Equal(0, f.TotalLaunches);
    }

    private static IReadOnlyDictionary<string, string> GitVersion(ApprovalFixture f, string version)
    {
        var realGit = CommandResolver.Create((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;
        var bin = Directory.CreateDirectory(Path.Combine(f.Evidence, "test-bin")).FullName;
        Executable.Write(Path.Combine(bin, "git"), "#!/bin/sh\nfor arg do last=$arg; done\nif [ \"$last\" = version ]; then printf '%s\\n' '" + version +
            "'; exit 0; fi\nexec '" + realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\n");
        return new Dictionary<string, string>(f.Git.Environment) { ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };
    }
}
