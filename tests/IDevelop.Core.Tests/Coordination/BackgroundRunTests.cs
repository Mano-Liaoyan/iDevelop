using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Core.Tests.Coordination;

/// <summary>A run's task whose Claude Code turn waits for a background subagent (#113).</summary>
public sealed class BackgroundRunTests
{
    [Fact]
    public async Task A_run_s_task_names_the_background_subagent_its_turn_waits_for_until_the_client_exits()
    {
        await using var f = new RunConversationFixture(Graph([RunConversationFixture.Agent(A, ConversationMode.Autonomous, ClientId.ClaudeCode)]), ClientId.ClaudeCode);
        f.Answer(A, FakeRule.On().Print(SessionLine(ClientId.ClaudeCode, "session-1")).Print(ClaudeBackgroundStart())
            .WaitForStdinEnd().WaitForFile(f.Gate("subagent")).Print(ClaudeBackgroundEnd("session-1", "Found 3 files.", "A ready.")));
        await f.Open();
        f.Runs.SuccessExitTime = TimeSpan.FromMilliseconds(200);
        await f.Resume();

        var waiting = await f.Until(view => view.Tasks[A].Background.Length > 0);
        Assert.Equal((TaskState.Running, new BackgroundTask("Survey the files", IsSubagent: true)), (waiting.Tasks[A].State, Assert.Single(waiting.Tasks[A].Background)));
        await System.Threading.Tasks.Task.Delay(500);
        Assert.Equal(TaskState.Running, f.View.Tasks[A].State);

        f.Open("subagent");
        await f.UntilStatus(RunStatus.Completed);
        Assert.Empty(f.View.Tasks[A].Background);
        Assert.Equal("A ready.", f.Log(A).Record!.Result);
    }
}
