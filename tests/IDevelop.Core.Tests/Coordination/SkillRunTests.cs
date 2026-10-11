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
}
