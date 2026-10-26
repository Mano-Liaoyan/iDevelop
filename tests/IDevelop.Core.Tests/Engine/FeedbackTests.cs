using IDevelop.Engine;
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
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(_f.File("fix.txt")).Write("b.txt", "B\n")),
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
        Assert.Contains("+A", _f.Prompt("second.txt"), StringComparison.Ordinal);
        Assert.Contains("+B", _f.Prompt("second.txt"), StringComparison.Ordinal);
        Assert.Single(engine.Entries(r).OfType<NodeEntry.Feedback>());
        // The fix continues in the same worktree, so its commit follows the first one and keeps its file.
        var fixedCommit = engine.History(a)[1].Commit!;
        Assert.Equal([engine.History(a)[0].Commit!], _f.Parents(fixedCommit));
        Assert.Equal(("A\n", "B\n"), (_f.Files(fixedCommit)["a.txt"], _f.Files(fixedCommit)["b.txt"]));
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

    [Fact]
    public async Task A_send_back_to_a_predecessor_without_a_session_starts_it_fresh_with_the_note()
    {
        var (a, r, p) = (_f.Node("A"), _f.Review("Check"), _f.Approval("Approve"));
        _f.Connect(a, r);
        _f.Connect(r, p);
        _f.Workflow = _f.Workflow.Must(new WorkflowEdit.SetRoundLimit(r, 1));
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a")),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("Check"), _f.Turns("check", Reply("s-r", Reject), Reply("s-r", Approve, On().CapturePrompt(_f.File("again.txt")))))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.InputRequired>(engine, r);
        await engine.Approve(r);
        await Until<NodeState.InputRequired>(engine, p);

        await engine.SendBack(p, "Check empty input.");

        await Until(engine, r, state => state is NodeState.Completed && engine.History(r).Count == 2);
        Assert.Contains("Check empty input.", _f.Prompt("again.txt"), StringComparison.Ordinal);
        Assert.Equal(new NodeState.InputRequired(NodeState.Approval), await Until<NodeState.InputRequired>(engine, p));
    }

    [Fact]
    public async Task A_fresh_session_of_the_subject_starts_a_new_loop_so_its_rounds_count_from_zero()
    {
        var (a, r) = (_f.Node("A"), _f.Review("Check"));
        _f.Connect(a, r);
        _f.Workflow = _f.Workflow.Must(new WorkflowEdit.SetRoundLimit(r, 1));
        _f.Install(
            FakeAgents.Resuming(ClientId.Antigravity, "s-a").Print(FakeAgents.SessionLine(ClientId.Antigravity, "s-a")).WaitForFile(_f.File("never")),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("Check"), _f.Turns("check", Reply("s-r", Reject), Reply("s-r", Approve)))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until(engine, a, state => state is NodeState.Running && engine.Entries(r).OfType<NodeEntry.Feedback>().Any());
        await engine.Stop(a);

        await engine.Retry(a);

        await Until(engine, r, state => state is NodeState.Completed && engine.History(r).Count == 1);
    }

    [Fact]
    public async Task A_reject_reaches_the_subject_even_when_a_message_to_the_reviewer_waits()
    {
        var (a, r) = (_f.Node("A"), _f.Review("Check"));
        _f.Connect(a, r);
        var gate = _f.File("gate");
        _f.Install(
            Reply("s-r", "Names look fine.", FakeAgents.Resuming(ClientId.Antigravity, "s-r")),
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(_f.File("fix.txt"))),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("Check"), _f.Turns("check", Reply("s-r", Reject, On().WaitForFile(gate)), Reply("s-r", Approve)))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.Running>(engine, r);
        await engine.Send(r, "Also check names.", interrupt: false);

        File.WriteAllText(gate, "");

        await Until(engine, a, state => state is NodeState.Completed && engine.History(a).Count == 2);
        Assert.Contains("The line says A.", _f.Prompt("fix.txt"), StringComparison.Ordinal);
        Assert.Contains(new NodeEntry.PersonMessage("Also check names."), engine.Entries(r));
    }

    [Fact]
    public async Task A_subject_edited_during_its_review_gets_no_fix_turn_and_stays_stale_for_the_person()
    {
        var (a, r) = (_f.Node("A"), _f.Review("Check"));
        _f.Connect(a, r);
        var gate = _f.File("gate");
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").CapturePrompt(_f.File("fix.txt"))),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A")),
                (Fresh("Check"), Reply("s-r", Reject, On().WaitForFile(gate)))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.Running>(engine, r);
        engine.Update(_f.Workflow = _f.Workflow.Must(new WorkflowEdit.SetField(a, "instructions", "Do A better.")));

        File.WriteAllText(gate, "");

        await Eventually(() => engine.Entries(r).OfType<NodeEntry.Feedback>().Any());
        Assert.Equal(new NodeState.Stale(), engine.State(a));
        Assert.Single(engine.History(a));
        Assert.False(File.Exists(_f.File("fix.txt")));
    }

    [Fact]
    public async Task Feedback_goes_only_to_a_predecessor_that_is_not_downstream_of_another_and_the_other_reruns_fresh()
    {
        var (p1, p2, ap) = (_f.Node("P1"), _f.Node("P2"), _f.Approval("Approve"));
        _f.Connect(p1, p2);
        _f.Connect(p1, ap);
        _f.Connect(p2, ap);
        _f.Install(
            Reply("s-p2", "P2 fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-p2").CapturePrompt(_f.File("p2-fix.txt"))),
            Reply("s-p1", "P1 fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-p1")),
            On().Choose(
                (Fresh("P1"), Reply("s-p1", "Report P1")),
                (Fresh("P2"), Reply("s-p2", "Report P2"))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.InputRequired>(engine, ap);

        await engine.SendBack(ap, "Handle empty input.");

        await Until(engine, ap, state => state is NodeState.InputRequired && engine.History(p2).Count == 2);
        Assert.Equal("P1 fixed", engine.History(p1)[^1].Report);
        Assert.Equal(engine.History(p1)[^1].Id, Assert.Single(engine.History(p2)[^1].Basis.Inputs).Result);
        Assert.False(File.Exists(_f.File("p2-fix.txt")));
    }

    [Fact]
    public async Task A_review_in_Chat_mode_cannot_run()
    {
        var r = _f.Review("Check");
        _f.Workflow = _f.Workflow.Must(new WorkflowEdit.SetConversation(r, ConversationMode.Chat));
        var engine = await _f.Open();

        Assert.Equal("A review ends each turn with its verdict, so it cannot chat. Choose Autonomous or May ask.", engine.CanRun(r));
    }

    [Fact]
    public async Task A_verdict_that_cannot_be_read_waits_for_the_person()
    {
        var (a, r) = (_f.Node("A"), _f.Review("Check"));
        _f.Connect(a, r);
        _f.Install(On().Choose((Fresh("A"), Reply("s-a", "Report A")), (Fresh("Check"), Reply("s-r", "It looks fine to me."))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        var state = Assert.IsType<NodeState.InputRequired>(await Until<NodeState.InputRequired>(engine, r));
        Assert.StartsWith("iDevelop could not read the verdict.", state.Why, StringComparison.Ordinal);
        Assert.Empty(engine.History(r));
        Assert.Single(engine.History(a));
    }

    [Fact]
    public async Task A_reopened_engine_replays_the_feedback_and_its_round_from_the_log()
    {
        var (a, r) = (_f.Node("A"), _f.Review("Check"));
        _f.Connect(a, r);
        _f.Install(
            FakeAgents.Resuming(ClientId.Antigravity, "s-a").Print(FakeAgents.SessionLine(ClientId.Antigravity, "s-a")).WaitForFile(_f.File("never")),
            On().Choose((Fresh("A"), Reply("s-a", "Report A")), (Fresh("Check"), Reply("s-r", Reject))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until(engine, a, state => state is NodeState.Running && engine.Entries(r).OfType<NodeEntry.Feedback>().Any());
        await engine.DisposeAsync();

        var reopened = await _f.Open();

        Assert.Equal(new NodeState.Idle(), reopened.State(r));
        Assert.IsType<NodeEntry.Feedback>(reopened.Entries(r)[^1]);
        Assert.Equal(1, new NodeLog(_f.Project, r).Record.Rounds);
        Assert.Equal(new NodeState.InputRequired(NodeState.Interrupted), reopened.State(a));
    }
}
