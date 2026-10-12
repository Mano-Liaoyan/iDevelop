using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A workflow run's turn keeps its task's skill choice (#93), as a standalone start does.</summary>
public sealed class SkillRunTests
{
    [Fact]
    public async Task Run_Workflow_runs_pi_with_exactly_the_chosen_skill_files_and_says_which_one_is_missing()
    {
        var deploy = RunConversationFixture.Agent(A, ConversationMode.Autonomous) with
        {
            Execution = new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high", Skills = ["deploy", "gone"] },
        };
        await using var f = new RunConversationFixture(Graph([deploy]), ClientId.Pi);
        var personal = Directory.CreateDirectory(Path.Combine(f.Evidence, "pi-skills")).FullName;
        SkillListingTests.Skill(personal, "deploy", "Deploys the site.");
        SkillListingTests.Skill(personal, "release", "Cuts a release.");
        f.Answer(A, f.Says(A, 1, "Done."));
        f.Install();
        f.Fakes.Install("pi", FakeAgents.PiRpc(personal), FakeAgents.PiProvider("deepseek", "ready"), FakeAgents.PiProvider("openai-codex", "invalid"),
            FakeRule.On().Choose([("Build A.", FakeRule.On().Scripted(f.Folder(A)))]));
        await f.Open(install: false);
        Assert.IsType<SkillListing.Listed>(await SkillListingTests.ListedAsync(f.Clients, ClientId.Pi, f.Preparation.Git.Folder));

        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(f.Evidence, "A-1.args")))!;
        Assert.Equal(["--no-skills", "--skill", Path.Combine(personal, "deploy", "SKILL.md")],
            arguments.SkipWhile(argument => argument != "--no-skills").TakeWhile(argument => argument != "--session-id"));
        Assert.Contains("Pi runs without gone, which is not installed.", f.Log(A).Record!.Activity.Select(line => line.Text));
    }

    [Fact]
    public async Task A_run_that_starts_before_codex_has_listed_its_skills_waits_for_the_listing_and_goes_on()
    {
        var parser = RunConversationFixture.Agent(A, ConversationMode.Autonomous) with
        {
            Execution = new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high", Skills = ["imagegen"] },
        };
        await using var f = new RunConversationFixture(Graph([parser]));
        var personal = Directory.CreateDirectory(Path.Combine(f.Evidence, "agents-skills")).FullName;
        var remote = Directory.CreateDirectory(Path.Combine(f.Evidence, "remote-plugins")).FullName;
        SkillListingTests.Skill(personal, "imagegen", "Makes images.");
        SkillListingTests.Skill(personal, "release", "Cuts a release.");
        SkillListingTests.Skill(remote, "pages-write-page", "Writes a page.", name: "pages:write-page");
        f.Answer(A, f.Says(A, 1, "Done."));
        f.Install();
        // Codex settles its list only once its remote plugins have loaded, so the run starts long before the listing is in.
        f.Fakes.Install("codex", FakeAgents.CodexModels, FakeAgents.CodexSignedIn, FakeAgents.CodexSkills([personal], [remote], 900),
            FakeRule.On("app-server").Choose([("Build A.", FakeRule.On().Scripted(f.Folder(A)))]));
        await f.Open(install: false);

        await f.Resume();
        await f.UntilStatus(RunStatus.Completed);

        using var thread = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Evidence, "A-1.args") + ".thread.json"));
        Assert.Equal("""{"skills.config":[{"name":"pages:write-page","enabled":false},{"name":"release","enabled":false}]}""",
            thread.RootElement.GetProperty("params").GetProperty("config").GetRawText());
    }

    /// <summary>
    /// A turn that waits for its client's first skill listing, which Codex answers only once the test lets it, stops waiting
    /// as soon as the run's stop is recorded or its window closes, and launches and requests nothing (#85). The listing would
    /// otherwise hold the run in Stopping, or the window open, until it ended on its own, up to 40 seconds later. A start
    /// whose stop ends that wait says the run stopped.
    /// </summary>
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("close")]
    public async Task A_stop_or_a_close_ends_a_turn_s_wait_for_its_skill_listing_and_launches_nothing(string end)
    {
        var parser = RunConversationFixture.Agent(A, ConversationMode.Autonomous) with
        {
            Execution = new ExecutionSettings(ClientId.Codex) { Model = "gpt-6-sol", Reasoning = "high", Skills = ["imagegen"] },
        };
        await using var f = new RunConversationFixture(Graph([parser]));
        var personal = Directory.CreateDirectory(Path.Combine(f.Evidence, "agents-skills")).FullName;
        SkillListingTests.Skill(personal, "imagegen", "Makes images.");
        f.Answer(A, f.Says(A, 1, "Done."));
        f.Install();
        var listed = f.Gate("listed");
        string[] folders = [".agents/skills", ".codex/skills", personal];
        f.Fakes.Install("codex", FakeAgents.CodexModels, FakeAgents.CodexSignedIn,
            FakeRule.On("app-server").Thread("skills/list").WaitForFile(listed).ListSkills("codex", folders)
                .AnswerEach("skills/list", FakeRule.On().ListSkills("codex", folders)),
            FakeRule.On("app-server").Choose([("Build A.", FakeRule.On().Scripted(f.Folder(A)))]));
        await f.Open(install: false);
        // A close waits this long for a start that would not end, so only the end of the wait lets it close sooner.
        f.Runs.LeaveTimeout = Bound;
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runs.Probe = point =>
        {
            if (point == "runner.prepared") prepared.TrySetResult();
        };
        try
        {
            using var stopping = new CancellationTokenSource();
            var cause = new AttemptCause.Initial();
            var start = end == "start"
                ? f.Runs.StartTurn(f.Coordinator.Permit!, new TurnIntent.First(RunOperations.First(f.Preparation.RunId, A, cause), A, cause), stop: stopping.Token)
                : null;
            if (start is null) await f.Resume();
            await prepared.Task.WaitAsync(Bound);
            var soon = TimeSpan.FromSeconds(10);

            switch (end)
            {
                case "start":
                    await stopping.CancelAsync();
                    Assert.Equal(RunProblem.RunStopped, Assert.IsType<TurnStart.Refused>(await start!.WaitAsync(soon)).Reason.Problem);
                    break;
                case "stop":
                    Assert.Equal(TaskState.Starting, f.View.Tasks[A].State);
                    Assert.IsType<RunCommand.Accepted>(await f.Coordinator.Stop(f.Address, f.Preparation.Op()).WaitAsync(Bound));
                    await f.Coordinator.Until(view => view.Status == RunStatus.Stopped).WaitAsync(soon);
                    break;
                default:
                    Assert.Equal(TaskState.Starting, f.View.Tasks[A].State);
                    await f.Runs.DisposeAsync().AsTask().WaitAsync(soon);
                    break;
            }

            Assert.Equal(0, f.Launches(A));
            Assert.Empty(f.Read().Claims);
            Assert.False(File.Exists(Path.Combine(f.AttemptFolder(A), "events.jsonl")) && f.Lines(A, "requested") > 0);
        }
        finally { File.WriteAllText(listed, "go"); }
    }
}
