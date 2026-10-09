using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

/// <summary>
/// A start keeps a node's skill choice (#93) with the skills its client lists in the project: a chosen skill that is no
/// longer installed is left out, and the attempt's activity names it. Antigravity CLI has no choice to keep.
/// </summary>
public sealed class SkillStartTests : IDisposable
{
    private const string Folder = "/project";

    private static readonly SkillOption Tdd = new("tdd", "Test first.") { Path = "/home/me/.pi/agent/skills/tdd/SKILL.md" };
    private static readonly SkillOption ImageGen = new("imagegen", "Makes images.") { Path = "/home/me/.agents/skills/imagegen/SKILL.md" };
    private static readonly SkillListing Listed = new SkillListing.Listed([ImageGen, Tdd]);

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<ClientId> Choosing => new(ClientId.ClaudeCode, ClientId.Codex, ClientId.Pi);

    [Theory]
    [MemberData(nameof(Choosing))]
    public void A_choice_runs_with_its_skills_that_are_still_installed_and_names_the_ones_that_are_not(ClientId client)
    {
        var plan = Allowed(Task(client, ["tdd", "gone", "also-gone"]), Listed);

        Assert.Equal(new SkillLaunch([Tdd], [ImageGen]), plan.Request.Skills, SkillLaunches.Instance);
        Assert.Equal(["also-gone", "gone"], plan.MissingSkills.ToArray());
        Assert.Equal(["also-gone", "gone"], plan.LoggedMissingSkills.ToArray());
    }

    [Theory]
    [MemberData(nameof(Choosing))]
    public void A_choice_waits_for_its_clients_listing_and_a_failed_listing_says_why(ClientId client)
    {
        var task = Task(client, ["tdd"]);

        Assert.Equal(new StartProblem.SkillsChecking(client), Blocked(task, null));
        Assert.Equal(new StartProblem.SkillsChecking(client), Blocked(task, new SkillListing.Checking()));
        Assert.Equal(new StartProblem.SkillsUnlisted(client, "It broke."), Blocked(task, new SkillListing.Unavailable("It broke.")));
    }

    [Theory]
    [MemberData(nameof(Choosing))]
    public void Without_a_choice_a_start_needs_no_listing_and_tells_the_client_nothing(ClientId client)
    {
        var plan = Allowed(Task(client, null), null);

        Assert.Null(plan.Request.Skills);
        Assert.Empty(plan.MissingSkills);
        Assert.True(plan.LoggedMissingSkills.IsDefault);
    }

    [Fact]
    public void Antigravity_runs_with_all_of_its_skills_whatever_the_file_says()
    {
        var plan = Allowed(Task(ClientId.Antigravity, ["tdd"]), null);

        Assert.Null(plan.Request.Skills);
        Assert.Empty(plan.MissingSkills);
    }

    [Fact]
    public void A_codex_choice_passes_through_an_npm_codex_cmd_shim()
    {
        var clients = Ready(ClientId.Codex, isBatchShim: true);

        var verdict = StartCheck.Evaluate(Task(ClientId.Codex, ["tdd"]), "C:/project", clients, skills: Listed);

        Assert.IsType<StartVerdict.Allowed>(verdict);
    }

    [Fact]
    public void An_attempt_names_the_skills_its_turn_runs_without_once_until_they_change()
    {
        var requested = new AttemptEvent.Requested(DateTimeOffset.UnixEpoch, AttemptId.New(), TestTasks.Design, "Design",
            new ExecutionSettings(ClientId.ClaudeCode) { Skills = ["gone", "tdd"] }, "p", "claude", []) { MissingSkills = ["gone"] };
        var record = AttemptReducer.Start(requested);
        Assert.Equal(["Claude Code runs without gone, which is not installed."], record.Activity.Select(line => line.Text));

        record = AttemptReducer.Apply(record, new AttemptEvent.TurnRequested(DateTimeOffset.UnixEpoch, "next", "claude", []) { MissingSkills = ["gone"] });
        Assert.Single(record.Activity);
        record = AttemptReducer.Apply(record, new AttemptEvent.TurnRequested(DateTimeOffset.UnixEpoch, "next", "claude", []) { MissingSkills = ["gone", "tdd"] });
        Assert.Equal("Claude Code runs without gone and tdd, which are not installed.", record.Activity[^1].Text);
        record = AttemptReducer.Apply(record, new AttemptEvent.TurnRequested(DateTimeOffset.UnixEpoch, "next", "claude", []));
        Assert.Empty(record.MissingSkills);
        Assert.Equal(2, record.Activity.Count);
        Assert.Equal("Pi runs without a, b and c, which are not installed.", AttemptReducer.MissingSkillsNote(ClientId.Pi, ["a", "b", "c"]));

        var roundTrip = JsonSerializer.Deserialize<AttemptEvent>(JsonSerializer.Serialize<AttemptEvent>(requested, AttemptLog.Options), AttemptLog.Options);
        Assert.Equal(["gone"], Assert.IsType<AttemptEvent.Requested>(roundTrip).MissingSkills.ToArray());
        Assert.DoesNotContain("missingSkills", JsonSerializer.Serialize<AttemptEvent>(requested with { MissingSkills = default }, AttemptLog.Options),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pi_runs_a_task_with_exactly_its_chosen_skill_files_and_its_activity_names_the_missing_one()
    {
        var project = _temp.Create("project");
        var evidence = _temp.Create("evidence");
        SkillListingTests.Skill(Path.Combine(project, ".pi", "skills"), "deploy", "Deploys the site.");
        SkillListingTests.Skill(Path.Combine(project, ".pi", "skills"), "release", "Cuts a release.");
        var fakes = new FakeClients(_temp.Create("bin"));
        FakeAgents.Install(fakes, ClientId.Pi, On("-p").RecordArguments(Path.Combine(evidence, "arguments.json")).Replay(Fixture.Path("pi-success.jsonl")));
        var clients = await fakes.DiscoverAsync();
        await using var runs = ProjectRuns.Open(project, clients);
        var task = TestNodes.Implement(TestTasks.Design, "Deploy", "Deploy it. Then reply with DONE.",
            execution: new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high", Skills = ["deploy", "gone"] });

        Assert.Equal(new StartResult.Refused(new StartProblem.SkillsChecking(ClientId.Pi)), runs.Start(task));
        await SkillListingTests.ListedAsync(clients, ClientId.Pi, project);
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += (_, _) =>
        {
            if (runs.Latest.GetValueOrDefault(TestTasks.Design) is { Status: not AttemptStatus.Running } record)
            {
                settled.TrySetResult(record);
            }
        };
        Assert.IsType<StartResult.Started>(runs.Start(task));
        var record = await settled.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(AttemptStatus.Succeeded, record.Status);
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(evidence, "arguments.json")))!;
        Assert.Equal(["--no-skills", "--skill", Path.Combine(Path.GetFullPath(project), ".pi", "skills", "deploy", "SKILL.md")],
            arguments.SkipWhile(argument => argument != "--no-skills").TakeWhile(argument => argument != "--session-id"));
        Assert.Equal("Pi runs without gone, which is not installed.", record.Activity[0].Text);
        Assert.Equal(["deploy", "gone"], record.Requested.Skills!);
    }

    private static TaskDefinition Task(ClientId client, ImmutableSortedSet<string>? skills) => TestNodes.Implement(TestTasks.Design, "Design", "Write it.",
        execution: new ExecutionSettings(client) { Model = "m", Reasoning = "high", Skills = skills });

    private static IReadOnlyDictionary<ClientId, ClientStatus> Ready(ClientId client, bool isBatchShim = false) => new Dictionary<ClientId, ClientStatus>
    {
        [client] = new ClientStatus.Ready(new ResolvedCommand(isBatchShim ? "C:/npm/client.cmd" : "/usr/bin/client", isBatchShim), [new ModelOption("m", "M", ["high"])]),
    };

    private static LaunchPlan Allowed(TaskDefinition task, SkillListing? skills) =>
        Assert.IsType<StartVerdict.Allowed>(StartCheck.Evaluate(task, Folder, Ready(task.Execution!.Client), skills: skills)).Plan;

    private static StartProblem Blocked(TaskDefinition task, SkillListing? skills) =>
        Assert.IsType<StartVerdict.Blocked>(StartCheck.Evaluate(task, Folder, Ready(task.Execution!.Client), skills: skills)).Problem;

    private sealed class SkillLaunches : IEqualityComparer<SkillLaunch?>
    {
        public static readonly SkillLaunches Instance = new();

        public bool Equals(SkillLaunch? x, SkillLaunch? y) =>
            x is not null && y is not null && x.Chosen.SequenceEqual(y.Chosen) && x.Others.SequenceEqual(y.Others);

        public int GetHashCode(SkillLaunch? obj) => 0;
    }
}
