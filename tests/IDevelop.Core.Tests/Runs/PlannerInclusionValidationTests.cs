using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Nodes;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Approval.PlannerInclusionTests;
using static IDevelop.Core.Tests.Runs.ReportReuseTests;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

/// <summary>
/// <see cref="ReportReuse.ValidatePlanner"/> and the approval that records an included planner's report, each guard with a
/// log or a workflow that only it refuses (E3d.2).
/// </summary>
public sealed class PlannerInclusionValidationTests
{
    private static readonly AttemptId Source = new(Id(90));
    private static readonly Guid Plan = Id(700);
    private static readonly TaskId Added = Proposal.Mint(Plan, "new-1");
    private static readonly TaskId Next = Proposal.Mint(Plan, "new-2");
    private static readonly TaskId Slot = new(Id(705));

    private const string First = """
        A first cut.

        ```idevelop
        {"status": "proposal", "add": [{"id": "new-1", "type": "type-1", "title": "N1", "fields": {"brief": "Build N1."}}]}
        ```
        """;

    private const string Second = """
        Split as you asked.

        ```idevelop
        {"status": "proposal",
         "fill": [{"slot": "slot-1", "title": "S", "fields": {"brief": "Build S."}}],
         "add": [{"id": "new-1", "type": "type-1", "title": "N1", "fields": {"brief": "Build N1."}},
                 {"id": "new-2", "type": "type-1", "title": "N2", "fields": {"brief": "Build N2."}},
                 {"id": "new-3", "type": "type-2", "title": "N3", "fields": {"instructions": "Build N3."}}],
         "connect": [{"from": "planner", "to": "new-1"}, {"from": "new-1", "to": "new-2"}]}
        ```
        """;

    private static TaskDefinition PlannerTask(WorkSpec? work = null, string brief = "Plan it.") =>
        new TaskDefinition(T, work is null ? PlannerBlueprint
            : new Blueprint(PlannerBlueprint.Key, PlannerBlueprint.Name, work, PlannerBlueprint.Fields, PlannerBlueprint.Defaults))
        {
            Title = "Plan", Execution = new(ClientId.Codex) { Model = "m1", Reasoning = "high" }, Conversation = ConversationMode.Chat,
        }.WithField("brief", brief)!;

    /// <summary>
    /// The planner with the empty task S the person drew after it, after the person accepted the planner's second
    /// proposal but the built-in Implement N3.
    /// </summary>
    private static Workflow Accepted(TaskDefinition? planner = null)
    {
        var workflow = FixtureWorkflow(planner ?? PlannerTask());
        foreach (var (id, name, x) in new[] { (Slot, "S", 0), (Added, "N1", 320), (Next, "N2", 640) })
        {
            workflow = Edit(workflow, new WorkflowEdit.PlaceNode(id, Step, new(x, 0))
            {
                Title = name, Fields = ImmutableDictionary<string, string>.Empty.Add("brief", $"Build {name}."),
                Settings = new(workflow.Tasks[T].Execution, ConversationMode.Autonomous),
            });
        }
        return Connect(Connect(Connect(workflow, T, Slot), T, Added), Added, Next);
    }

    /// <summary>
    /// A standalone Chat planner's log of two turns on <paramref name="commit"/>'s files, marked done, as Run Workflow's
    /// confirmation leaves it. <paramref name="change"/> breaks one thing the validator checks.
    /// </summary>
    private static void WritePlanner(string project, CommitId commit, string change = "", AttemptId? attempt = null)
    {
        var definition = PlannerTask(change == "notPlanner" ? new WorkSpec.Agent(AgentAccess.ReadOnly, false, PromptTemplate.Parse("{{brief}}"))
            : change == "editor" ? new WorkSpec.Agent(AgentAccess.Edit, true, PromptTemplate.Parse("{{brief}}")) : null,
            change == "definition" ? "Plan more." : "Plan it.");
        var tree = Tree(project, commit);
        var later = tree;
        if (change == "tree")
        {
            File.WriteAllText(Path.Combine(project, "later.txt"), "later");
            later = Tree(project, Commit(project));
        }
        var settings = change == "settings" ? new ExecutionSettings(ClientId.Codex) { Model = "m2", Reasoning = "high" } : definition.Execution!;
        using var log = AttemptLog.Create(DataFolder.Attempts(project), new AttemptEvent.Requested(At, attempt ?? Source, T,
            "Plan", settings, change == "prompt" ? "Different" : "Plan it.\n\nEnd every message with a proposal block.", "codex", [])
        {
            StandaloneCapture = change == "uncaptured" ? null : JsonSerializer.SerializeToElement(
                new StandaloneCapture(definition, change == "inputs" ? "Declared" : ""), RunJournal.Options),
            Conversation = ConversationMode.Chat,
            Planning = change == "unplanned" ? null : new PlanningHandles(Plan, [Slot], [Step.Key, BuiltInBlueprints.Implement.Key]),
            Tree = tree,
            ReadOnly = change != "writer",
            Continues = change == "continued" ? new(new(Id(89)), "session-1") : null,
            RunBinding = change == "bound" ? new(W, Run, new(V1), new(Id(101))) : null,
        });
        log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("session-1")));
        log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded(First)));
        log.Append(new AttemptEvent.Exited(At, 0, "") { Tree = tree });
        log.Append(new AttemptEvent.TurnRequested(At, "Split it.", "codex", []) { Tree = later, Conversation = ConversationMode.Chat });
        log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded(change switch
        {
            "noProposal" => "Nothing more.",
            "unreadable" => "```idevelop\n{\"status\": \"proposal\", \"add\": 3}\n```",
            _ => Second,
        })));
        log.Append(new AttemptEvent.Exited(At, 0, "") { Tree = later });
        if (change == "handoff") log.Append(new AttemptEvent.HandedToTerminal(At, project, "codex resume session-1"));
        if (change == "queued") log.Append(new AttemptEvent.MessageQueued(At, "Also add tests.", false));
        if (change != "waiting") log.Append(new AttemptEvent.MarkedDone(At));
    }

    private static PlannerReport Validate(string project, Workflow approved, CommitId commit, int turn = 2, bool waiting = false,
        AttemptId? source = null) =>
        ReportReuse.ValidatePlanner(project, approved, T, new(T, source ?? Source), turn, commit, new(Id(800)), waiting);

    [Fact]
    public void A_finished_planner_whose_accepted_proposal_the_workflow_holds_is_included_with_its_evidence()
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit);

        var report = Validate(f.Project, f.Workflow, commit);

        Assert.Null(report.Rejection);
        Assert.Equal(Second, report.Report);
        var evidence = report.Evidence!;
        Assert.Equal((2, new OperationId(Id(800))), (evidence.Turn, evidence.Confirmation));
        Assert.Equal(Checkpoint(AttemptLog.FolderOf(DataFolder.Attempts(f.Project), T, Source)), evidence.SourceLog);
        Assert.Equal(Revision.Hash(Revision.CanonicalTask(f.Workflow.Tasks[T])), evidence.Definition);
    }

    [Theory]
    [InlineData("continued")]
    [InlineData("handoff")]
    [InlineData("queued")]
    [InlineData("bound")]
    [InlineData("uncaptured")]
    [InlineData("inputs")]
    [InlineData("definition")]
    [InlineData("settings")]
    [InlineData("prompt")]
    [InlineData("writer")]
    [InlineData("unplanned")]
    [InlineData("tree")]
    [InlineData("noProposal")]
    [InlineData("unreadable")]
    [InlineData("waiting")]
    public void A_log_that_breaks_one_check_cannot_be_included(string change)
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit, change);

        var report = Validate(f.Project, f.Workflow, commit);

        Assert.Equal(new RunRejection(RunProblem.ReuseUnverifiable, Task: T), report.Rejection);
        Assert.Null(report.Evidence);
    }

    [Fact]
    public void A_waiting_planner_counts_only_for_a_preview()
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit, "waiting");

        Assert.Null(Validate(f.Project, f.Workflow, commit, waiting: true).Rejection);
        Assert.Equal(RunProblem.ReuseUnverifiable, Validate(f.Project, f.Workflow, commit).Rejection!.Problem);
    }

    [Fact]
    public void Only_the_final_turn_and_the_attempt_the_log_names_can_be_included()
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit);
        var copy = AttemptLog.FolderOf(DataFolder.Attempts(f.Project), T, new(Id(91)));
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(AttemptLog.FolderOf(DataFolder.Attempts(f.Project), T, Source), "events.jsonl"), Path.Combine(copy, "events.jsonl"));

        Assert.Equal(RunProblem.ReuseUnverifiable, Validate(f.Project, f.Workflow, commit, turn: 1).Rejection!.Problem);
        Assert.Equal(RunProblem.ReuseUnverifiable, Validate(f.Project, f.Workflow, commit, source: new(Id(91))).Rejection!.Problem);
        Assert.Null(Validate(f.Project, f.Workflow, commit).Rejection);
    }

    [Fact]
    public void A_proposal_of_an_earlier_turn_cannot_be_included()
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit, "noProposal");

        Assert.Equal(RunProblem.ReuseUnverifiable, Validate(f.Project, f.Workflow, commit, turn: 1).Rejection!.Problem);
    }

    [Theory]
    [InlineData("notPlanner")]
    [InlineData("editor")]
    public void Only_a_read_only_planner_can_be_included(string change)
    {
        var work = change == "notPlanner" ? new WorkSpec.Agent(AgentAccess.ReadOnly, false, PromptTemplate.Parse("{{brief}}"))
            : new WorkSpec.Agent(AgentAccess.Edit, true, PromptTemplate.Parse("{{brief}}"));
        using var f = new RunFixtures(Accepted(PlannerTask(work)));
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit, change);

        Assert.Equal(RunProblem.ReuseUnverifiable, Validate(f.Project, f.Workflow, commit).Rejection!.Problem);
    }

    [Theory]
    [InlineData("input", false)]
    [InlineData("connection", false)]
    [InlineData("subset", true)]
    [InlineData("edited", true)]
    [InlineData("refilled", true)]
    [InlineData("unknownType", false)]
    public void The_approved_workflow_must_hold_what_the_person_accepted_and_give_the_planner_no_input(string change, bool includable)
    {
        var workflow = Accepted();
        workflow = change switch
        {
            "input" => Connect(Edit(workflow, TestNodes.Place(Task(U), new(0, 200))), U, T, ConnectionKind.Context),
            "connection" => Edit(workflow, new WorkflowEdit.Delete([], [new(T, Added)])),
            "subset" => Edit(workflow, new WorkflowEdit.Delete([Next], [])),
            "unknownType" => Edit(workflow, new WorkflowEdit.Delete([Slot, Added, Next], [])),
            "edited" => Edit(workflow, new WorkflowEdit.SetField(Added, "brief", "Build N1 with care.")),
            "refilled" => Edit(workflow, new WorkflowEdit.SetField(Slot, "brief", "Build S with care.")),
            _ => workflow,
        };
        using var f = new RunFixtures(workflow);
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit);

        var report = Validate(f.Project, f.Workflow, commit);

        Assert.Equal(includable, report.Rejection is null);
    }

    [Fact]
    public void Approval_records_an_included_planner_and_its_task_never_starts_in_the_run()
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit);
        var operation = f.Op();
        var confirmation = f.Op();
        ImmutableArray<ReportInclusion> include = [new(T, Source, 2)];

        var created = Assert.IsType<RunDecision.Created>(f.Store.Approve(W, Run, operation, Revision.Capture(f.Workflow),
            new(commit, BaseChoice.Head), include, confirmation));
        var repeated = Assert.IsType<RunDecision.Existing>(f.NewStore().Approve(W, Run, operation, Revision.Capture(f.Workflow),
            new(commit, BaseChoice.Head), include, confirmation));

        Assert.Equal(RunJournal.Canonical(created.Event), RunJournal.Canonical(repeated.Event));
        var record = f.Read();
        Assert.Equal(1, record.Sequence);
        var result = Assert.Single(record.Results);
        Assert.Equal((T, Second, (ResultId?)null), (result.Task, result.Report, result.Supersedes));
        var origin = Assert.IsType<ResultOrigin.Included>(result.Origin);
        Assert.Equal((new AttemptSource.Standalone(T, Source), confirmation, 2), (origin.Source, origin.Evidence.Confirmation, origin.Evidence.Turn));
        Assert.Equal(new CodeSelection.Root(commit), record.Inputs[result.Inputs].Code);
        Assert.Equal(RunProblem.StartConflict, Problem(f.Store.Reserve(f.Lease(T), f.Op(), record.Revision.Id, new AttemptCause.Initial())));
    }

    [Fact]
    public void An_inclusion_that_fails_approves_nothing()
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit, "waiting");

        var refused = f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow), new(commit, BaseChoice.Head), [new(T, Source, 2)], f.Op());

        Assert.Equal(new RunRejection(RunProblem.ReuseUnverifiable, Task: T), Assert.IsType<RunDecision.Rejected>(refused).Reason);
        Assert.False(File.Exists(f.Journal(W, Run)) && new FileInfo(f.Journal(W, Run)).Length > 0);
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow),
            new(commit, BaseChoice.Head), [new(T, Source, 2)])));
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.Approve(W, Run, f.Op(), Revision.Capture(f.Workflow),
            new(commit, BaseChoice.Head), [new(T, Source, 2), new(T, Source, 2)], f.Op())));
        Assert.Equal(new RunRejection(RunProblem.ReuseUnverifiable, Task: U), Assert.IsType<RunDecision.Rejected>(f.Store.Approve(W, Run, f.Op(),
            Revision.Capture(f.Workflow), new(commit, BaseChoice.Head), [new(U, Source, 2)], f.Op())).Reason);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("twice")]
    [InlineData("input")]
    [InlineData("code")]
    [InlineData("origin")]
    [InlineData("definition")]
    [InlineData("source")]
    [InlineData("empty")]
    [InlineData("confirmation")]
    [InlineData("edge")]
    public void A_journal_whose_approval_includes_a_result_that_does_not_fit_is_rejected(string change)
    {
        using var f = new RunFixtures(Accepted());
        var commit = Repository(f.Project);
        WritePlanner(f.Project, commit);
        var approved = Assert.IsType<RunEvent.Approved>(Assert.IsType<RunDecision.Created>(f.Store.Approve(W, Run, f.Op(),
            Revision.Capture(f.Workflow), new(commit, BaseChoice.Head), [new(T, Source, 2)], f.Op())).Event);
        var included = approved.Included!.Value[0];
        var origin = (ResultOrigin.Included)included.Result.Origin;
        var broken = change switch
        {
            "task" => included with { Result = included.Result with { Task = Added }, Inputs = included.Inputs with { Task = Added } },
            "input" => included with { Inputs = included.Inputs with { Text = "More." } },
            "code" => included with { Inputs = included.Inputs with { Code = new CodeSelection.Root(new(new string('9', 40))) } },
            "origin" => included with { Result = included.Result with { Origin = new ResultOrigin.Executed(new(Id(95))) } },
            "definition" => included with { Result = included.Result with { Origin = origin with { Evidence = origin.Evidence with { Definition = Revision.Hash("other") } } } },
            "source" => included with { Result = included.Result with { Origin = origin with { Source = new(U, Source) } } },
            "confirmation" => included with { Result = included.Result with { Origin = origin with { Evidence = origin.Evidence with { Confirmation = default } } } },
            _ => included,
        };
        ImmutableArray<IncludedResult> all = change switch
        {
            "twice" => [included, included with { Result = included.Result with { Id = new(Id(96)) }, Inputs = included.Inputs with { Id = new(Id(97)) } }],
            "empty" => [],
            _ => [broken],
        };
        var entry = new RunEntry(3, 1, new(Id(98)), Prompt, At, approved with { Included = all });
        if (change == "edge")
        {
            // The planner gets an input in the approved revision; nothing else about the included result changes.
            var revision = Revision.Capture(Connect(Edit(f.Workflow, TestNodes.Place(Task(U), new(0, 400))), U, T, ConnectionKind.Context));
            var moved = included with { Result = included.Result with { Revision = revision.Id }, Inputs = included.Inputs with { Revision = revision.Id } };
            entry = entry with { Event = approved with { Revision = revision, Included = [moved] } };
        }

        var read = Assert.IsType<RunRead.Rejected>(RunReducer.Replay(W, Run, [entry]));

        Assert.Equal(change switch
        {
            "twice" => RunProblem.StartConflict,
            "input" or "code" => RunProblem.InputConflict,
            "empty" or "confirmation" => RunProblem.InvalidData,
            _ => RunProblem.ReuseUnverifiable,
        }, read.Reason.Problem);
        Assert.IsType<RunRead.Loaded>(RunReducer.Replay(W, Run, [new RunEntry(3, 1, new(Id(98)), Prompt, At, approved)]));
    }
}
