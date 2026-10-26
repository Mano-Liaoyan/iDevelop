using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A question Claude Code asks while its turn runs shows on the node's card and in its conversation with its choices, and
/// Send answers it with the chosen choice, through the real main window and the fake agent.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class QuestionTests
{
    private static readonly ExecutionSettings Claude = new(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" };

    private static NodeFixture Asking()
    {
        var f = new NodeFixture(AppTempFolder.TaskAt(TestTasks.Design, "Paint", 105, 90, Claude, "Paint the door.", ConversationMode.MayAsk));
        FakeAgents.Install(f.Fakes, ClientId.ClaudeCode, On().Print(FakeAgents.SessionLine(ClientId.ClaudeCode, "s-1"))
            .Print(FakeAgents.ClaudeAsks("Which color?", "Red", "Blue")).ReadLine(Path.Combine(f.Evidence, "answer.json"))
            .Print(FakeAgents.ReplyLines(ClientId.ClaudeCode, "Painted it.")));
        return f;
    }

    private static string Answered(NodeFixture f) => File.ReadAllText(Path.Combine(f.Evidence, "answer.json"));

    private static void Invoke(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IInvokeProvider>()!.Invoke();

    [AvaloniaFact]
    public void The_card_shows_the_question_with_its_choices_and_Send_answers_with_the_chosen_one_while_the_turn_runs()
    {
        using var f = Asking();
        var shell = f.Open();
        var node = shell.NodeModel("Paint");
        node.IsExpanded = true;
        node.RunCommand.Execute(null);
        shell.WaitUntil(() => node.IsAsking, "the agent asks");
        shell.Render();

        Assert.Equal(("The agent asks", "Which color?"), (node.Detail!.Heading, node.Detail.Text));
        Assert.True(node.ShowsStop);
        Assert.False(node.ShowsRetry);
        Assert.False(node.SendCommand.CanExecute(null));
        var choices = shell.Node("Paint").GetVisualDescendants().OfType<ToggleButton>()
            .Where(button => AutomationProperties.GetAutomationId(button) == "CardChoice").ToArray();
        Assert.Equal(["Red", "Blue"], choices.Select(AutomationProperties.GetName));

        shell.Click(choices[1]);
        Assert.True(node.SendCommand.CanExecute(null));
        Invoke(shell.InCard<Button>("Paint", "CardSend"));

        shell.WaitForState("Paint", NodeState.Completed);
        Assert.Contains("\"Which color?\":\"Blue\"", Answered(f));
    }

    [AvaloniaFact]
    public void The_conversation_shows_the_choices_and_its_Send_answers_with_the_person_s_own_words()
    {
        using var f = Asking();
        var shell = f.Open();
        var node = shell.NodeModel("Paint");
        node.RunCommand.Execute(null);
        shell.WaitUntil(() => node.IsAsking, "the agent asks");
        node.OpenConversationCommand.Execute(null);
        shell.WaitUntil(() => shell.Has<Control>("ConversationQuestions"), "the conversation opens");
        shell.Render();

        var conversation = shell.Window.ViewModel.Conversation!;
        Assert.Equal("Sending answers the agent's question with your choices and your words.", conversation.ComposerHint);
        Assert.Equal(["Red", "Blue"], conversation.Questions.Single().Choices.Select(choice => choice.Label));
        Assert.True(shell.Find<Control>("ConversationQuestions").IsEffectivelyVisible);
        shell.Find<TextBox>("ConversationComposer").Text = "Green, please.";
        Invoke(shell.Find<Button>("ConversationSend"));

        shell.WaitForState("Paint", NodeState.Completed);
        Assert.Contains("\"Which color?\":\"Green, please.\"", Answered(f));
        Assert.Equal("", node.Draft);
    }
}
