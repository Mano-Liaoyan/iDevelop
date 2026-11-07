using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Conversation;
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
        Assert.Equal("AG", mark);
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
        f.Install(Reply("s-1", "Pears are ripe."));
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
        Assert.Equal("Use this message as the result", shell.Window.ViewModel.Conversation!.ApproveLabel);
    }

    [AvaloniaFact]
    public void An_open_question_takes_no_card_and_the_composer_answers_it()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which fruit?"));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.InputRequired);
        shell.NodeModel("A").OpenConversationCommand.Execute(null);
        shell.Render();
        var model = shell.Window.ViewModel.Conversation!;

        Assert.Equal((AttentionCard.Reply, false, false), (model.Card, model.ShowsCard, model.ShowsCardApprove));
        Assert.True(model.WaitsForWords);
        var question = Assert.Single(model.Items.OfType<Conversation.MessageItemViewModel>(), message => message.IsAsking);
        Assert.True(question.ShowsAuthor);
        Assert.EndsWith("Waiting for your reply", question.AuthorLine);
        Assert.True(shell.Find<Avalonia.Controls.Control>("ConversationModel").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_inspector_keeps_the_title_and_the_result_beside_the_open_conversation()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(Reply("s-1", "Done."));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForCard("A", "Completed");
        var node = shell.NodeModel("A");

        node.OpenConversationCommand.Execute(null);
        shell.Render();

        Assert.True(shell.Find<TextBox>("TaskTitle").IsEffectivelyVisible);
        Assert.Contains("Done.", shell.Find<TextBlock>("ResultSummary").Text);
        Assert.True(shell.Find<TextBlock>("ResultSummary").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_inspector_keeps_one_section_order_in_every_state()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(Reply("s-1", "Done."));
        var shell = f.Open();
        shell.Click(shell.Header(shell.Node("A")));
        shell.Render();
        string[] Order() => [.. new[] { "Agent", "Input", "Output" }.OrderBy(section => shell.Bounds(shell.Section(section)).Top)];
        Assert.Equal(["Agent", "Input", "Output"], Order());

        shell.RunNode("A");
        shell.WaitForCard("A", "Completed");
        shell.Render();

        Assert.Equal(["Agent", "Input", "Output"], Order());
    }

    [AvaloniaFact]
    public void The_zoom_label_calls_the_reading_size_100_percent()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        var canvas = f.Open().Window.ViewModel.Canvas!;

        canvas.ViewportZoom = ZoomTiers.ActualSize;

        Assert.Equal("100%", canvas.ZoomLabel);
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
