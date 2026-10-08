using IDevelop.Core.Tests.Git;
using IDevelop.Core.Tests.Runs;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class RootExitTests
{
    [Fact]
    public async System.Threading.Tasks.Task Observation_records_the_literal_exit_tip_and_head_and_repeats_without_Git()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Claim(f, ready);
        Commit(f, ready);
        var operation = f.Op();
        using var mutation = f.Git.Open().TakeMutationLock();
        Assert.NotNull(mutation);
        var observed = Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(T), operation,
            ready.Execution.Launch, new RootExit.Exited(int.MinValue))).Observation;
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67", observed.Tip.Hex);
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2", observed.Head);
        Assert.Equal(new RootExit.Exited(int.MinValue), observed.Exit);
        Assert.Equal(At, observed.At);
        Assert.Equal(TipOwnership.Explained, observed.Ownership);
        Assert.Single(f.Read().RootExits);
        var sequence = f.Read().Sequence;
        Assert.Equal(0, f.Git.Run(ready.Checkout, "update-ref", "-d", ready.Execution.Location.Owner.Branch).ExitCode);
        Assert.Equal(observed, Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(T), operation,
            ready.Execution.Launch, new RootExit.Exited(int.MinValue))).Observation);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(3, f.Read().Receipts.Values.Single(entry => entry.Event is RunEvent.RootExitObserved).Schema);
    }

    [Fact]
    public async System.Threading.Tasks.Task Commits_before_observation_are_explained_and_commits_after_it_block_sibling_publication()
    {
        foreach (var before in new[] { false, true })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
            var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            Claim(f, writer);
            if (before) Commit(f, writer);
            var observed = Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(),
                writer.Execution.Launch, new RootExit.Exited(0))).Observation;
            Assert.Equal(TipOwnership.Explained, observed.Ownership);
            if (!before) Commit(f, writer);
            var repository = f.Git.Open();
            var tip = GitFixture.Read(repository.ReadRef(writer.Execution.Location.Owner.Branch));
            Assert.Equal(before, RefOwnership.Accepts(f.Read(), repository, writer.Execution.Location.Owner.Branch, tip));
            var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
            f.Close(sibling);
            var publication = f.Materializer().Publish(f.Lease(U), f.Op(), sibling.Execution.Launch.Attempt);
            if (before)
            {
                Assert.Equal(U, Assert.IsType<Publication.Accepted>(publication).Result.Task);
                Assert.Equal(U, Assert.Single(f.Read().Results).Task);
            }
            else
            {
                Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Publication.Blocked>(publication).Block.Problem);
                Assert.Equal(0, f.Read().Results.Count(result => result.Task == U));
            }
            Assert.Equal("B\n", f.Git.Run(writer.Checkout, "show", tip!.Value.Hex + ":b.txt").Text);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Rewind_below_the_expected_tip_is_unexplained_and_does_not_gain_permission()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Claim(f, writer);
        var repository = f.Git.Open();
        Assert.True(RefOwnership.Accepts(f.Read(), repository, writer.Execution.Location.Owner.Branch, f.A));
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "--hard", "HEAD~1").ExitCode);
        var observed = Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(),
            writer.Execution.Launch, new RootExit.Exited(1))).Observation;
        Assert.Equal("81ddb7c330112c7f16700ed002803a04b0bce693", observed.Tip.Hex);
        Assert.Equal(TipOwnership.Unexplained, observed.Ownership);
        Assert.False(RefOwnership.Accepts(f.Read(), repository, writer.Execution.Location.Owner.Branch, observed.Tip));
        Assert.True(RefOwnership.Accepts(f.Read(), repository, writer.Execution.Location.Owner.Branch, f.A));
    }

    [Fact]
    public async System.Threading.Tasks.Task Missing_branch_fences_the_claim_and_prevents_turn_closure()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Claim(f, writer);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "update-ref", "-d", writer.Execution.Location.Owner.Branch).ExitCode);
        var fenced = Assert.IsType<RootObservation.Fenced>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(),
            writer.Execution.Launch, new RootExit.NotStarted("Root creation failed.")));
        Assert.Equal(writer.Execution.Launch, fenced.Launch);
        Assert.Equal("The task branch is absent.", fenced.Detail);
        Assert.Equal(new[] { writer.Execution.Launch }, f.Read().Fenced);
        Assert.Equal(RunProblem.UnresolvedOwnership, Problem(f.Store.CloseTurn(f.Permit, f.Op(), writer.Execution.Launch,
            new LogCheckpoint(0, Prompt))));
        var control = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        Claim(f, control);
        Assert.Equal(TipOwnership.Explained, Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(U), f.Op(),
            control.Execution.Launch, new RootExit.NotStarted("Root creation failed."))).Observation.Ownership);
    }

    [Fact]
    public void Schema_two_replays_to_27_and_refuses_a_root_observation()
    {
        using var f = new RunFixtures();
        var bytes = File.ReadAllBytes(Fixture.Path("e2-run/events.jsonl"));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        Assert.Equal((2, 27L, 1), (f.Read().Schema, f.Read().Sequence, f.Read().Results.Count));
        var observation = new RunEvent.RootExitObserved(new(new(Id(104)), 1), new RootExit.Exited(0), At, Base,
            "refs/heads/idp/task", TipOwnership.Explained);
        File.AppendAllText(f.Journal(W, Run), RunJournal.Encode(new(2, 28, f.Op(), Prompt, At, observation)));
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 28), Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run)).Reason);
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        Assert.Equal(27L, f.Read().Sequence);
    }

    [Fact]
    public async System.Threading.Tasks.Task Reducer_refuses_unclaimed_repeated_and_closed_turn_observations()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var observation = new RunEvent.RootExitObserved(writer.Execution.Launch, new RootExit.Exited(0), At, f.A,
            writer.Execution.Location.Owner.Branch, TipOwnership.Explained);
        Assert.Equal(RunProblem.InvalidClaim, Problem(f.Store.Record(f.Permit, f.Op(), observation)));
        Claim(f, writer);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), observation));
        Assert.Equal(RunProblem.InvalidClaim, Problem(f.Store.Record(f.Permit, f.Op(), observation)));
        using var closed = new RunFixtures(FixtureWorkflow(Writer(T) with { Conversation = IDevelop.Workflows.ConversationMode.Chat }));
        closed.Approve();
        var reservation = closed.Reserve();
        closed.Claim(reservation);
        Assert.IsType<RunDecision.Recorded>(closed.Store.CloseTurn(closed.Permit, closed.Op(), new(A1, 1),
            closed.WriteLog(reservation, conversation: IDevelop.Workflows.ConversationMode.Chat)));
        Assert.Equal(RunProblem.InvalidClaim, Problem(closed.Store.Record(closed.Permit, closed.Op(),
            observation with { Launch = new(A1, 1), Tip = Base })));
        Assert.Single(f.Read().RootExits);
        Assert.Single(closed.Read().TurnClosures);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_rejected_observation_record_fences_the_claim()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        Claim(f, writer);
        var rejected = Assert.IsType<RootObservation.Fenced>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(),
            writer.Execution.Launch, new RootExit.NotStarted(" ")));
        Assert.Equal("InvalidData", rejected.Detail);
        Assert.Equal(new[] { writer.Execution.Launch }, f.Read().Fenced);
        Assert.Equal(RunProblem.UnresolvedOwnership, Assert.IsType<RootObservation.Rejected>(f.Materializer().ObserveRootExit(
            f.Lease(T), f.Op(), writer.Execution.Launch, new RootExit.Exited(0))).Reason.Problem);
        var control = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        Claim(f, control);
        Assert.Equal(new RootExit.Exited(0), Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(U), f.Op(),
            control.Execution.Launch, new RootExit.Exited(0))).Observation.Exit);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("turn")]
    [InlineData("detail")]
    [InlineData("tip")]
    [InlineData("head")]
    [InlineData("ownership")]
    public void Entry_validation_refuses_invalid_observations_and_accepts_both_exit_variants(string damage)
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var prepared = f.Read().Preparations[new(A1, 1)];
        var observation = new RunEvent.RootExitObserved(prepared.Launch, new RootExit.Exited(int.MaxValue), At, Base,
            prepared.Location.Owner.Branch, TipOwnership.Explained);
        var invalid = damage switch
        {
            "attempt" => observation with { Launch = new(default, 1) },
            "turn" => observation with { Launch = new(A1, 0) },
            "detail" => observation with { Exit = new RootExit.NotStarted(" ") },
            "tip" => observation with { Tip = new("HEAD") },
            "head" => observation with { Head = " " },
            "ownership" => observation with { Ownership = (TipOwnership)2 },
            _ => throw new InvalidOperationException(),
        };
        var record = f.Read();
        Assert.Equal(RunProblem.InvalidData, Assert.IsType<RunRead.Rejected>(RunReducer.Apply(W, Run, record,
            new(3, record.Sequence + 1, f.Op(), Prompt, At, invalid))).Reason.Problem);
        var accepted = Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), observation));
        Assert.Equal(new RootExit.Exited(int.MaxValue), Assert.IsType<RunEvent.RootExitObserved>(accepted.Event).Exit);
        var entry = new RunEntry(3, 1, f.Op(), Prompt, At,
            observation with { Exit = new RootExit.NotStarted("Root creation failed."), Head = null });
        var decoded = RunJournal.Decode(RunJournal.Encode(entry));
        Assert.Null(decoded.Rejection);
        var decodedEvent = Assert.IsType<RunEvent.RootExitObserved>(Assert.Single(decoded.Entries).Event);
        Assert.Equal(new RootExit.NotStarted("Root creation failed."), decodedEvent.Exit);
        Assert.Null(decodedEvent.Head);
        foreach (var schema in new[] { 1, 2 })
            Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 1), RunJournal.Decode(RunJournal.Encode(entry with { Schema = schema })).Rejection);
    }

    [Theory]
    [InlineData((int)RunPhase.Approved, true)]
    [InlineData((int)RunPhase.StopRequested, true)]
    [InlineData((int)RunPhase.Abandoned, true)]
    [InlineData((int)RunPhase.Completed, false)]
    [InlineData((int)RunPhase.Stopped, false)]
    [InlineData((int)RunPhase.Failed, false)]
    public void Root_observations_follow_the_turn_closure_phase_permissions(int phase, bool permitted)
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var record = f.Read() with { Phase = (RunPhase)phase };
        var observation = new RunEvent.RootExitObserved(new(A1, 1), new RootExit.Exited(0), At, Base, null, TipOwnership.Explained);
        var applied = RunReducer.Apply(W, Run, record, new(3, record.Sequence + 1, f.Op(), Prompt, At, observation));
        if (permitted)
            Assert.Equal(observation, Assert.IsType<RunRead.Loaded>(applied).Record.RootExits[new(A1, 1)]);
        else
            Assert.Equal(RunProblem.RunStopped, Assert.IsType<RunRead.Rejected>(applied).Reason.Problem);
        var control = RunReducer.Apply(W, Run, record with { Phase = RunPhase.Approved },
            new(3, record.Sequence + 1, f.Op(), Prompt, At, observation));
        Assert.Equal(observation, Assert.IsType<RunRead.Loaded>(control).Record.RootExits[new(A1, 1)]);
    }

    internal static void Claim(PreparationFixture f, Preparation.Ready ready) =>
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(ready.Execution.Location.Owner.Task), f.Op(), ready.Execution.Launch,
            f.Read().Inputs[ready.Execution.Inputs], ready.Execution.PromptHash));

    private static void Commit(PreparationFixture f, Preparation.Ready ready)
    {
        f.Git.Write("b.txt", "B\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "b.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-qm", "b").ExitCode);
        Assert.Equal("2f1d113f78fb3fe0c4c6d9ad1d7dc2788acecf67\n", f.Git.Run(ready.Checkout, "rev-parse", "HEAD").Text);
    }
}
