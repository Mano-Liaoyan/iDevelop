using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Desktop.Canvas;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.NodeFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// A Review node through the engine (ADR 0035, ADR 0040, ADR 0042): its verdict's findings show in the inspector, a reject
/// goes back to its subject, which continues its session, and at its round limit it offers Approve and Send back.
/// </summary>
[Collection(ProcessCollection.Name)]
public sealed class ReviewNodeTests
{
    private const string Approves = "Looks right.\n\n```idevelop\n{\"status\": \"verdict\", \"verdict\": \"approve\", \"findings\": []}\n```";

    private const string Rejects =
        "One problem.\n\n```idevelop\n{\"status\": \"verdict\", \"verdict\": \"changes\", \"findings\": [{\"id\": \"1\", \"severity\": \"high\", " +
        "\"location\": \"a.txt:1\", \"text\": \"The line says A.\", \"change\": \"Write A fixed.\"}]}\n```";

    private static readonly TaskId A = TestTasks.Design;
    private static readonly TaskId R = TestTasks.Review;

    [AvaloniaFact]
    public void An_approving_review_completes_and_its_card_says_so()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Review(R, "Check", 405), Dependency(A, R));
        f.Install(On().Choose((Fresh("A"), Reply("s-a", "A ready.", On().Write("a.txt", "A\n"))), (Fresh("Check"), Reply("s-r", Approves))));
        var shell = f.Open();

        shell.Click(shell.Find<Button>("RunWorkflow"));

        shell.WaitForState("Check", NodeState.Completed);
        Assert.Equal("Approved", shell.NodeModel("Check").ReviewSummary);
        Assert.Empty(shell.NodeModel("Check").Findings);
    }

    [AvaloniaFact]
    public void At_its_round_limit_a_rejecting_review_shows_its_findings_and_offers_Approve_and_Send_back()
    {
        using var f = new NodeFixture(Task(A, "A", 105), Review(R, "Check", 405), Dependency(A, R), new WorkflowEdit.SetRoundLimit(R, 1));
        f.Install(
            Reply("s-a", "A fixed.", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(Path.Combine(f.Evidence, "fix.txt"))),
            On().Choose((Fresh("A"), Reply("s-a", "A ready.", On().Write("a.txt", "A\n"))), (Fresh("Check"), Reply("s-r", Rejects))));
        var shell = f.Open();

        shell.Click(shell.Find<Button>("RunWorkflow"));

        shell.WaitUntil(() => shell.NodeModel("Check").EngineState is IDevelop.Engine.NodeState.InputRequired { Why: IDevelop.Engine.NodeState.RoundLimit },
            "the review reaches its round limit", () => shell.NodeModel("Check").StatusLabel);
        Assert.Contains("The line says A.", File.ReadAllText(Path.Combine(f.Evidence, "fix.txt")));
        Assert.Equal("Input required · round limit", shell.CardText("Check", "CardStatus"));
        shell.Click(shell.Header(shell.Node("Check")));
        Assert.Equal("1 open finding", shell.InView<TextBlock>("ReviewSummary").Text);
        var finding = Assert.Single(shell.NodeModel("Check").Findings);
        Assert.Equal(("Finding 1", "High", "a.txt:1", "The line says A."), (finding.Heading, finding.Severity, finding.Location, finding.Reviewer));
        Assert.True(shell.InView<Button>("Approve").IsEffectivelyVisible);
        Assert.True(shell.InView<Button>("SendBack").IsEffectivelyVisible);

        shell.Click(shell.InView<Button>("Approve"));

        shell.WaitForState("Check", NodeState.Completed);
    }
}
