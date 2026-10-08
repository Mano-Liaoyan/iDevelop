using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class ClaimFreshnessTests
{
    [Fact]
    public async System.Threading.Tasks.Task A_dependency_superseded_after_preparation_rejects_the_first_claim_and_keeps_the_preparation()
    {
        var workflow = Connect(FixtureWorkflow(Task(T), Task(U).WithField("brief", "Build B")!), T, U);
        using var f = new PreparationFixture(workflow);
        var producer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var first = Accept(f, producer, "A ready.\n");
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var input = f.Read().Inputs[ready.Execution.Inputs];
        Assert.Equal(new InputId(Id(104)), input.Id);
        Assert.Equal(first.Id, Assert.Single(input.Bindings.OfType<InputBinding.Provided>()).Result);
        Assert.Contains("Build B", ready.Execution.Prompt);
        var retry = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.Retry(producer.Execution.Launch.Attempt, f.Op())));
        var second = Accept(f, retry, "A2\n", first.Id);
        Assert.Equal("A2\n", f.Read().CurrentResults[T].Report);
        Assert.Equal(first.Id, second.Supersedes);

        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Claim(f.Lease(U), f.Op(), ready.Execution.Launch, input, ready.Execution.PromptHash));
        Assert.Equal("StaleInput", rejected.Reason.Problem.ToString());
        Assert.Equal(T, rejected.Reason.Task);
        Assert.Equal(0, f.Read().Claims.Keys.Count(key => key.Attempt == ready.Execution.Launch.Attempt));
        Assert.Equal(ready.Execution, f.Read().Preparations[ready.Execution.Launch]);
        Assert.Equal(new InputId(Id(104)), f.Read().Preparations[ready.Execution.Launch].Inputs);

        using var control = new PreparationFixture(workflow);
        Accept(control, Assert.IsType<Preparation.Ready>(await control.Prepare(T)), "A ready.\n");
        var fresh = Assert.IsType<Preparation.Ready>(await control.Prepare(U));
        Assert.IsType<RunDecision.Granted>(control.Store.Claim(control.Lease(U), control.Op(), fresh.Execution.Launch,
            control.Read().Inputs[fresh.Execution.Inputs], fresh.Execution.PromptHash));
        Assert.Equal(1, control.Read().Claims.Keys.Count(key => key.Attempt == fresh.Execution.Launch.Attempt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task Missing_or_superseded_context_stays_frozen_and_does_not_block_a_claim(bool provided)
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Task(T), Task(U)), T, U, ConnectionKind.Context));
        Preparation.Ready? producer = null;
        ResultRecord? first = null;
        if (provided)
        {
            producer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            first = Accept(f, producer, "A ready.\n");
        }
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        var input = f.Read().Inputs[ready.Execution.Inputs];
        if (provided) Assert.Equal(first!.Id, Assert.IsType<InputBinding.Provided>(Assert.Single(input.Bindings)).Result);
        else Assert.Equal(new ConnectionKey(T, U), Assert.IsType<InputBinding.MissingContext>(Assert.Single(input.Bindings)).Edge);
        var updated = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: provided ? new AttemptCause.Retry(producer!.Execution.Launch.Attempt, f.Op()) : new AttemptCause.Initial()));
        Accept(f, updated, "A2\n", first?.Id);

        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(U), f.Op(), ready.Execution.Launch, input, ready.Execution.PromptHash));
        var recorded = f.Read().Inputs[ready.Execution.Inputs];
        Assert.Equal(input.Id, recorded.Id);
        Assert.Equal(input.Text, recorded.Text);
        if (provided) Assert.Equal(first!.Id, Assert.IsType<InputBinding.Provided>(Assert.Single(recorded.Bindings)).Result);
        else Assert.Equal(new ConnectionKey(T, U), Assert.IsType<InputBinding.MissingContext>(Assert.Single(recorded.Bindings)).Edge);
        Assert.Equal(ready.Execution, f.Read().Preparations[ready.Execution.Launch]);
        Assert.Equal(1, f.Read().Claims.Keys.Count(key => key.Attempt == ready.Execution.Launch.Attempt));
    }

    [Fact]
    public void A_current_dependency_result_that_becomes_transitively_stale_rejects_the_first_claim()
    {
        var workflow = Connect(Connect(FixtureWorkflow(Task(T), Task(U), Task(C), Task(D)), T, U), U, C);
        using var f = new RunFixtures(workflow);
        f.Approve();
        var upstream = f.Reserve(T);
        var first = f.Complete(upstream, "A ready.\n");
        var producer = f.Reserve(U);
        var result = f.Complete(producer, "B ready.\n");
        var consumer = f.Reserve(C);
        f.Prepare(consumer);
        var retry = f.Reserve(T, new AttemptCause.Retry(upstream.Attempt.Id, f.Op()));
        f.Complete(retry, "A2\n", first.Id);
        Assert.Equal(result.Id, f.Read().CurrentResults[U].Id);
        Assert.Contains(result.Id, f.Read().StaleResults);

        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Claim(f.Lease(C), f.Op(), new(consumer.Attempt.Id, 1), consumer.Inputs, Prompt));
        Assert.Equal("StaleInput", rejected.Reason.Problem.ToString());
        Assert.Equal(U, rejected.Reason.Task);
        Assert.Equal(0, f.Read().Claims.Keys.Count(key => key.Attempt == consumer.Attempt.Id));
        var independent = f.Reserve(D);
        f.Claim(independent);
        Assert.Equal(1, f.Read().Claims.Keys.Count(key => key.Attempt == independent.Attempt.Id));
        Assert.Equal(3, f.Read().Schema);
    }

    [Fact]
    public void A_repeated_first_claim_and_a_later_turn_keep_their_inputs_after_a_dependency_is_superseded()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(T), Task(U) with { Conversation = ConversationMode.Chat }), T, U));
        f.Approve();
        var producer = f.Reserve(T);
        var first = f.Complete(producer, "A ready.\n");
        var consumer = f.Reserve(U);
        f.Claim(consumer);
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), new(consumer.Attempt.Id, 1),
            f.WriteLog(consumer, report: "More?", conversation: ConversationMode.Chat)));
        f.Prepare(consumer, turn: 2, prompt: "Continue.");
        var retry = f.Reserve(T, new AttemptCause.Retry(producer.Attempt.Id, f.Op()));
        f.Complete(retry, "A2\n", first.Id);

        Assert.IsType<RunDecision.Existing>(f.Store.Claim(f.Lease(U), f.Op(), new(consumer.Attempt.Id, 1), consumer.Inputs, Prompt));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(U), f.Op(), new(consumer.Attempt.Id, 2), consumer.Inputs, Revision.Hash("Continue.")));
        Assert.Equal(2, f.Read().Claims.Keys.Count(key => key.Attempt == consumer.Attempt.Id));
        Assert.Equal(first.Id, Assert.IsType<InputBinding.Provided>(Assert.Single(f.Read().Inputs[consumer.Inputs.Id].Bindings)).Result);
        Assert.Equal(3, f.Read().Schema);
    }

    private static ResultRecord Accept(PreparationFixture f, Preparation.Ready ready, string report, ResultId? supersedes = null)
    {
        f.Close(ready, report);
        return Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit,
            f.Op(), ready.Execution.Launch.Attempt, ready.Execution.Inputs, report, supersedes)).Event).Result;
    }
}
