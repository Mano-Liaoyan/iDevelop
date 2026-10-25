using IDevelop.Engine;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// A review's reject goes along a feedback edge: its findings continue the predecessor's session, and the reviewer runs
/// again on the new result. After the review's round limit the reviewer waits for the person.
/// </summary>
public sealed class FeedbackTests : IAsyncLifetime
{
    private const string Approve = "Looks right.\n\n```idevelop\n{\"status\": \"verdict\", \"verdict\": \"approve\", \"findings\": []}\n```";

    private const string Reject =
        "One problem.\n\n```idevelop\n{\"status\": \"verdict\", \"verdict\": \"changes\", \"findings\": [{\"id\": \"1\", \"severity\": \"high\", " +
        "\"location\": \"a.txt:1\", \"text\": \"The line says A.\", \"change\": \"Write A fixed.\"}]}\n```";

    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    [Fact]
    public async Task An_approving_review_completes_with_its_verdict_and_hands_its_subject_s_commit_on()
    {
        var (a, r, c) = (_f.Node("A"), _f.Review("Check"), _f.Node("C"));
        _f.Connect(a, r);
        _f.Connect(r, c);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
            (Fresh("Check"), Reply("s-r", Approve, On().CapturePrompt(_f.File("r.txt")))),
            (Fresh("C"), Reply("s-c", "Report C", On().Write("c.txt", "C\n")))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until<NodeState.Completed>(engine, c);
        var review = Assert.Single(engine.History(r));
        Assert.True(review.Verdict!.Approves);
        Assert.Null(review.Commit);
        Assert.Contains("Report A", _f.Prompt("r.txt"), StringComparison.Ordinal);
        Assert.Contains("+A", _f.Prompt("r.txt"), StringComparison.Ordinal);
        Assert.Contains("\"severity\": \"high\"", _f.Prompt("r.txt"), StringComparison.Ordinal);
        Assert.Equal([engine.History(a)[0].Commit!], _f.Parents(engine.History(c)[0].Commit!));
    }

    [Fact]
    public async Task A_reject_continues_the_subject_s_session_with_the_findings_and_the_review_runs_again_on_its_new_result()
    {
        var (a, r) = (_f.Node("A"), _f.Review("Check"));
        _f.Connect(a, r);
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(_f.File("fix.txt")).Write("a.txt", "A fixed\n")),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
                (Fresh("Check"), _f.Turns("check",
                    Reply("s-r", Reject),
                    Reply("s-r", Approve, On().CapturePrompt(_f.File("second.txt")))))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until(engine, r, state => state is NodeState.Completed && engine.History(r).Count == 1);
        Assert.Equal(2, engine.History(a).Count);
        Assert.Contains("The line says A.", _f.Prompt("fix.txt"), StringComparison.Ordinal);
        Assert.Contains("a.txt:1", _f.Prompt("fix.txt"), StringComparison.Ordinal);
        Assert.Contains("Write A fixed.", _f.Prompt("fix.txt"), StringComparison.Ordinal);
        Assert.Contains("Report A fixed", _f.Prompt("second.txt"), StringComparison.Ordinal);
        Assert.Contains("+A fixed", _f.Prompt("second.txt"), StringComparison.Ordinal);
        Assert.Single(engine.Entries(r).OfType<NodeEntry.Feedback>());
    }

    [Fact]
    public async Task A_fix_round_makes_the_subject_s_other_successor_stale_and_it_runs_again_on_the_new_result()
    {
        var (a, r, d) = (_f.Node("A"), _f.Review("Check"), _f.Node("D"));
        _f.Connect(a, r);
        _f.Connect(a, d);
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a")),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("D"), Reply("s-d", "Report D")),
                (Fresh("Check"), _f.Turns("check", Reply("s-r", Reject), Reply("s-r", Approve)))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until(engine, r, state => state is NodeState.Completed && engine.History(r).Count == 1);
        Assert.Equal(2, engine.History(a).Count);
        // D's turn on A's first result either completed and went stale, or stopped when A's fix started.
        await Until(engine, d, state => state is NodeState.Completed && engine.History(d)[^1].Basis.Inputs.Single().Result == engine.History(a)[^1].Id);
    }

    [Fact]
    public async Task After_three_rounds_the_review_waits_and_a_send_back_goes_to_its_subject_as_a_reject_would()
    {
        var (a, r) = (_f.Node("A"), _f.Review("Check"));
        _f.Connect(a, r);
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(_f.File("fix.txt"))),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("Check"), _f.Turns("check", Reply("s-r", Reject), Reply("s-r", Reject), Reply("s-r", Reject), Reply("s-r", Approve)))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        Assert.Equal(new NodeState.InputRequired(NodeState.RoundLimit), await Until<NodeState.InputRequired>(engine, r));
        Assert.Equal(3, engine.Entries(r).OfType<NodeEntry.Feedback>().Count());
        Assert.Equal(4, engine.History(a).Count);
        Assert.Equal(new NodeState.Completed(), engine.State(a));

        await engine.SendBack(r, "Handle empty input.");

        await Until(engine, r, state => state is NodeState.Completed && engine.History(r).Count == 1);
        Assert.Equal("Handle empty input.", _f.Prompt("fix.txt"));
        Assert.Equal(5, engine.History(a).Count);
    }

    [Fact]
    public async Task At_its_limit_a_person_s_approval_completes_the_review_and_hands_the_subject_s_result_on()
    {
        var (a, r, c) = (_f.Node("A"), _f.Review("Check"), _f.Node("C"));
        _f.Connect(a, r);
        _f.Connect(r, c);
        _f.Workflow = _f.Workflow.Must(new WorkflowEdit.SetRoundLimit(r, 1));
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a")),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("Check"), Reply("s-r", Reject)),
                (Fresh("C"), Reply("s-c", "Report C", On().CapturePrompt(_f.File("c.txt"))))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.InputRequired>(engine, r);

        await engine.Approve(r);

        await Until<NodeState.Completed>(engine, c);
        Assert.Equal("### A\n\nReport A fixed", Assert.Single(engine.History(r)).Report);
        Assert.Contains("Report A fixed", _f.Prompt("c.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_after_a_Merge_node_sends_its_findings_to_the_Merge_node_s_session()
    {
        var (a, b, m, r) = (_f.Node("A"), _f.Node("B"), _f.Merge("M"), _f.Review("Check"));
        _f.Connect(a, m);
        _f.Connect(b, m);
        _f.Connect(m, r);
        _f.Install(
            Reply("s-m", "Merged again", FakeAgents.Resuming(ClientId.Antigravity, "s-m").CapturePrompt(_f.File("fix.txt"))),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
                (Fresh("B"), Reply("s-b", "Report B", On().Write("b.txt", "B\n"))),
                (Fresh("M"), Reply("s-m", "Merged")),
                (Fresh("Check"), _f.Turns("check", Reply("s-r", Reject), Reply("s-r", Approve)))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until(engine, r, state => state is NodeState.Completed && engine.History(r).Count == 1);
        Assert.Contains("The line says A.", _f.Prompt("fix.txt"), StringComparison.Ordinal);
        Assert.Equal(2, engine.History(m).Count);
        Assert.Single(engine.History(a));
        Assert.Single(engine.History(b));
    }
}
