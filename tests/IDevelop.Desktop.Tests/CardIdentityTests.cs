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
        var lines = node.CardLine;
        var mark = node.ClientMark;

        shell.RunNode("A");
        shell.WaitForCard("A", "Reply");

        Assert.Equal(lines, node.CardLine);
        // The card shows the end of the inspector's model name, so the name it shows is never a different one.
        Assert.Contains(node.CardModel, node.AgentModel);
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
        Assert.Null(canvas.RunScope);
        Assert.False(canvas.CanStartRun);
        Assert.Contains("Nothing to run", canvas.RunWorkflowTip);
    }

    [AvaloniaFact]
    public void Run_Workflow_says_in_its_tooltip_how_many_nodes_it_runs_when_some_keep_their_result()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Task(B, "B", 405));
        f.Install(Reply("s-1", "Done."));
        var shell = f.Open();
        var canvas = shell.Window.ViewModel.Canvas!;
        Assert.Contains("run every node", canvas.RunWorkflowTip);
        Assert.Null(canvas.RunScope);
        Assert.Equal("No result yet", shell.NodeModel("A").OutputSummary);

        shell.RunNode("A");
        shell.WaitForCard("A", "Completed");
        // The folded Output section names the latest result.
        Assert.StartsWith("Result 1 · Current", shell.NodeModel("A").OutputSummary);

        Assert.Contains("run 1 of 2 nodes", canvas.RunWorkflowTip);
        // The button keeps its one name, and the run bar says in a plain line that the run is partial.
        Assert.Equal("Runs 1 node without a current result", canvas.RunScope);
        Assert.True(shell.ShowsAny("RunScopeIdle"));
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
    public void The_inspector_keeps_the_title_and_rests_Output_folded_beside_the_open_conversation()
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
        Assert.False(shell.Has<TextBlock>("ResultSummary") && shell.Find<TextBlock>("ResultSummary").IsEffectivelyVisible);
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
        Assert.Equal(["Agent", "Output", "Input"], Order());

        shell.RunNode("A");
        shell.WaitForCard("A", "Completed");
        shell.Render();

        Assert.Equal(["Agent", "Output", "Input"], Order());
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

    /// <summary>Each model in the app's model lists, and the card's text for it.</summary>
    [Theory]
    // Claude Code's own list. CC says "Claude".
    [InlineData(ClientId.ClaudeCode, "Claude Fable 5.1", "Fable 5.1")]
    [InlineData(ClientId.ClaudeCode, "Claude Opus 5.5", "Opus 5.5")]
    [InlineData(ClientId.ClaudeCode, "Claude Sonnet 5.5", "Sonnet 5.5")]
    [InlineData(ClientId.ClaudeCode, "Claude Haiku 4.5", "Haiku 4.5")]
    // Codex's list. Cx names no family, and "GPT-6.1" holds the version, so the name stays whole.
    [InlineData(ClientId.Codex, "GPT-6.1-Sol", "GPT-6.1-Sol")]
    [InlineData(ClientId.Codex, "GPT-6-Sol", "GPT-6-Sol")]
    [InlineData(ClientId.Codex, "GPT-5.5", "GPT-5.5")]
    [InlineData(ClientId.Codex, "GPT-6.1 Sol", "GPT-6.1 Sol")]
    [InlineData(ClientId.Codex, "GPT-6.1 Terra", "GPT-6.1 Terra")]
    [InlineData(ClientId.Codex, "GPT-5.9 Mini", "GPT-5.9 Mini")]
    // Antigravity's list. AG says "Gemini", and the Claude models it offers keep "Claude".
    [InlineData(ClientId.Antigravity, "Gemini 4 Argon", "4 Argon")]
    [InlineData(ClientId.Antigravity, "Gemini 3.8 Flash", "3.8 Flash")]
    [InlineData(ClientId.Antigravity, "Gemini 3.7 Flash", "3.7 Flash")]
    [InlineData(ClientId.Antigravity, "Gemini 3.6 Flash", "3.6 Flash")]
    [InlineData(ClientId.Antigravity, "Gemini 3.1 Pro", "3.1 Pro")]
    [InlineData(ClientId.Antigravity, "Claude Sonnet 4.6 (Thinking)", "Claude Sonnet 4.6 (Thinking)")]
    [InlineData(ClientId.Antigravity, "Claude Opus 4.6 (Thinking)", "Claude Opus 4.6 (Thinking)")]
    [InlineData(ClientId.Antigravity, "GPT-OSS 120B", "GPT-OSS 120B")]
    // Pi's list. Pi serves many makers, so each name keeps its family.
    [InlineData(ClientId.Pi, "DeepSeek V4.1 Flash", "DeepSeek V4.1 Flash")]
    [InlineData(ClientId.Pi, "DeepSeek V4 Pro", "DeepSeek V4 Pro")]
    [InlineData(ClientId.Pi, "DeepSeek V5", "DeepSeek V5")]
    [InlineData(ClientId.Pi, "GPT-5.5", "GPT-5.5")]
    [InlineData(ClientId.Pi, "GPT-6 Astra", "GPT-6 Astra")]
    [InlineData(ClientId.Pi, "GPT-6 Luna", "GPT-6 Luna")]
    [InlineData(ClientId.Pi, "Qwen 4 Max", "Qwen 4 Max")]
    [InlineData(ClientId.Pi, "GLM 6", "GLM 6")]
    // A model name that is only the family word keeps it.
    [InlineData(ClientId.ClaudeCode, "Claude", "Claude")]
    public void A_card_shows_the_end_of_the_model_name_without_the_family_its_badge_gives(ClientId client, string name, string card) =>
        Assert.Equal(card, CardText.ModelName(client, name));

}
