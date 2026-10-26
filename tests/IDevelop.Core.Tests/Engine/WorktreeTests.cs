using IDevelop.Engine;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Engine.EngineFixture;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests.Engine;

/// <summary>
/// Each editing node works in its own worktree on its own branch. It starts from the merge of its predecessors' commits, or
/// from the branch's HEAD as a root, and its completed turn is one commit there. A conflict goes to a Merge node or waits
/// for the person.
/// </summary>
public sealed class WorktreeTests : IAsyncLifetime
{
    private readonly EngineFixture _f = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _f.DisposeAsync().AsTask();

    [Fact]
    public async Task A_root_node_starts_at_the_branch_s_HEAD_and_commits_its_change_on_its_own_branch()
    {
        var a = _f.Node("A");
        _f.Install(Reply("s-a", "Report A", On().RecordWorkingDirectory(_f.File("a.cwd")).Write("a.txt", "A\n")));
        var engine = await _f.Open();

        await engine.Run(a);

        await Until<NodeState.Completed>(engine, a);
        var commit = Assert.IsType<string>(Assert.Single(engine.History(a)).Commit);
        Assert.Equal([_f.Start], _f.Parents(commit));
        Assert.Equal("A\n", _f.Files(commit)["a.txt"]);
        Assert.Equal(commit, _f.Repository.Git("rev-parse", $"idp/{_f.Workflow.Id}/{a}").Trim());
        Assert.Equal(Path.GetFullPath(_f.Worktree(a)), Path.GetFullPath(File.ReadAllText(_f.File("a.cwd")).Trim()));
        Assert.Equal(_f.Start, _f.Head);
        Assert.False(File.Exists(Path.Combine(_f.Project, "a.txt")));
    }

    [Fact]
    public async Task A_node_starts_from_its_one_predecessor_s_commit()
    {
        var (a, b) = (_f.Node("A"), _f.Node("B"));
        _f.Connect(a, b);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("b.txt", "B\n")))));
        var engine = await _f.Open();

        await engine.Run(a);

        await Until<NodeState.Completed>(engine, b);
        var commit = engine.History(b)[0].Commit!;
        Assert.Equal([engine.History(a)[0].Commit!], _f.Parents(commit));
        Assert.Equal(["README.md", "a.txt", "b.txt"], _f.Files(commit).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_node_starts_from_the_merge_of_its_predecessors_commits_and_a_reader_hands_on_what_it_read()
    {
        var (a, b, p, c) = (_f.Node("A"), _f.Node("B"), _f.Reader("P"), _f.Node("C"));
        _f.Connect(a, c);
        _f.Connect(b, p);
        _f.Connect(p, c);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("b.txt", "B\n"))),
            (Fresh("P"), Reply("s-p", "Report P", On().RecordWorkingDirectory(_f.File("p.cwd")))),
            (Fresh("C"), Reply("s-c", "Report C", On().Write("c.txt", "C\n")))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until<NodeState.Completed>(engine, c);
        Assert.Null(engine.History(p)[0].Commit);
        Assert.Equal(Path.GetFullPath(_f.Project), Path.GetFullPath(File.ReadAllText(_f.File("p.cwd")).Trim()));
        Assert.False(Directory.Exists(_f.Worktree(p)));
        var merge = Assert.Single(_f.Parents(engine.History(c)[0].Commit!));
        Assert.Equal([engine.History(a)[0].Commit!, engine.History(b)[0].Commit!], _f.Parents(merge));
        Assert.Equal(["README.md", "a.txt", "b.txt", "c.txt"], _f.Files(engine.History(c)[0].Commit!).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_conflict_without_a_Merge_node_waits_for_the_person_who_resolves_it_in_the_worktree()
    {
        var (a, b, c) = (_f.Node("A"), _f.Node("B"), _f.Node("C"));
        _f.Connect(a, c);
        _f.Connect(b, c);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("x.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("x.txt", "B\n"))),
            (Fresh("C"), Reply("s-c", "Report C", On().CapturePrompt(_f.File("c.txt")).Write("c.txt", "C\n")))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        Assert.Equal(new NodeState.InputRequired(NodeState.Conflict), await Until<NodeState.InputRequired>(engine, c));
        Assert.Contains("x.txt", Assert.IsType<NodeEntry.Waiting>(engine.Entries(c)[^1]).Text, StringComparison.Ordinal);
        var file = Path.Combine(_f.Worktree(c), "x.txt");
        Assert.Contains("<<<<<<<", File.ReadAllText(file), StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.MarkResolved(c));
        Assert.Contains("x.txt", refused.Message, StringComparison.Ordinal);

        File.WriteAllText(file, "A and B\n");
        await engine.MarkResolved(c);

        await Until<NodeState.Completed>(engine, c);
        Assert.True(File.Exists(_f.File("c.txt")));
        var merge = Assert.Single(_f.Parents(engine.History(c)[0].Commit!));
        Assert.Equal([engine.History(a)[0].Commit!, engine.History(b)[0].Commit!], _f.Parents(merge));
        Assert.Equal("A and B\n", _f.Files(merge)["x.txt"]);
    }

    [Fact]
    public async Task A_conflict_goes_to_the_Merge_node_whose_turn_commits_on_both_parents()
    {
        var (a, b, m) = (_f.Node("A"), _f.Node("B"), _f.Merge("M"));
        _f.Connect(a, m);
        _f.Connect(b, m);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("x.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("x.txt", "B\n"))),
            (Fresh("M"), Reply("s-m", "Merged", On().CapturePrompt(_f.File("m.txt")).Write("x.txt", "A and B\n")))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until<NodeState.Completed>(engine, m);
        Assert.Contains("`x.txt`", _f.Prompt("m.txt"), StringComparison.Ordinal);
        var merge = engine.History(m)[0].Commit!;
        Assert.Equal([engine.History(a)[0].Commit!, engine.History(b)[0].Commit!], _f.Parents(merge));
        Assert.Equal("A and B\n", _f.Files(merge)["x.txt"]);
    }

    [Fact]
    public async Task Mark_resolved_is_refused_while_a_predecessor_runs_again_and_after_the_node_s_basis_changed()
    {
        var (a, b, c) = (_f.Node("A"), _f.Node("B"), _f.Node("C"));
        _f.Connect(a, c);
        _f.Connect(b, c);
        var gate = _f.File("gate");
        File.WriteAllText(gate, "");
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().WaitForFile(gate).Write("x.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("x.txt", "B\n")))));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.InputRequired>(engine, c);
        File.WriteAllText(Path.Combine(_f.Worktree(c), "x.txt"), "A and B\n");
        File.Delete(gate);
        await engine.Retry(a);

        Assert.Equal("It waits for A.", (await Assert.ThrowsAsync<InvalidOperationException>(() => engine.MarkResolved(c))).Message);
        await engine.Stop(a);
        engine.Update(_f.Workflow.Must(new WorkflowEdit.SetField(c, "instructions", "Do more.")));
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.MarkResolved(c));

        Assert.Contains("Run", changed.Message, StringComparison.Ordinal);
        Assert.Equal(new NodeState.InputRequired(NodeState.Conflict), engine.State(c));
    }

    [Fact]
    public async Task A_Merge_node_whose_agent_leaves_conflict_markers_waits_for_the_person()
    {
        var (a, b, m) = (_f.Node("A"), _f.Node("B"), _f.Merge("M"));
        _f.Connect(a, m);
        _f.Connect(b, m);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("x.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("x.txt", "B\n"))),
            (Fresh("M"), Reply("s-m", "Merged"))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        Assert.Equal(new NodeState.InputRequired(NodeState.Conflict), await Until<NodeState.InputRequired>(engine, m));
        Assert.Empty(engine.History(m));
    }

    [Fact]
    public async Task A_node_after_a_Merge_node_that_left_Git_s_merge_as_it_was_starts_from_the_Merge_node_s_commit()
    {
        var (a, b, m, d) = (_f.Node("A"), _f.Node("B"), _f.Merge("M"), _f.Node("D"));
        _f.Connect(a, m);
        _f.Connect(b, m);
        _f.Connect(m, d);
        // Binary files conflict without markers, so Git's merge stays as it was.
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("x.bin", "\0A"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("x.bin", "\0B"))),
            (Fresh("M"), Reply("s-m", "Merged")),
            (Fresh("D"), Reply("s-d", "Report D", On().Write("d.txt", "D\n")))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until<NodeState.Completed>(engine, d);
        var merge = engine.History(m)[0].Commit!;
        Assert.Equal([engine.History(a)[0].Commit!, engine.History(b)[0].Commit!], _f.Parents(merge));
        Assert.Equal([merge], _f.Parents(engine.History(d)[0].Commit!));
    }

    [Fact]
    public async Task A_second_conflict_among_three_inputs_goes_to_the_Merge_node_s_session()
    {
        var (a, b, c, m) = (_f.Node("A"), _f.Node("B"), _f.Node("C"), _f.Merge("M"));
        _f.Connect(a, m);
        _f.Connect(b, m);
        _f.Connect(c, m);
        _f.Install(
            Reply("s-m", "Merged all", FakeAgents.Resuming(ClientId.Antigravity, "s-m").CapturePrompt(_f.File("again.txt")).Write("x.txt", "A, B, and C\n")),
            On().Choose(
                (Fresh("A"), Reply("s-a", "Report A", On().Write("x.txt", "A\n"))),
                (Fresh("B"), Reply("s-b", "Report B", On().Write("x.txt", "B\n"))),
                (Fresh("C"), Reply("s-c", "Report C", On().Write("x.txt", "C\n"))),
                (Fresh("M"), Reply("s-m", "Merged two", On().Write("x.txt", "A and B\n")))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until<NodeState.Completed>(engine, m);
        Assert.Contains("`x.txt`", _f.Prompt("again.txt"), StringComparison.Ordinal);
        var merge = engine.History(m)[0].Commit!;
        Assert.Equal("A, B, and C\n", _f.Files(merge)["x.txt"]);
        var (first, third) = (_f.Parents(merge)[0], _f.Parents(merge)[1]);
        Assert.Equal(engine.History(c)[0].Commit!, third);
        Assert.Equal([engine.History(a)[0].Commit!, engine.History(b)[0].Commit!], _f.Parents(first));
    }

    [Fact]
    public async Task A_Merge_node_runs_its_agent_when_its_inputs_merge_cleanly()
    {
        var (a, b, m) = (_f.Node("A"), _f.Node("B"), _f.Merge("M"));
        _f.Connect(a, m);
        _f.Connect(b, m);
        _f.Install(On().Choose(
            (Fresh("A"), Reply("s-a", "Report A", On().Write("a.txt", "A\n"))),
            (Fresh("B"), Reply("s-b", "Report B", On().Write("b.txt", "B\n"))),
            (Fresh("M"), Reply("s-m", "Checked", On().CapturePrompt(_f.File("m.txt"))))));
        var engine = await _f.Open();

        await engine.RunWorkflow();

        await Until<NodeState.Completed>(engine, m);
        Assert.True(File.Exists(_f.File("m.txt")));
        Assert.Equal("Checked", engine.History(m)[0].Report);
        Assert.Equal([engine.History(a)[0].Commit!, engine.History(b)[0].Commit!], _f.Parents(engine.History(m)[0].Commit!));
    }

    [Fact]
    public async Task A_worktree_that_cannot_start_fails_its_node_and_the_other_successors_still_start()
    {
        var (p, b, c) = (_f.Reader("P"), _f.Node("B"), _f.Node("C"));
        _f.Connect(p, b);
        _f.Connect(p, c);
        // A folder where the exclude file belongs makes adding a worktree fail with an IOException.
        var exclude = Path.Combine(_f.Project, ".git", "info", "exclude");
        File.Delete(exclude);
        Directory.CreateDirectory(exclude);
        _f.Install(Reply("s-p", "Report P"));
        var engine = await _f.Open();

        await engine.Run(p);

        await Until<NodeState.Failed>(engine, b);
        await Until<NodeState.Failed>(engine, c);
    }

    [Fact]
    public async Task A_project_below_the_repository_s_top_keeps_its_worktrees_in_the_project_and_works_in_its_folder_there()
    {
        var project = Path.Combine(_f.Project, "services", "api");
        _f.Repository.Write("services/api/api.txt", "API\n");
        _f.Repository.Commit("API");
        _f.ProjectFolder = project;
        var a = _f.Node("A");
        _f.Install(Reply("s-a", "Report A", On().RecordWorkingDirectory(_f.File("a.cwd")).Write("a.txt", "A\n")));
        var engine = await _f.Open();

        await engine.Run(a);

        await Until<NodeState.Completed>(engine, a);
        var checkout = Path.Combine(project, ".worktrees", _f.Workflow.Id.ToString(), a.ToString());
        Assert.Equal(Path.Combine(checkout, "services", "api"), File.ReadAllText(_f.File("a.cwd")).Trim());
        Assert.Equal("A\n", _f.Files(engine.History(a)[0].Commit!)["services/api/a.txt"]);
        await engine.MergeIntoBranch();
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(project, "a.txt")));
    }

    [Fact]
    public async Task A_Retry_starts_again_from_a_clean_worktree()
    {
        var a = _f.Node("A");
        _f.Install(Reply("s-a", "Report A", On().Write("a.txt", "A\n")));
        var engine = await _f.Open();
        await engine.Run(a);
        await Until<NodeState.Completed>(engine, a);
        File.WriteAllText(Path.Combine(_f.Worktree(a), "stray.txt"), "left over\n");

        await engine.Retry(a);

        await Until(engine, a, _ => engine.History(a).Count == 2);
        var commit = engine.History(a)[1].Commit!;
        Assert.Equal([_f.Start], _f.Parents(commit));
        Assert.Equal(["README.md", "a.txt"], _f.Files(commit).Keys.Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(_f.Worktree(a), "stray.txt")));
    }

    [Fact]
    public async Task A_send_back_continues_in_the_worktree_with_the_earlier_change()
    {
        var (a, p) = (_f.Node("A"), _f.Approval("Check"));
        _f.Connect(a, p);
        _f.Install(
            Reply("s-a", "Report A fixed", FakeAgents.Resuming(ClientId.Antigravity, "s-a").Write("fix.txt", "Fixed\n")),
            Reply("s-a", "Report A", On().Write("a.txt", "A\n")));
        var engine = await _f.Open();
        await engine.RunWorkflow();
        await Until<NodeState.InputRequired>(engine, p);

        await engine.SendBack(p, "Fix it.");

        await Until(engine, a, _ => engine.History(a).Count == 2);
        var (first, second) = (engine.History(a)[0].Commit!, engine.History(a)[1].Commit!);
        Assert.Equal([first], _f.Parents(second));
        Assert.Equal(["README.md", "a.txt", "fix.txt"], _f.Files(second).Keys.Order(StringComparer.Ordinal));
    }
}
