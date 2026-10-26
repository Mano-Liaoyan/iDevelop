using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using IDevelop.Clients;
using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

/// <summary>
/// A start keeps a node's skill choice (#93) with the skills its client lists in the project: a chosen skill that is no
/// longer installed is left out. Antigravity CLI has no choice to keep.
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
    public void A_choice_runs_with_its_skills_that_are_still_installed(ClientId client)
    {
        var plan = Allowed(Task(client, ["tdd", "gone", "also-gone"]), Listed);

        Assert.Equal(new SkillLaunch([Tdd], [ImageGen]), plan.Request.Skills, SkillLaunches.Instance);
    }

    [Theory]
    [MemberData(nameof(Choosing))]
    public void A_choice_waits_while_its_client_lists_its_skills(ClientId client)
    {
        var task = Task(client, ["tdd"]);

        var waits = $"iDevelop is still listing the skills {ClientCatalog.Name(client)} loads.";
        Assert.Equal(waits, Blocked(task, null));
        Assert.Equal(waits, Blocked(task, new SkillListing.Checking()));
    }

    [Theory]
    [MemberData(nameof(Choosing))]
    public void A_choice_whose_skill_list_could_not_be_read_still_starts_with_its_names_as_they_are(ClientId client)
    {
        var plan = Allowed(Task(client, ["gone", "tdd"]), new SkillListing.Unavailable("It broke."));

        Assert.Equal(new SkillLaunch([new SkillOption("gone", ""), new SkillOption("tdd", "")], []) { Unchecked = true }, plan.Request.Skills, SkillLaunches.Instance);
    }

    [Theory]
    [MemberData(nameof(Choosing))]
    public void Without_a_choice_a_start_needs_no_listing_and_tells_the_client_nothing(ClientId client)
    {
        var plan = Allowed(Task(client, null), null);

        Assert.Null(plan.Request.Skills);
    }

    [Fact]
    public void Antigravity_runs_with_all_of_its_skills_whatever_the_file_says()
    {
        var plan = Allowed(Task(ClientId.Antigravity, ["tdd"]), null);

        Assert.Null(plan.Request.Skills);
    }

    [Fact]
    public void A_codex_choice_passes_through_an_npm_codex_cmd_shim()
    {
        var clients = Ready(ClientId.Codex, isBatchShim: true);

        var (plan, problem) = LaunchPlan.For(Task(ClientId.Codex, ["tdd"]), "C:/project", "p", null, clients, Listed);

        Assert.Null(problem);
        Assert.NotNull(plan);
    }

    [Fact]
    public void A_pi_skill_file_whose_path_holds_a_space_runs_through_pis_node_script_rather_than_its_npm_shim()
    {
        const string file = "C:/Users/John Smith/.pi/agent/skills/deploy/SKILL.md";
        var listed = new SkillListing.Listed([new SkillOption("deploy", "") { Path = file }]);
        var shim = new ResolvedCommand("C:/npm/pi.cmd", IsBatchShim: true);
        var followed = shim with { Script = new NodeScript("C:/npm/node.exe", ["C:/npm/node_modules/pi/cli.js"]) };
        IReadOnlyDictionary<ClientId, ClientStatus> With(ResolvedCommand command) =>
            new Dictionary<ClientId, ClientStatus> { [ClientId.Pi] = new ClientStatus.Ready(command, [new ModelOption("m", "M", ["high"])]) };

        var plan = LaunchPlan.For(Task(ClientId.Pi, ["deploy"]), "C:/project", "p", null, With(followed), listed).Plan!;
        var launch = followed.Launch(plan.Launch.Arguments)!;

        Assert.Equal(("C:/npm/node.exe", "C:/npm/node_modules/pi/cli.js"), (launch.File, launch.Arguments[0]));
        Assert.Contains(file, launch.Arguments);
        // A shim iDevelop cannot follow still says that cmd.exe could misread the path.
        Assert.Equal($"cmd.exe could misread the argument {file}.",
            LaunchPlan.For(Task(ClientId.Pi, ["deploy"]), "C:/project", "p", null, With(shim), listed).Problem);
    }

    [Fact]
    public async Task Pi_runs_a_task_whose_skill_list_could_not_be_read_with_all_of_its_skills_and_says_so()
    {
        var project = _temp.Create("project");
        var evidence = _temp.Create("evidence");
        var fakes = new FakeClients(_temp.Create("bin"));
        fakes.Install("pi",
            On("--mode", "rpc", "--no-session").Answer(
                ("get_available_models", On().Replay(Fixture.Path("pi-rpc-models.jsonl"))),
                ("get_commands", On().Print("""{"id":"idevelop-skills","type":"response","command":"get_commands","success":false,"error":"an extension failed"}"""))).WaitForStdinEnd(),
            FakeAgents.PiProvider("deepseek", "ready"), FakeAgents.PiProvider("openai-codex", "invalid"),
            On("-p").RecordArguments(Path.Combine(evidence, "arguments.json")).Replay(Fixture.Path("pi-success.jsonl")));
        var clients = await fakes.DiscoverAsync();
        var task = TestNodes.Implement(TestTasks.Design, "Deploy", "Deploy it. Then reply with DONE.",
            execution: new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high", Skills = ["deploy"] });

        Assert.IsType<SkillListing.Unavailable>(await SkillListingTests.ListedAsync(clients, ClientId.Pi, project));
        var (outcome, events) = await Turn(clients, project, project, task);

        Assert.IsType<TurnOutcome.Done>(outcome);
        Assert.DoesNotContain("--no-skills", JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(evidence, "arguments.json")))!);
        Assert.Contains(new AgentEvent.Notice("Pi's skill list couldn't be read, so the skill choice couldn't be applied, and Pi runs with all of its skills. " +
            "Pi did not list its skills: an extension failed."), events);
    }

    [Fact]
    public async Task A_turn_in_a_worktree_waits_for_the_project_s_listing_and_runs_with_exactly_the_chosen_skill_files_it_holds()
    {
        var project = _temp.Create("project");
        var evidence = _temp.Create("evidence");
        SkillListingTests.Skill(Path.Combine(project, ".pi", "skills"), "deploy", "Deploys the site.");
        SkillListingTests.Skill(Path.Combine(project, ".pi", "skills"), "release", "Cuts a release.");
        var fakes = new FakeClients(_temp.Create("bin"));
        FakeAgents.Install(fakes, ClientId.Pi, On("-p").RecordArguments(Path.Combine(evidence, "arguments.json")).Replay(Fixture.Path("pi-success.jsonl")));
        var clients = await fakes.DiscoverAsync();
        var task = TestNodes.Implement(TestTasks.Design, "Deploy", "Deploy it. Then reply with DONE.",
            execution: new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high", Skills = ["deploy", "gone"] });

        // The listing has not started, so the node may run, and its turn waits for the listing.
        Assert.Null(new CliAgent(clients, project).Problem(task));
        var (outcome, events) = await Turn(clients, project, _temp.Create("worktree"), task);

        Assert.IsType<TurnOutcome.Done>(outcome);
        Assert.Contains(new AgentEvent.Notice("Pi runs without gone, which is not installed."), events);
        var arguments = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(evidence, "arguments.json")))!;
        Assert.Equal(["--no-skills", "--skill", Path.Combine(Path.GetFullPath(project), ".pi", "skills", "deploy", "SKILL.md")],
            arguments.SkipWhile(argument => argument != "--no-skills").TakeWhile(argument => argument != "--session-id"));
    }

    private static async Task<(TurnOutcome Outcome, AgentEvent[] Events)> Turn(ClientDirectory clients, string project, string folder, TaskDefinition task)
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
        var outcome = await new CliAgent(clients, project).Turn(task, folder, "Deploy it. Then reply with DONE.", null, events.Enqueue, Channel.CreateUnbounded<QuestionReply>().Reader, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));
        return (outcome, [.. events]);
    }

    private static TaskDefinition Task(ClientId client, ImmutableSortedSet<string>? skills) => TestNodes.Implement(TestTasks.Design, "Design", "Write it.",
        execution: new ExecutionSettings(client) { Model = "m", Reasoning = "high", Skills = skills });

    private static IReadOnlyDictionary<ClientId, ClientStatus> Ready(ClientId client, bool isBatchShim = false) => new Dictionary<ClientId, ClientStatus>
    {
        [client] = new ClientStatus.Ready(new ResolvedCommand(isBatchShim ? "C:/npm/client.cmd" : "/usr/bin/client", isBatchShim), [new ModelOption("m", "M", ["high"])]),
    };

    private static LaunchPlan Allowed(TaskDefinition task, SkillListing? skills) =>
        Assert.IsType<LaunchPlan>(LaunchPlan.For(task, Folder, "p", null, Ready(task.Execution!.Client), skills).Plan);

    private static string? Blocked(TaskDefinition task, SkillListing? skills) =>
        LaunchPlan.For(task, Folder, "p", null, Ready(task.Execution!.Client), skills).Problem;

    private sealed class SkillLaunches : IEqualityComparer<SkillLaunch?>
    {
        public static readonly SkillLaunches Instance = new();

        public bool Equals(SkillLaunch? x, SkillLaunch? y) =>
            x is not null && y is not null && x.Chosen.SequenceEqual(y.Chosen) && x.Others.SequenceEqual(y.Others) && x.Unchecked == y.Unchecked;

        public int GetHashCode(SkillLaunch? obj) => 0;
    }
}
