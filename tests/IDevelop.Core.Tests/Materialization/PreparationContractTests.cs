using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class PreparationContractTests
{
    [Theory]
    [InlineData("{{brief}}", null)]
    [InlineData("{{brief}}\n\n{{inputs}}", null)]
    [InlineData("{{brief}}\n\n{{inputs}}", "Supplied prompt.")]
    public async Task Prompt_delivers_the_report_once_with_and_without_the_template_placeholder(string template, string? prompt)
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U, template)), T, U));
        await f.Publish(T, f.A, artifact: true, artifactName: "report.md");
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: prompt));
        Assert.StartsWith(prompt ?? "Inspect", ready.Execution.Prompt);
        Assert.Equal(1, ready.Execution.Prompt.Split("B ready.", StringSplitOptions.None).Length - 1);
        Assert.Contains("Code: d4d26ecdf72779dbc9c5c983025fb51546c8f9ea", ready.Execution.Prompt);
        Assert.Equal(new byte[] { 67, 0, 127 }, File.ReadAllBytes(Path.Combine(ready.Checkout,
            ".idp/inputs/00000000-0000-0000-0000-000000000103/0a07c9c1-332b-8f5f-a0fb-3d93fb49f99b/artifacts/report.md")));
        Assert.Equal("B ready.\n", File.ReadAllText(Path.Combine(ready.Checkout,
            ".idp/inputs/00000000-0000-0000-0000-000000000103/0a07c9c1-332b-8f5f-a0fb-3d93fb49f99b/report.md")));
        var prepared = f.Read().Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Prepared>().Single(e => e.Execution == ready.Execution);
        var snapshot = File.ReadAllText(Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, prepared.SharedRefs.RelativePath));
        Assert.Contains("refs/idp/93f23689/base", snapshot);
        Assert.Contains("refs/heads/idp/93f23689/task/90d5b0a2", snapshot);
        Assert.Contains("adfe40b30c176fb407933286f51d15ea9b54cdc3", snapshot);
    }

    [Fact]
    public async Task Review_requires_a_supplied_prompt_then_prepares_the_subject_snapshot_without_an_outbox()
    {
        var review = Task(U, work: new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")));
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), review), T, U));
        var result = await f.Publish(T, f.A);
        var operation = f.Op();
        Assert.Equal("InvalidData", Assert.IsType<Preparation.Rejected>(await f.Prepare(U, operation)).Reason.Problem.ToString());
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U, operation, prompt: "Review the changes."));
        Assert.StartsWith("Review the changes.\n\n## B (dependency)", ready.Execution.Prompt);
        Assert.Equal(1, ready.Execution.Prompt.Split("B ready.", StringSplitOptions.None).Length - 1);
        Assert.Equal("", ready.Execution.OutboxPath);
        Assert.Equal(new ReviewInput(T, result.Id), f.Read().Inputs[ready.Execution.Inputs].Review);
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Tracked_reserved_paths_block_before_key_allocation_and_a_changed_base_ref_blocks_reuse()
    {
        using var tracked = new PreparationFixture(FixtureWorkflow(Writer(T)));
        tracked.Git.Write(".idp/inputs/owned.txt", "tracked\n");
        tracked.Git.Git("add", "-f", ".idp/inputs/owned.txt");
        var block = Assert.IsType<Preparation.Blocked>(await tracked.Prepare(T));
        Assert.Equal("InputUnavailable", block.Block.Problem.ToString());
        Assert.Contains(".idp/inputs/owned.txt", block.Block.Detail);
        Assert.Null(tracked.Read().RunKey);
        Assert.Equal("tracked\n", File.ReadAllText(Path.Combine(tracked.Git.Folder, ".idp/inputs/owned.txt")));
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var operation = f.Op();
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T, operation));
        f.Git.Git("update-ref", "refs/idp/93f23689/base", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        Assert.Equal("UncertainOwnership", Assert.IsType<Preparation.Blocked>(await f.Prepare(T, operation)).Block.Problem.ToString());
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Layout_intents_are_limited_to_base_retention_and_prepared_evidence_must_be_in_run_storage()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var record = f.Read();
        var layout = record.Receipts.Single(pair => pair.Value.Event is RunEvent.LayoutAllocated { Key: LayoutKey.Run }).Key;
        Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(W, f.RunId, f.Op(), new RunEvent.GitIntended(layout,
            new GitMutation.MoveRef(new("refs/idp/foreign/base", null, f.A))))).Reason.Problem.ToString());
        Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(W, f.RunId, f.Op(), new RunEvent.Prepared(ready.Execution,
            new("results/foreign/refs.json", Revision.Hash("{}"), 2)))).Reason.Problem.ToString());
        var prepared = new RunEvent.Prepared(ready.Execution, new("evidence/00000000-0000-0000-0000-000000000001/refs.json", Revision.Hash("{}"), 2));
        var entry = new RunEntry(2, 1, f.Op(), Revision.Hash("fixture"), At, prepared);
        var encoded = RunJournal.Encode(entry);
        Assert.Contains("\"sharedRefs\":{\"relativePath\":\"evidence/00000000-0000-0000-0000-000000000001/refs.json\"", encoded);
        Assert.Equal(prepared, Assert.IsType<RunEvent.Prepared>(Assert.Single(RunJournal.Decode(encoded).Entries).Event));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }
    [Fact]
    public async Task Prepared_append_rechecks_frozen_inputs_under_the_journal_lock()
    {
        var workflow = Connect(Connect(FixtureWorkflow(Writer(T), Writer(U), Writer(C)), T, U), C, U, ConnectionKind.Context);
        using var f = new PreparationFixture(workflow);
        await f.Publish(T, f.A);
        var operation = f.Op();
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.prepared.before") throw new Crash();
        }).Prepare(W, f.RunId, operation, U, new AttemptCause.Initial()));
        var context = await f.Publish(C, f.A, "C available.\n");
        var record = f.Read();
        var input = record.Inputs.Values.Single(input => input.Task == U);
        var attempt = record.Attempts.Values.Single(attempt => attempt.Task == U);
        var owner = record.GitIntents.Values.Select(intent => intent.Mutation).OfType<GitMutation.CreateWorktree>().Single(create => create.Owner.Task == U).Owner;
        var prepared = new PreparedExecution(new(attempt.Id, 1), input.Id, new(owner, new("d4d26ecdf72779dbc9c5c983025fb51546c8f9ea")), "Inspect", Revision.Hash("Inspect"), RunLayout.Outbox(attempt.Id));
        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Record(W, f.RunId, f.Op(), new RunEvent.Prepared(prepared, SharedRefs)));
        Assert.Equal("InputConflict", rejected.Reason.Problem.ToString());
        Assert.Equal("C available.\n", context.Report);
        Assert.Equal(0, f.Read().Preparations.Values.Count(execution => f.Read().Attempts[execution.Launch.Attempt].Task == U));
        Assert.Equal("A\n", File.ReadAllText(f.Git.PathOf(".worktrees/93f23689/" + record.TaskKeys[U] + "/a.txt")));
    }

    private sealed class Crash : Exception;

}
