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
}
