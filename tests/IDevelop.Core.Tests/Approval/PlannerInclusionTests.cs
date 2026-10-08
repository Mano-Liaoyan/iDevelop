using System.Collections.Immutable;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.ApprovalFixture;
using static IDevelop.Core.Tests.Approval.ApprovalTests;
using static IDevelop.Core.Tests.Approval.ChangedContentTests;
using static IDevelop.Core.Tests.Coordination.CoordinatorFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Approval;

/// <summary>
/// Generate's Chat planner ran on its own, the person accepted its proposal, and Run Workflow includes its report instead
/// of running it again (E3d.2).
/// </summary>
public sealed class PlannerInclusionTests
{
    internal const string Session = "session-X";
    internal static readonly OperationId Confirm = new(Guid.Parse("00000000-0000-0000-0000-0000000d0001"));

    /// <summary>A read-only Chat planner whose prompt starts with <c>"Build X."</c>, as Generate places one.</summary>
    internal static readonly Blueprint PlannerBlueprint = new(new("example.planner", 1), "Planner",
        new WorkSpec.Agent(AgentAccess.ReadOnly, true, PromptTemplate.Parse("{{brief}}")),
        [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Chat));

    /// <summary>The one type the planner may add: a writer whose prompt is its brief.</summary>
    internal static readonly Blueprint Step = new(new("example.step", 1), "Step",
        new WorkSpec.Agent(AgentAccess.Edit, false, PromptTemplate.Parse("{{brief}}")),
        [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Autonomous));

    internal const string FirstProposal = """
        A first cut.

        ```idevelop
        {"status": "proposal", "add": [{"id": "new-1", "type": "type-1", "title": "N1", "fields": {"brief": "Build N1."}}],
         "connect": [{"from": "planner", "to": "new-1"}]}
        ```
        """;

    internal const string SecondProposal = """
        Split as you asked.

        ```idevelop
        {"status": "proposal",
         "add": [{"id": "new-1", "type": "type-1", "title": "N1", "fields": {"brief": "Build N1."}},
                 {"id": "new-2", "type": "type-1", "title": "N2", "fields": {"brief": "Build N2."}}],
         "connect": [{"from": "planner", "to": "new-1"}, {"from": "new-1", "to": "new-2"}]}
        ```
        """;

    internal static TaskDefinition Planner(string model = "gpt-6-sol") => new TaskDefinition(X, PlannerBlueprint)
    {
        Title = "X", Execution = new(ClientId.Codex) { Model = model, Reasoning = "high" }, Conversation = ConversationMode.Chat,
    }.WithField("brief", "Build X.")!;

    /// <summary>A project whose workflow holds only the planner, with every answer its run and the run it plans need.</summary>
    internal static ApprovalFixture Fixture() => new ApprovalFixture(FixtureWorkflow(Planner()))
        .Answer(X, FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, Session)).Print(FakeAgents.ReplyLines(ClientId.Codex, FirstProposal)))
        .Answer("N1", FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-N1")).Write("n1.txt", "N1\n")
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "N1 ready.\n")))
        .Answer("N2", FakeRule.On().Print(FakeAgents.SessionLine(ClientId.Codex, "session-N2")).Write("n2.txt", "N2\n")
            .Print(FakeAgents.ReplyLines(ClientId.Codex, "N2 ready.\n")))
        .WithRule(FakeAgents.Resuming(ClientId.Codex, Session).Print(FakeAgents.SessionLine(ClientId.Codex, Session))
            .Print(FakeAgents.ReplyLines(ClientId.Codex, SecondProposal)));

    /// <summary>
    /// Runs the planner on its own for two Chat turns, the second after the person's reply, and accepts all of the second
    /// proposal with the planner's agent for the new tasks, as Generate's sheet does.
    /// </summary>
    internal static async System.Threading.Tasks.Task<AttemptRecord> PlanTwoTurns(ApprovalFixture f, ProjectRuns? window = null)
    {
        var runs = window ?? f.Runs;
        var planner = f.Workflow.Tasks[X];
        await Waits(runs, 1, () => System.Threading.Tasks.Task.FromResult<object>(runs.Start(planner, PlanningContext.For(f.Workflow, X, [Step], _ => false))));
        var waiting = await Waits(runs, 2, async () => await runs.SendAsync(planner, "Split it.", stopTurn: false));
        var proposal = Assert.IsType<ProposalRead.Ready>(Proposal.Read(waiting, key => key == Step.Key ? Step : null)).Proposal;
        f.Workflow = Edit(f.Workflow, proposal.Accept(f.Workflow, proposal.Items.ToHashSet(), _ => false, planner.Execution));
        return waiting;
    }

    /// <summary>Does <paramref name="act"/>, then waits until the planner waits for the person after turn <paramref name="turns"/>.</summary>
    internal static async System.Threading.Tasks.Task<AttemptRecord> Waits(ProjectRuns runs, int turns, Func<System.Threading.Tasks.Task<object>> act)
    {
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (runs.Latest.GetValueOrDefault(X) is { Status: AttemptStatus.WaitingForInput } record && record.Turns.Count == turns && runs.Active.IsEmpty)
                settled.TrySetResult(record);
        }
        runs.Changed += OnChanged;
        try
        {
            var result = await act();
            Assert.False(result is StartResult.Refused or SendResult.Refused, $"refused: {result}");
            OnChanged(null, EventArgs.Empty);
            return await settled.Task.WaitAsync(Bound);
        }
        finally { runs.Changed -= OnChanged; }
    }

    internal static TaskId Added(ApprovalFixture f, string title) => f.Workflow.Tasks.Values.Single(task => task.Title == title).Id;

    internal static RunConfirmation Including(RunPreflight preview, OperationId command, params PreflightPlanner[] planners) =>
        new(preview, BaseChoice.Head, command) { Include = [.. planners.Select(planner => new ReportInclusion(planner.Task, planner.Source, planner.Turn))] };

    [Fact]
    public async System.Threading.Tasks.Task A_Chat_planner_s_two_turns_are_included_without_running_it_again()
    {
        await using var f = Fixture();
        await f.Open();
        var waiting = await PlanTwoTurns(f);
        var preview = f.Preflight();
        var planner = Assert.Single(preview.Planners);
        Assert.Equal((X, waiting.Id, 2, AttemptStatus.WaitingForInput), (planner.Task, planner.Source, planner.Turn, planner.Status));
        Assert.Equal<BaseChoice>([BaseChoice.Head], planner.Bases);
        Assert.Null(planner.Problem);
        Assert.Equal(2, f.TotalLaunches);

        var started = Assert.IsType<WorkflowStart.Started>(await f.Runs.StartWorkflow(f.Workflow, Including(preview, Confirm, planner)).WaitAsync(Bound));
        await Completed(started.Coordinator);

        var finished = f.Runs.Latest[X];
        Assert.Equal((waiting.Id, AttemptStatus.Succeeded, 2), (finished.Id, finished.Status, finished.Turns.Count));
        Assert.Equal([1, 1], new[] { f.Launches("N1"), f.Launches("N2") });
        Assert.Equal(2, f.TotalLaunches - f.Launches("N1") - f.Launches("N2"));
        var record = f.Read(Assert.Single(f.ApprovedRuns()));
        var included = Assert.Single(record.Results, result => result.Origin is ResultOrigin.Included);
        Assert.Equal(X, included.Task);
        Assert.Equal(waiting.Result, included.Report);
        Assert.Equal(new AttemptSource.Standalone(X, waiting.Id), ((ResultOrigin.Included)included.Origin).Source);
        Assert.DoesNotContain(record.Attempts.Values, attempt => attempt.Task == X);
        Assert.Contains("Split as you asked.", File.ReadAllText(Path.Combine(f.Folder("N1"), "1.stdin")));
        Assert.Equal("N1\n", f.ResultFile(record.Id, Added(f, "N2"), "n1.txt"));
    }

    [Fact]
    public async System.Threading.Tasks.Task An_ordinary_reusable_report_is_included_at_confirmation_with_E1_s_validator()
    {
        await using var f = ChainAnswers(new ApprovalFixture(Chain()));
        await f.Open();
        var settled = new TaskCompletionSource<AttemptRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runs.Changed += (_, _) =>
        {
            if (f.Runs.Active.IsEmpty && f.Runs.Latest.GetValueOrDefault(X) is { Status: AttemptStatus.Succeeded } done) settled.TrySetResult(done);
        };
        Assert.IsType<StartResult.Started>(f.Runs.Start(f.Workflow.Tasks[X]));
        var attempt = await settled.Task.WaitAsync(Bound);
        var preview = f.Preflight();
        var reusable = Assert.Single(preview.Reusable);
        Assert.Empty(preview.Planners);

        var started = Assert.IsType<WorkflowStart.Started>(await f.Runs.StartWorkflow(f.Workflow,
            new(preview, BaseChoice.Head, Confirm) { Include = [new(X, reusable.Source, 1)] }).WaitAsync(Bound));
        await Completed(started.Coordinator);

        Assert.Equal([1, 1, 1], new[] { f.Launches(A), f.Launches(B), f.Launches(X) });
        var result = f.Read(started.Coordinator.Address.Run).CurrentResults[X];
        Assert.Equal(("X ready.\n", new AttemptSource.Standalone(X, attempt.Id)), (result.Report, Assert.IsType<ResultOrigin.Reused>(result.Origin).Source));
        var refused = Assert.IsType<WorkflowStart.Refused>(await f.Runs.StartWorkflow(f.Workflow,
            new(f.Preflight(), BaseChoice.Head, new(Guid.NewGuid())) { Include = [new(X, reusable.Source, 2)] }).WaitAsync(Bound));
        Assert.Equal((ApprovalProblem.InclusionRefused, "ReuseUnverifiable"), (refused.Problem, refused.Detail));
        Assert.Single(Intents(f.Project));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_planner_that_read_uncommitted_work_is_included_on_the_snapshot_base()
    {
        await using var f = Fixture();
        f.Git.Write("notes.txt", "notes\n");
        await f.Open();
        var waiting = await PlanTwoTurns(f);
        var preview = f.Preflight();
        var planner = Assert.Single(preview.Planners);
        Assert.Equal<BaseChoice>([BaseChoice.Snapshot], planner.Bases);

        var started = Assert.IsType<WorkflowStart.Started>(await f.Runs.StartWorkflow(f.Workflow,
            Including(preview, Confirm, planner) with { Choice = BaseChoice.Snapshot }).WaitAsync(Bound));
        await Completed(started.Coordinator);

        var record = f.Read(started.Coordinator.Address.Run);
        Assert.Equal(BaseChoice.Snapshot, record.Base.Choice);
        Assert.Equal((X, waiting.Result), (record.CurrentResults[X].Task, record.CurrentResults[X].Report));
        Assert.IsType<ResultOrigin.Included>(record.CurrentResults[X].Origin);
        Assert.Equal("notes\n", f.ResultFile(record.Id, Added(f, "N1"), "notes.txt"));
        Assert.Equal(2, f.TotalLaunches - f.Launches("N1") - f.Launches("N2"));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_reply_after_the_preview_makes_the_preview_stale_and_finishes_nothing()
    {
        await using var f = Fixture();
        await f.Open();
        var waiting = await PlanTwoTurns(f);
        var preview = f.Preflight();
        await Waits(f.Runs, 3, async () => await f.Runs.SendAsync(f.Workflow.Tasks[X], "Keep it short.", stopTurn: false));

        var changed = Assert.IsType<WorkflowStart.Changed>(await f.Runs.StartWorkflow(f.Workflow, Including(preview, Confirm,
            Assert.Single(preview.Planners))).WaitAsync(Bound));

        Assert.Equal(3, Assert.Single(changed.Current.Planners).Turn);
        Assert.Empty(f.ApprovedRuns());
        Assert.Empty(Intents(f.Project));
        Assert.Equal((waiting.Id, AttemptStatus.WaitingForInput, 3), (f.Runs.Latest[X].Id, f.Runs.Latest[X].Status, f.Runs.Latest[X].Turns.Count));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_planner_is_included_once_and_only_on_the_base_whose_files_it_read()
    {
        await using var f = Fixture();
        await f.Open();
        var waiting = await PlanTwoTurns(f);
        f.Git.Write("notes.txt", "notes\n");
        var preview = f.Preflight();
        var planner = Assert.Single(preview.Planners);
        Assert.Equal<BaseChoice>([BaseChoice.Head, BaseChoice.Snapshot], preview.Choices);
        Assert.Equal<BaseChoice>([BaseChoice.Head], planner.Bases);

        var snapshot = Assert.IsType<WorkflowStart.Refused>(await f.Runs.StartWorkflow(f.Workflow,
            Including(preview, Confirm, planner) with { Choice = BaseChoice.Snapshot }).WaitAsync(Bound));
        var twice = Assert.IsType<WorkflowStart.Refused>(await f.Runs.StartWorkflow(f.Workflow, Including(preview, Confirm, planner, planner))
            .WaitAsync(Bound));

        Assert.Equal((ApprovalProblem.InclusionRefused, "ReuseUnverifiable"), (snapshot.Problem, snapshot.Detail));
        Assert.Equal(ApprovalProblem.NotConfirmable, twice.Problem);
        Assert.Empty(f.ApprovedRuns());
        Assert.Equal(AttemptStatus.WaitingForInput, f.Runs.Latest[X].Status);
        f.Workflow = Connect(Edit(f.Workflow, IDevelop.TestSupport.TestNodes.Place(Agent(A), new(0, 300))), A, X, ConnectionKind.Context);
        Assert.Empty(f.Preflight().Planners);
        Assert.Equal(waiting.Id, f.Runs.Latest[X].Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_planner_whose_settings_changed_since_it_ran_cannot_be_included()
    {
        await using var f = Fixture();
        await f.Open();
        var waiting = await PlanTwoTurns(f);
        f.Workflow = Edit(f.Workflow, new WorkflowEdit.SetExecution(X, new(ClientId.Codex) { Model = "gpt-6.1-sol", Reasoning = "high" }));
        var preview = f.Preflight();
        var planner = Assert.Single(preview.Planners);
        Assert.Empty(planner.Bases);
        Assert.Equal(RunProblem.ReuseUnverifiable, planner.Problem);

        var refused = Assert.IsType<WorkflowStart.Refused>(await f.Runs.StartWorkflow(f.Workflow, Including(preview, Confirm, planner)).WaitAsync(Bound));

        Assert.Equal((ApprovalProblem.InclusionRefused, "ReuseUnverifiable"), (refused.Problem, refused.Detail));
        Assert.Empty(f.ApprovedRuns());
        Assert.Empty(Intents(f.Project));
        Assert.Equal(2, f.TotalLaunches);
        Assert.Equal((waiting.Id, AttemptStatus.WaitingForInput), (f.Runs.Latest[X].Id, f.Runs.Latest[X].Status));
    }
}
