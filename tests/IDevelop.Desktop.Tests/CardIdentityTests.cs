using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.AgyTurn;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>A card keeps its agent in every state, Run Workflow says how far it reaches, and the open conversation holds what the inspector then leaves out.</summary>
[Collection(ProcessCollection.Name)]
public sealed class CardIdentityTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId B = TestTasks.Build;

    [AvaloniaFact]
    public void A_card_keeps_its_model_and_client_letter_when_its_state_changes()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which file?"));
        var shell = f.Open();
        var node = shell.NodeModel("A");
        var lines = node.CardLines;
        var mark = node.ClientMark;

        shell.RunNode("A");
        shell.WaitForCard("A", "Reply");

        Assert.Equal(lines, node.CardLines);
        Assert.Equal("A", mark);
        Assert.Equal(mark, node.ClientMark);
        Assert.Equal("Antigravity", node.CardClient);
    }

    [AvaloniaFact]
    public void Run_Workflow_says_its_reach_in_its_tooltip_and_goes_quiet_with_nothing_to_run()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(Reply("s-1", "Done."));
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.Contains("run every node", canvas.RunWorkflowTip);
        Assert.True(canvas.CanStartRun);

        shell.RunNode("A");
        shell.WaitForCard("A", "Completed");

        Assert.Equal(0, canvas.RunWork);
        Assert.False(canvas.CanStartRun);
        Assert.Contains("Nothing to run", canvas.RunWorkflowTip);
    }

    [AvaloniaFact]
    public void The_replys_action_stays_a_button_while_the_person_writes()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which fruit?"));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.InputRequired);
        var node = shell.NodeModel("A");
        node.OpenConversationCommand.Execute(null);
        shell.Render();
        var action = shell.Find<Button>("ConversationApprove");
        var empty = shell.Bounds(action);

        node.Draft = "Pears.";
        shell.Render();

        Assert.True(action.IsEffectivelyVisible);
        Assert.Equal(empty, shell.Bounds(action));
        Assert.Contains("action", action.Classes);
        Assert.Equal("Accept the agent's message as the result", shell.Window.ViewModel.Conversation!.ApproveLabel);
    }

    [AvaloniaFact]
    public void The_open_conversation_holds_the_result_so_the_output_section_lists_only_where_it_goes()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(Reply("s-1", "Done."));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForCard("A", "Completed");
        var node = shell.NodeModel("A");
        Assert.True((node.ShowsResults, node.ShowsOutputSection) == (true, true));

        node.OpenConversationCommand.Execute(null);
        shell.Render();

        Assert.False(node.ShowsResults);
        Assert.False(node.ShowsOutputSection);
    }

    [Theory]
    [InlineData("@@ -1 +1 @@", DiffLineKind.Hunk)]
    [InlineData("+added", DiffLineKind.Added)]
    [InlineData("-gone", DiffLineKind.Deleted)]
    [InlineData(" kept", DiffLineKind.Context)]
    public void A_diff_line_says_what_it_is(string text, DiffLineKind kind) => Assert.Equal(kind, DiffLine.Of(text).Kind);

    [Theory]
    [InlineData("Sonnet 5.5", "Sonnet 5.5|Sonnet")]
    [InlineData("GPT-6.1 Terra", "GPT-6.1 Terra|GPT-6.1|GPT-6.1")]
    public void A_far_card_falls_back_to_the_model_family(string model, string lines)
    {
        var shown = CardText.FarLines(CardText.ModelLines(model, model));
        Assert.Equal(lines.Split('|').Distinct().Order(), shown.Order());
    }
}
