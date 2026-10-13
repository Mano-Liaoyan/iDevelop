using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.ConversationFixtures;

namespace IDevelop.Desktop.Tests;

/// <summary>What a task says while its Claude Code turn waits for work in the background, and a subagent's messages (#113).</summary>
public sealed class BackgroundWorkTests : IDisposable
{
    private static readonly BackgroundTask Survey = new("Survey the files", IsSubagent: true);
    private static readonly BackgroundTask Server = new("npm run dev", IsSubagent: false);
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_run_s_task_whose_turn_waits_for_a_background_subagent_says_so_on_its_card()
    {
        Assert.Equal((NodeState.Running, "Waiting for a background subagent"),
            WorkflowRunText.Of(new TaskView(TestTasks.Build, TaskState.Running) { Background = [Server, Survey] }, _ => "A"));
        Assert.Equal((NodeState.Running, "Waiting for background work"),
            WorkflowRunText.Of(new TaskView(TestTasks.Build, TaskState.Running) { Background = [Server] }, _ => "A"));
        Assert.Equal((NodeState.Running, "Running"), WorkflowRunText.Of(new TaskView(TestTasks.Build, TaskState.Running), _ => "A"));
    }

    [Fact]
    public void A_task_s_own_running_attempt_that_waits_for_a_background_subagent_says_so()
    {
        var waiting = Record(A, Launched(1), At(2, new AgentEvent.Succeeded("I'll wait.")), At(3, new AgentEvent.BackgroundTasks([Survey])));

        Assert.Equal("Waiting for a background subagent", RunText.StatusLabel(waiting, elsewhere: false));
        Assert.Equal("Running", RunText.StatusLabel(Record(A, Launched(1)), elsewhere: false));
    }

    [AvaloniaFact]
    public void A_subagent_s_message_shows_under_the_subagent_s_name_in_the_conversation()
    {
        var project = _temp.Seed([AppTempFolder.TaskAt(TestTasks.Design, "Design", 105, 90, CodexHigh, "Draft it.")]);
        WriteAttempt(project, Requested(A, TestTasks.Design, 0, "Draft it."), Launched(1), At(2, new AgentEvent.SessionStarted("thread-1")),
            At(3, new AgentEvent.Message("I'll wait for the survey.") { Id = "m1:0" }),
            At(4, new AgentEvent.Message("Found 3 files.") { Id = "m2:0", Subagent = "general-purpose" }),
            At(5, new AgentEvent.Message("There are 3 files.") { Id = "m3:0" }), At(6, new AgentEvent.Succeeded("There are 3 files.")), Exited(7));
        var shell = Shell.Open(project);
        shell.Click(shell.Header(shell.Node("Design")));
        ControlAutomationPeer.CreatePeerForElement(shell.InView<Button>("OpenConversation")).GetProvider<IInvokeProvider>()!.Invoke();
        var conversation = shell.Window.ViewModel.Conversation!;
        shell.WaitUntil(() => conversation.Idle.IsCompleted && conversation.Items.OfType<MessageItemViewModel>().Count() >= 4, "the conversation shows its history");

        Assert.Equal(["iDevelop", "Codex", "Subagent: general-purpose", "Codex"],
            conversation.Items.OfType<MessageItemViewModel>().Select(message => message.AuthorLabel));
    }
}
