using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

/// <summary>A review node's loop through the real main window, with a scripted Codex implementer and Claude Code reviewer.</summary>
[Collection(ProcessCollection.Name)]
public sealed class ReviewNodeTests : IDisposable
{
    private const string ImplementerSession = "019a9d2e-1111-7000-8000-000000000001";
    private const string ReviewerSession = "019a9d2e-2222-7000-8000-000000000002";

    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;
    private readonly string _implementer;
    private readonly string _reviewer;
    private readonly string _gate;
    private readonly string _reading;

    public ReviewNodeTests()
    {
        _fakes = new FakeClients(_temp.Create("bin"));
        _implementer = _temp.Create("implementer");
        _reviewer = _temp.Create("reviewer");
        _gate = Path.Combine(_temp.Create("gate"), "go");
        _reading = Path.Combine(_temp.Create("reading"), "go");
    }

    public void Dispose()
    {
        File.WriteAllText(_gate, "");
        File.WriteAllText(_reading, "");
        _temp.Dispose();
    }

    [AvaloniaFact]
    public void A_review_shows_its_round_and_findings_takes_guidance_and_ends_approved()
    {
        Install(_fakes, ClientId.Codex, Resuming(ClientId.Codex, ImplementerSession).Scripted(_implementer), Fresh(ClientId.Codex).Scripted(_implementer));
        Install(_fakes, ClientId.ClaudeCode, Resuming(ClientId.ClaudeCode, ReviewerSession).Scripted(_reviewer), Fresh(ClientId.ClaudeCode).Scripted(_reviewer));
        Turn(_implementer, 1, ClientId.Codex, ImplementerSession, "Wrote calc.py.", write: "def add(a, b):\n    return a - b\n");
        Turn(_reviewer, 1, ClientId.ClaudeCode, ReviewerSession,
            "Found one.\n\n```idevelop\n{\"status\": \"verdict\", \"verdict\": \"changes\", \"findings\": [{\"id\": \"1\", \"text\": \"add subtracts.\", \"change\": \"Return a + b.\"}]}\n```",
            gate: _reading);
        Turn(_implementer, 2, ClientId.Codex, ImplementerSession,
            "Fixed.\n\n```idevelop\n{\"status\": \"answers\", \"answers\": [{\"id\": \"1\", \"answer\": \"fixed\", \"note\": \"It adds now.\"}]}\n```",
            write: "def add(a, b):\n    return a + b\n", gate: _gate);
        Turn(_reviewer, 2, ClientId.ClaudeCode, ReviewerSession, "Good.\n\n```idevelop\n{\"status\": \"verdict\", \"verdict\": \"approve\", \"findings\": []}\n```");
        var project = _temp.Seed(
            TaskAt(TestTasks.Build, "Add numbers", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" }, "Write calc.py with add(a, b)."),
            new WorkflowEdit.PlaceNode(TestTasks.Review, BuiltInBlueprints.Review, new CanvasPoint(465, 90))
            {
                Title = "Review add",
                Settings = new NodeSettings(new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-haiku-4-5", Reasoning = "high" }, ConversationMode.Autonomous),
            },
            new WorkflowEdit.Connect(new ConnectionKey(TestTasks.Build, TestTasks.Review), ConnectionKind.Dependency));
        Process.Start(new ProcessStartInfo("git", ["init", "-q", project]) { UseShellExecute = false })!.WaitForExit();
        var shell = Shell.Open(project, _fakes.DiscoverAsync().Result);

        shell.Click(shell.Header(shell.Node("Add numbers")));
        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Add numbers", "CardStatus") == "Succeeded", "the subject succeeds");
        shell.Click(shell.Header(shell.Node("Review add")));

        Assert.Equal("Reviewer", shell.Find<TextBlock>("AgentHeading").Text);
        Assert.Equal("Claude Code reviews in plan mode, which edits no file.", shell.Find<TextBlock>("PermissionNote").Text);
        Assert.False(shell.Find<ComboBox>("TaskConversation").IsEffectivelyVisible);
        Assert.Equal("Guide the Review", shell.Find<TextBlock>("ComposerHeading").Text);

        shell.Click(shell.InView<Button>("RunTask"));
        shell.WaitUntil(() => shell.CardText("Review add", "CardStatus") == "Running", "the reviewer reads");
        Assert.Equal("The reviewer reads the change.", shell.Find<TextBlock>("ReviewSummary").Text);
        var underReview = shell.InCard<Border>("Add numbers", "CardUnderReview");
        Assert.Equal((true, "Under review by Review add"), (underReview.IsEffectivelyVisible, ToolTip.GetTip(underReview)));
        File.WriteAllText(_reading, "");
        shell.WaitUntil(() => shell.CardText("Review add", "CardStatus") == "In review", "the review waits for the fix");

        Assert.Equal("Round 1 · 1 open finding", shell.Find<TextBlock>("ReviewSummary").Text);
        Assert.Contains("Round 1 · 1 open finding", (string?)ToolTip.GetTip(shell.InCard<Panel>("Review add", "TaskCard")));
        Assert.Equal(["Finding 1 · Open", "add subtracts.", "Settled by: Return a + b."], Shell.Texts(shell.Find<ItemsControl>("Findings")));
        // The review rests in review while its fix round snapshots the project and starts, off the window's thread.
        shell.WaitUntil(() => shell.CardText("Add numbers", "CardStatus") == "Running", "the fix round starts");
        shell.Click(shell.InView<TextBox>("Composer"));
        shell.Type("Keep it short.");
        shell.Click(shell.InView<Button>("SendMessage"));
        shell.WaitUntil(() => shell.Find<TextBox>("Composer").Text == "", "the guidance is sent");

        File.WriteAllText(_gate, "");

        shell.WaitUntil(() => shell.CardText("Review add", "CardStatus") == "Succeeded", "the review approves");
        Assert.Equal("Approved in round 2.", shell.Find<TextBlock>("ReviewSummary").Text);
        Assert.Equal(["Finding 1 · Resolved", "add subtracts.", "Implementer: It adds now."], Shell.Texts(shell.Find<ItemsControl>("Findings")));
        Assert.Contains("Keep it short.", File.ReadAllText(Path.Combine(_reviewer, "2.stdin")));
        var conversation = Shell.Texts(shell.Find<ItemsControl>("Conversation"));
        Assert.Contains("iDevelop", conversation);
        Assert.DoesNotContain("You", conversation);
    }

    private static void Turn(string folder, int turn, ClientId client, string session, string reply, string? write = null, string? gate = null)
    {
        var steps = FakeRule.On().Print(SessionLine(client, session));
        steps = gate is null ? steps : steps.WaitForFile(gate);
        steps = write is null ? steps : steps.Write("calc.py", write);
        File.WriteAllText(Path.Combine(folder, $"{turn}.json"), steps.Print(ReplyLines(client, reply)).StepsJson());
    }
}
