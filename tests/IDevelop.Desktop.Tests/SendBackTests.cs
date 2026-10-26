using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// An Approval node waits for the person, who approves what the nodes before it handed on, or sends it back with a note
/// that continues their sessions (ADR 0035, ADR 0042).
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class SendBackTests
{
    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId P = TestTasks.Build;

    [AvaloniaFact]
    public void Send_back_takes_a_note_continues_the_nodes_session_with_it_and_the_approval_asks_again_on_the_new_result()
    {
        using var f = new NodeFixture(Plan(A, "A", 105), Approval(P, "Check", 405), Dependency(A, P));
        f.Install(
            Reply("s-a", "A fixed.", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(Path.Combine(f.Evidence, "note.txt"))),
            Reply("s-a", "A ready."));
        var shell = f.Open();
        shell.RunNode("A");
        shell.WaitForState("Check", NodeState.InputRequired);
        shell.Click(shell.Header(shell.Node("Check")));
        var sendBack = shell.InView<Button>("SendBack");
        Assert.False(sendBack.IsEffectivelyEnabled);

        shell.InView<TextBox>("SendBackNote").Text = "Fix the typo.";
        shell.Render();
        shell.Click(shell.InView<Button>("SendBack"));

        var note = Path.Combine(f.Evidence, "note.txt");
        shell.WaitUntil(() => File.Exists(note), "A's session gets the note");
        Assert.Equal("Fix the typo.", File.ReadAllText(note).Trim());
        shell.WaitUntil(() => shell.NodeModel("A").LastReply == "A fixed." && shell.NodeModel("Check").State == NodeState.InputRequired,
            "the approval asks again on the new result");
        Assert.Equal("", shell.InView<TextBox>("SendBackNote").Text);

        shell.Click(shell.InView<Button>("Approve"));

        shell.WaitForState("Check", NodeState.Completed);
        Assert.Contains("A fixed.", Assert.Single(shell.NodeModel("Check").Results).Report);
    }
}
