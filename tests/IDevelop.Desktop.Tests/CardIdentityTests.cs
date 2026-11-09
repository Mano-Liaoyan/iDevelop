using Avalonia.VisualTree;
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

        shell.RunNode("A");
        shell.WaitForCard("A", "Needs you");

        Assert.Equal(lines, node.CardLine);
        // The card shows the end of the inspector's model name, so the name it shows is never a different one.
        Assert.Contains(node.CardModel, node.AgentModel);
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
        shell.WaitForCard("A", "Done");

        Assert.Equal(0, canvas.RunWork);
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
        Assert.Equal("No result yet", shell.NodeModel("A").OutputSummary);

        shell.RunNode("A");
        shell.WaitForCard("A", "Done");
        // The folded Output section shows the latest result's own summary.
        Assert.Equal("Done.", shell.NodeModel("A").OutputSummary);

        Assert.Contains("run 1 of 2 nodes", canvas.RunWorkflowTip);
        // The button keeps its one name, and only its tooltip says that the run is partial (ADR 0064).
        Assert.False(shell.ShowsAny("RunScopeIdle"));
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
        var action = shell.Find<Button>("Approve");
        var empty = shell.Bounds(action);

        node.Draft = "Pears.";
        shell.Render();

        Assert.True(action.IsEffectivelyVisible);
        Assert.Equal(empty, shell.Bounds(action));
        Assert.Contains("action", action.Classes);
        Assert.Equal("Accept as result", shell.Window.ViewModel.Conversation!.Dock.ApproveLabel);
    }

    [AvaloniaFact]
    public void A_question_in_plain_prose_gives_no_choices_and_the_dock_offers_Accept_beside_the_composer()
    {
        using var f = new NodeFixture(Task(A, "A", 105, ConversationMode.Chat));
        f.Install(Reply("s-1", "Which fruit?\n1. Pears\n2. Plums"));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("A", NodeState.InputRequired);
        shell.NodeModel("A").OpenConversationCommand.Execute(null);
        shell.Render();
        var model = shell.Window.ViewModel.Conversation!;

        Assert.Equal((ActionDockKind.Reply, true), (model.Dock.Kind, model.Dock.ShowsApprove));
        Assert.DoesNotContain(shell.Window.GetVisualDescendants().OfType<Avalonia.Controls.Control>(), control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) == "ConversationChoice");
        Assert.True(shell.Find<Avalonia.Controls.Control>("ConversationModel").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_inspector_keeps_the_title_and_rests_Output_folded_beside_the_open_conversation()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        f.Install(Reply("s-1", "Done."));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForCard("A", "Done");
        var node = shell.NodeModel("A");

        node.OpenConversationCommand.Execute(null);
        shell.Render();

        Assert.True(shell.Find<TextBox>("TaskTitle").IsEffectivelyVisible);
        Assert.False(shell.Has<TextBlock>("ResultSummary") && shell.Find<TextBlock>("ResultSummary").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_zoom_label_shows_the_true_zoom()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        var canvas = f.Open().Window.ViewModel.Canvas!;

        canvas.ViewportZoom = 1;
        Assert.Equal("100%", canvas.ZoomLabel);
        canvas.ViewportZoom = 0.42;
        Assert.Equal("42%", canvas.ZoomLabel);
    }

    [AvaloniaFact]
    public void A_plate_card_keeps_its_title_and_a_block_card_only_its_mark()
    {
        using var f = new NodeFixture(Task(A, "A", 105));
        var shell = f.Open();
        bool Shows(string id) => shell.Window.GetVisualDescendants().OfType<Control>()
            .Any(control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) == id && control.IsEffectivelyVisible);
        bool ShowsClass(string css) => shell.Window.GetVisualDescendants().OfType<Control>().Any(control => control.Classes.Contains(css) && control.IsEffectivelyVisible);

        shell.Editor.ViewportZoom = 0.5;
        shell.Render();
        Assert.False(Shows("CardModel"), "A Plate card hides the agent row.");
        Assert.True(ShowsClass("plateTitle"), "A Plate card shows its title.");

        shell.Editor.ViewportZoom = 0.2;
        shell.Render();
        Assert.False(ShowsClass("plateTitle"), "A Block card has no text.");
        Assert.True(ShowsClass("blockMark"), "A Block card has its State Mark.");

        shell.Editor.ViewportZoom = 1;
        shell.Render();
        Assert.True(Shows("CardModel"), "A card at 100% shows its model.");
    }

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
