using System.Collections.Immutable;
using IDevelop.Desktop.Canvas;
using IDevelop.Desktop.Execution;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

/// <summary>A node's state from its newest attempt and why it cannot start, and its role.</summary>
public sealed class NodeStateTests
{
    private static readonly StartProblem NoAgent = new StartProblem.NoAgent();

    private static AttemptRecord Attempt(AttemptStatus status, bool stopping = false) =>
        AttemptReducer.Start(new AttemptEvent.Requested(
            DateTimeOffset.UnixEpoch, AttemptId.New(), TestTasks.Design, "Design", new ExecutionSettings(ClientId.Codex), "", "codex", []))
        with { Status = status, Stopping = stopping };

    private static readonly (string Name, AttemptRecord? Attempt, bool Elsewhere)[] Attempts =
    [
        ("none", null, false),
        ("running", Attempt(AttemptStatus.Running), false),
        ("running elsewhere", Attempt(AttemptStatus.Running), true),
        ("stopping", Attempt(AttemptStatus.Running, stopping: true), true),
        ("waiting", Attempt(AttemptStatus.WaitingForInput), false),
        ("in review", Attempt(AttemptStatus.InReview), false),
        ("succeeded", Attempt(AttemptStatus.Succeeded), false),
        ("failed", Attempt(AttemptStatus.Failed), false),
        ("interrupted", Attempt(AttemptStatus.Interrupted), false),
        ("cancelled", Attempt(AttemptStatus.Cancelled), false),
    ];

    private static (string, NodeState)[] States(StartProblem? problem) =>
        [.. Attempts.Select(row => (row.Name, NodeStates.Of(row.Attempt, row.Elsewhere, problem)))];

    [Fact]
    public void Without_a_problem_the_state_is_the_attempts()
    {
        Assert.Equal(
            [
                ("none", NodeState.Idle), ("running", NodeState.Running), ("running elsewhere", NodeState.RunningElsewhere),
                ("stopping", NodeState.Stopping), ("waiting", NodeState.Waiting), ("in review", NodeState.InReview),
                ("succeeded", NodeState.Succeeded), ("failed", NodeState.Failed), ("interrupted", NodeState.Interrupted),
                ("cancelled", NodeState.Cancelled),
            ],
            States(null));
    }

    [Fact]
    public void A_missing_setting_shows_unless_a_run_goes_on_or_ended_in_a_way_the_person_must_see()
    {
        Assert.Equal(
            [
                ("none", NodeState.NeedsSetup), ("running", NodeState.Running), ("running elsewhere", NodeState.RunningElsewhere),
                ("stopping", NodeState.Stopping), ("waiting", NodeState.Waiting), ("in review", NodeState.InReview),
                ("succeeded", NodeState.NeedsSetup), ("failed", NodeState.Failed), ("interrupted", NodeState.Interrupted),
                ("cancelled", NodeState.NeedsSetup),
            ],
            States(NoAgent));
    }

    [Fact]
    public void A_wait_for_something_else_is_not_a_missing_setting()
    {
        Assert.Equal([("none", NodeState.Idle), ("succeeded", NodeState.Succeeded)],
            States(new StartProblem.SubjectNotDone("Build")).Where(row => row.Item1 is "none" or "succeeded"));
    }

    [Fact]
    public void Only_a_setting_the_person_gives_is_a_setup_problem()
    {
        StartProblem[] setup =
        [
            NoAgent, new StartProblem.FieldMissing("Instructions"), new StartProblem.NoModel(ClientId.Codex),
            new StartProblem.ModelNotOffered(ClientId.Codex, "gpt-x"), new StartProblem.ModelUnready(ClientId.Codex, "gpt-x", "Sign in."),
            new StartProblem.ReasoningNotOffered(ClientId.Codex, "gpt-x", "max", ["low"]), new StartProblem.NoReadOnlyMode(ClientId.Pi),
            new StartProblem.NoSubject(), new StartProblem.ClientMissing(ClientId.Codex, "Not found."), new StartProblem.ClientUnready(ClientId.Codex, "Sign in."),
        ];
        StartProblem[] other =
        [
            new StartProblem.Waiting("Design"), new StartProblem.RunsInWorkflow(), new StartProblem.SubjectNotDone("Build"),
            new StartProblem.InReview("Review"), new StartProblem.UnderReview("Review"), new StartProblem.ClientChecking(ClientId.Codex),
            new StartProblem.AlreadyRunning(TestTasks.Design, "Design"), new StartProblem.RunInAnotherWindow(), new StartProblem.RunOwned(),
        ];

        Assert.All(setup, problem => Assert.True(NodeStates.IsSetup(problem), problem.ToString()));
        Assert.All(other, problem => Assert.False(NodeStates.IsSetup(problem), problem.ToString()));
    }

    [Fact]
    public void A_run_owned_task_has_the_same_plain_reason_for_start_send_and_terminal_handoff()
    {
        var problem = new StartProblem.RunOwned();
        Assert.Equal("A workflow run owns this task.", RunText.Describe(problem));
        Assert.Equal("A workflow run owns this task.", RunText.Describe(new SendProblem.CannotStart(problem)));
        Assert.Equal("A workflow run owns this task.", RunText.Describe(new TerminalProblem.Blocked(problem)));
    }

    [Fact]
    public void An_open_proposal_outranks_a_review_in_progress()
    {
        var underReview = new StartProblem.UnderReview("Review the change");

        Assert.Equal(
            [NodeRole.None, NodeRole.UnderReview, NodeRole.Proposing, NodeRole.Proposing],
            new[] { (null, false), (underReview, false), (null, true), (underReview, true) }
                .Select(row => NodeStates.RoleOf(row.Item1, row.Item2)));
    }
}
