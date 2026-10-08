using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class MaterializationTests
{
    private static readonly CommitId WriterCommit = new("2222222222222222222222222222222222222222");
    private static readonly CommitId OtherCommit = new("3333333333333333333333333333333333333333");
    private static readonly CommitId JoinCommit = new("4444444444444444444444444444444444444444");
    private static readonly TreeId Tree = new("5555555555555555555555555555555555555555");

    [Fact]
    public void A_report_only_dependency_selects_the_approved_root()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U)), T, U));
        f.Approve();
        f.Complete(f.Reserve());
        var inputs = f.Reserve(U).Inputs;
        Assert.Equal(new CodeSelection.Root(new("1111111111111111111111111111111111111111")), inputs.Code);
        Assert.Equal(new InputBinding.Provided(new(T, U), ConnectionKind.Dependency, new(Id(103))), Assert.Single(inputs.Bindings));
    }

    [Fact]
    public void Two_forwarders_of_one_writer_keep_both_bindings_and_select_the_smaller_task()
    {
        var workflow = Connect(Connect(Connect(Connect(FixtureWorkflow(Writer(T), Task(U), Task(C), Task(D)), T, U), T, C), U, D), C, D);
        using var f = new RunFixtures(workflow);
        f.Approve();
        Publish(f, f.Reserve(), WriterCommit);
        f.Complete(f.Reserve(U));
        f.Complete(f.Reserve(C));
        var reserved = f.Reserve(D);
        var single = Assert.IsType<CodeSelection.Single>(reserved.Inputs.Code);
        Assert.Equal(U, single.Source.Task);
        Assert.Equal(new[] { T }, single.Source.Owners);
        Assert.Equal("2222222222222222222222222222222222222222", single.Source.Commit.Hex);
        Assert.Equal([U, C], reserved.Inputs.Bindings.OfType<InputBinding.Provided>().Select(binding => binding.Edge.From));
        Assert.Equal(2, reserved.Inputs.Files.Length);
    }

    [Fact]
    public void Two_distinct_commits_require_a_complete_frozen_join()
    {
        using var f = new RunFixtures(Connect(Connect(FixtureWorkflow(Writer(T), Writer(U), Task(D)), T, D), U, D));
        f.Approve();
        Publish(f, f.Reserve(), WriterCommit);
        Publish(f, f.Reserve(U), OtherCommit);
        var operation = f.Op();
        var planned = Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(D), operation, f.Read().Revision.Id, new AttemptCause.Initial()));
        var plan = Assert.IsType<MaterializationPlan.Preparation>(Assert.IsType<RunEvent.Planned>(planned.Event).Plan);
        Assert.Equal(["2222222222222222222222222222222222222222", "3333333333333333333333333333333333333333"], plan.Sources.Select(source => source.Commit.Hex));
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.Reserve(f.Lease(D), f.Op(), operation)));
        var join = new JoinRecord(f.Op(), plan.Sources, JoinCommit, Tree, "refs/idp/join");
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.Reserve(f.Lease(D), f.Op(), operation, join with { Sources = [plan.Sources[0]] })));
        var accepted = Assert.IsType<RunDecision.Created>(f.Store.Reserve(f.Lease(D), f.Op(), operation, join));
        Assert.Equal("4444444444444444444444444444444444444444", Assert.IsType<RunEvent.Reserved>(accepted.Event).Inputs.CodeBase.Hex);
        var sameContent = join with { Sources = [.. plan.Sources] };
        Assert.IsType<RunDecision.Existing>(f.Store.Reserve(f.Lease(D), f.Op(), operation, sameContent));
        Assert.Equal(2, f.Read().Inputs[plan.Inputs].Bindings.Length);
    }

    [Fact]
    public void Delivered_dependency_text_has_literal_report_artifact_and_provenance()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Writer(T), Task(U)), T, U));
        f.Approve();
        Publish(f, f.Reserve(), WriterCommit, "Ready.", [new("payload", "artifacts/payload", new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 3)]);
        var input = f.Reserve(U).Inputs;
        Assert.Equal("""
            ## Plan (dependency)

            Result: 00000000-0000-0000-0000-000000000103
            Code: 2222222222222222222222222222222222222222
            Owner: 00000000-0000-0000-0000-000000000002

            Report:

            Ready.

            Artifacts:

            - payload: .idp/inputs/00000000-0000-0000-0000-000000000104/00000000-0000-0000-0000-000000000103/artifacts/payload (3 bytes, SHA-256 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa)
            """.ReplaceLineEndings("\n"), input.Text);
        Assert.Equal([".idp/inputs/00000000-0000-0000-0000-000000000104/00000000-0000-0000-0000-000000000103/report.md",
            ".idp/inputs/00000000-0000-0000-0000-000000000104/00000000-0000-0000-0000-000000000103/artifacts/payload"], input.Files.Select(file => file.RelativePath));
        Assert.Equal([6L, 3L], input.Files.Select(file => file.ByteLength));
        Assert.Equal("e20846d20bcd60f0f57708fb7e0afdc584cfac4711ea4bb636dfce053ad50136", input.Files[0].Content.Sha256);
    }

    [Fact]
    public void A_report_over_the_utf8_budget_is_delivered_whole_by_path()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U)), T, U));
        f.Approve();
        f.Complete(f.Reserve(), new string('x', 70000));
        var input = f.Reserve(U).Inputs;
        Assert.Equal("""
            ## Plan (dependency)

            Result: 00000000-0000-0000-0000-000000000103

            Report:

            .idp/inputs/00000000-0000-0000-0000-000000000104/00000000-0000-0000-0000-000000000103/report.md (70000 bytes)
            """.ReplaceLineEndings("\n"), input.Text);
        Assert.Equal(70000, input.Files.Single().ByteLength);
        Assert.Equal(70000, f.Read().Results.Single().Report.Length);
    }

    [Fact]
    public void Prepared_prompt_is_required_and_owns_the_claim_hash()
    {
        using var f = new RunFixtures();
        f.Approve();
        var reservation = f.Reserve();
        Assert.Equal(RunProblem.InvalidClaim, Problem(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 1), reservation.Inputs, Prompt)));
        f.Prepare(reservation);
        Assert.Equal(RunProblem.InvalidClaim, Problem(f.Store.Claim(f.Lease(T), f.Op(),
            new(A1, 1), reservation.Inputs, new("0000000000000000000000000000000000000000000000000000000000000000"))));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 1), reservation.Inputs, Prompt));
        Assert.Equal("Inspect", f.Read().Preparations[new(A1, 1)].Prompt);
    }

    [Fact]
    public void Unfinished_publication_blocks_settlement_until_a_block_preserves_it()
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var reservation = f.Reserve();
        Close(f, reservation);
        var operation = f.Op();
        var publication = Publication(reservation, new(Id(300)), WriterCommit);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(publication)));
        Assert.Equal(RunProblem.UnfinishedPublication, Problem(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Stopped)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(new(operation, T, A1,
            MaterializationProblem.DirtyWorktree, reservation.Inputs.Id, [], "Checkout changed."))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Stopped));
        Assert.Equal(RunPhase.Stopped, f.Read().Phase);
    }

    [Fact]
    public void Terminal_maintenance_retains_salvage_but_cannot_move_task_branches_or_publish()
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var reservation = f.Reserve();
        Close(f, reservation);
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Stopped));
        var operation = f.Op();
        const string reference = "refs/idp/salvage/task/00000000-0000-0000-0000-000000000102";
        var salvage = new MaterializationPlan.Salvage(T, A1, Base, Base, null, Recipe(Base), WriterCommit, [], reference);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(salvage)));
        ObserveMove(f, operation, reference, null, WriterCommit);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.SalvageRetained(operation, reference, WriterCommit)));
        Assert.Equal(RunProblem.RunStopped, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(Publication(reservation, new(Id(300)), WriterCommit)))));
        Assert.Equal(RunProblem.RunStopped, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.GitIntended(operation,
            new GitMutation.MoveRef(new("refs/heads/idp/task", Base, WriterCommit))))));
        Assert.Equal(RunProblem.RunStopped, Problem(f.Store.Plan(f.Lease(T), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial())));
        Assert.Equal(RunPhase.Stopped, f.NewStore().Read(W, Run) is RunRead.Loaded loaded ? loaded.Record.Phase : RunPhase.Approved);
    }

    [Fact]
    public void Publication_requires_both_observed_ref_moves_and_one_plan_per_attempt()
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var reservation = f.Reserve();
        Close(f, reservation);
        var operation = f.Op();
        var publication = Publication(reservation, new(Id(300)), WriterCommit);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(publication)));
        Assert.IsType<RunDecision.Existing>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(publication)));
        Assert.Equal(RunProblem.StartConflict, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(publication with
        {
            Result = new(Id(301)),
        }))));
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.AcceptPublication(f.Permit, f.Op(), operation)));
        ObserveMove(f, operation, f.Read().Preparations[new(A1, 1)].Location.Owner.Branch, Base, WriterCommit);
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.AcceptPublication(f.Permit, f.Op(), operation)));
        ObserveMove(f, operation, $"refs/idp/{f.Read().RunKey}/result/{f.Read().TaskKeys[T]}/{A1.Value:D}", null, WriterCommit);
        Assert.IsType<RunDecision.Created>(f.Store.AcceptPublication(f.Permit, f.Op(), operation));
        Assert.IsType<RunDecision.Existing>(f.Store.AcceptPublication(f.Permit, f.Op(), operation));
        Assert.Equal("2222222222222222222222222222222222222222", Assert.IsType<CodeOutput.Produced>(f.Read().Results.Single().Code).Code.Commit.Hex);
    }

    [Fact]
    public void Stale_unreserved_plans_are_replaced_and_the_new_snapshot_is_reused()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Task(), Task(U)), T, U));
        f.Approve();
        var source = f.Reserve();
        var result = f.Complete(source);
        var oldOperation = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(U), oldOperation, f.Read().Revision.Id, new AttemptCause.Initial()));
        Assert.IsType<RunDecision.Existing>(f.Store.Plan(f.Lease(U), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial()));
        f.Complete(f.Reserve(cause: new AttemptCause.Retry(source.Attempt.Id, f.Op())), "Updated.", result.Id);
        Assert.Equal(RunProblem.StaleInput, Problem(f.Store.Reserve(f.Lease(U), f.Op(), oldOperation)));
        var replacement = Assert.IsType<RunDecision.Recorded>(f.Store.Plan(f.Lease(U), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial()));
        var plan = Assert.IsType<MaterializationPlan.Preparation>(Assert.IsType<RunEvent.Planned>(replacement.Event).Plan);
        Assert.Equal("Updated.", f.Read().Results.Single(r => r.Id == ((InputBinding.Provided)plan.Bindings.Single()).Result).Report);
        var repeated = Assert.IsType<RunDecision.Existing>(f.Store.Plan(f.Lease(U), f.Op(), f.Read().Revision.Id, new AttemptCause.Initial()));
        Assert.Equal(plan.Inputs, Assert.IsType<MaterializationPlan.Preparation>(Assert.IsType<RunEvent.Planned>(repeated.Event).Plan).Inputs);
        Assert.Equal(4, f.Read().Plans.Count);
    }

    [Fact]
    public void Retry_preparation_keeps_upstream_code_frozen_and_uses_its_own_published_tip()
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var first = f.Reserve();
        Publish(f, first, WriterCommit);
        var retry = f.Reserve(cause: new AttemptCause.Retry(first.Attempt.Id, f.Op()));
        f.Prepare(retry);
        var prepared = f.Read().Preparations[new(retry.Attempt.Id, 1)];
        Assert.Equal("1111111111111111111111111111111111111111", retry.Inputs.CodeBase.Hex);
        Assert.Equal("2222222222222222222222222222222222222222", prepared.Location.AttemptBase.Hex);
        Assert.Equal(".idp/outbox/00000000-0000-0000-0000-000000000105", prepared.OutboxPath);
    }

    [Fact]
    public void Changed_writer_sources_refuse_a_retry_of_a_consumer()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Writer(T), Task(U)), T, U));
        f.Approve();
        var writer = f.Reserve();
        var old = Publish(f, writer, WriterCommit);
        var consumer = f.Reserve(U);
        f.Complete(consumer);
        var replacement = f.Reserve(cause: new AttemptCause.Retry(writer.Attempt.Id, f.Op()));
        Close(f, replacement, "Updated.", OtherCommit);
        var operation = f.Op();
        var publication = Publication(replacement, f.NextResultId(), OtherCommit, "Updated.") with
        {
            Supersedes = old.Id, VerifiedTip = WriterCommit, Recipe = Recipe(WriterCommit),
        };
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(publication)));
        var record = f.Read();
        ObserveMove(f, operation, record.Preparations[new(replacement.Attempt.Id, 1)].Location.Owner.Branch, WriterCommit, OtherCommit);
        ObserveMove(f, operation, $"refs/idp/{record.RunKey}/result/{record.TaskKeys[T]}/{replacement.Attempt.Id.Value:D}", null, OtherCommit);
        Assert.IsType<RunDecision.Created>(f.Store.AcceptPublication(f.Permit, f.Op(), operation));
        Assert.Equal(RunProblem.StaleInput, Problem(f.Store.Plan(f.Lease(U), f.Op(), record.Revision.Id,
            new AttemptCause.Retry(consumer.Attempt.Id, f.Op()))));
        Assert.Equal("2222222222222222222222222222222222222222", consumer.Inputs.CodeBase.Hex);
    }

    [Fact]
    public void Context_carries_provenance_and_reports_without_artifacts_or_code_sources()
    {
        using var f = new RunFixtures(Connect(FixtureWorkflow(Writer(T), Task(U)), T, U, ConnectionKind.Context));
        f.Approve();
        Publish(f, f.Reserve(), WriterCommit, "Ready.", [new("payload", "artifacts/payload", Prompt, 3)]);
        var reserved = f.Reserve(U);
        Assert.Equal(new CodeSelection.Root(Base), reserved.Inputs.Code);
        Assert.Equal(".idp/inputs/00000000-0000-0000-0000-000000000104/00000000-0000-0000-0000-000000000103/report.md",
            Assert.Single(reserved.Inputs.Files).RelativePath);
        Assert.Contains("## Plan (context)", reserved.Inputs.Text);
        Assert.Contains("Code: 2222222222222222222222222222222222222222", reserved.Inputs.Text);
    }

    [Fact]
    public void Retained_salvage_authorizes_retry_reset_and_blocks_resolve_once()
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var reservation = f.Reserve();
        Close(f, reservation);
        var operation = f.Op();
        const string reference = "refs/idp/salvage/task";
        var file = new EvidenceFile("new.txt", Prompt, 5);
        var salvage = new MaterializationPlan.Salvage(T, A1, Base, Base, null, Recipe(Base), WriterCommit, [file], reference);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(salvage)));
        var reset = new MaterializationPlan.RetryReset(T, A1, operation, Base, Base, [file]);
        Assert.Equal(RunProblem.InvalidData, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(reset))));
        ObserveMove(f, operation, reference, null, WriterCommit);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.SalvageRetained(operation, reference, WriterCommit)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(reset)));
        var block = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, block, new RunEvent.Blocked(new(operation, T, A1,
            MaterializationProblem.UncertainOwnership, reservation.Inputs.Id, [file], "Retained for inspection."))));
        Assert.Equal(RunProblem.InvalidData, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(block, " "))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(block, "Verified ownership.")));
        Assert.True(f.Read().Blocks[block].Resolved);
        Assert.Equal(RunProblem.InvalidData, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.BlockResolved(block, "Verified again."))));
    }

    [Fact]
    public void Stop_requested_finishes_publication_and_refuses_new_preparation()
    {
        using var f = new RunFixtures(FixtureWorkflow(Writer(T)));
        f.Approve();
        var reserved = f.Reserve();
        Close(f, reserved);
        Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        var operation = f.Op();
        var plan = Publication(reserved, f.NextResultId(), WriterCommit);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(plan)));
        var record = f.Read();
        ObserveMove(f, operation, record.Preparations[new(A1, 1)].Location.Owner.Branch, Base, WriterCommit);
        ObserveMove(f, operation, $"refs/idp/{record.RunKey}/result/{record.TaskKeys[T]}/{A1.Value:D}", null, WriterCommit);
        Assert.IsType<RunDecision.Created>(f.Store.AcceptPublication(f.Permit, f.Op(), operation));
        Assert.Equal(RunProblem.RunStopped, Problem(f.Store.Plan(f.Lease(T), f.Op(), record.Revision.Id, new AttemptCause.Retry(A1, f.Op()))));
        Assert.Equal(RunPhase.StopRequested, f.Read().Phase);
    }

    [Fact]
    public void Review_results_forward_the_frozen_subject_without_becoming_a_writer()
    {
        var blueprint = new Blueprint(new("example.review", 1), "Review", new WorkSpec.Review(PromptTemplate.Parse("{{brief}}"),
            PromptTemplate.Parse("{{brief}}")), [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Autonomous));
        var reviewer = new TaskDefinition(U, blueprint) { Title = "Review", Execution = Task().Execution };
        using var f = new RunFixtures(Connect(FixtureWorkflow(Writer(T), reviewer), T, U));
        f.Approve();
        Publish(f, f.Reserve(), WriterCommit);
        var reservation = f.Reserve(U);
        Assert.Equal(new ReviewInput(T, new(Id(103))), reservation.Inputs.Review);
        f.Claim(reservation);
        f.WriteLog(reservation, subject: T, report: "Approved.");
        var folder = f.Store.AttemptFolder(W, Run, U, reservation.Attempt.Id);
        File.AppendAllText(Path.Combine(folder, "events.jsonl"), JsonSerializer.Serialize<AttemptEvent>(new AttemptEvent.Concluded(At, null), AttemptLog.Options) + "\n");
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), reservation.Attempt.Id,
            TerminalAttemptOutcome.Succeeded, Checkpoint(folder)));
        var result = Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(),
            reservation.Attempt.Id, reservation.Inputs.Id, "Approved.")).Event).Result;
        Assert.Equal(new CodeOutput.Forwarded(new(Id(104))), result.Code);
        Assert.Equal("Approved.", result.Report);
        Assert.Equal("", f.Read().Preparations[new(reservation.Attempt.Id, 1)].OutboxPath);
    }

    [Fact]
    public void Later_turns_require_closed_prior_turns_and_match_the_recorded_prompt()
    {
        using var f = new RunFixtures(FixtureWorkflow(Task() with { Conversation = ConversationMode.Chat }));
        f.Approve();
        var reservation = f.Reserve();
        f.Claim(reservation);
        var first = f.Read().Preparations[new(A1, 1)];
        var second = first with { Launch = new(A1, 2), Prompt = "Follow up", PromptHash = Revision.Hash("Follow up") };
        Assert.Equal(RunProblem.InvalidClaim, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.Prepared(second, SharedRefs))));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), new(A1, 1), f.WriteLog(reservation)));
        Assert.Equal(RunProblem.InputConflict, Problem(f.Store.Record(f.Permit, f.Op(), new RunEvent.Prepared(second with
        {
            Location = second.Location with { Owner = second.Location.Owner with { Branch = "refs/heads/foreign" } },
        }, SharedRefs))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Prepared(second, SharedRefs)));
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(T), f.Op(), new(A1, 2), reservation.Inputs, second.PromptHash));
        Assert.Equal("Follow up", f.Read().Preparations[new(A1, 2)].Prompt);
    }

    private static TaskDefinition Writer(TaskId task)
    {
        var blueprint = new Blueprint(new("example.writer", 1), "Writer", new WorkSpec.Agent(AgentAccess.Edit, false, PromptTemplate.Parse("{{brief}}")),
            [new("brief", "Brief", FieldShape.Text, true, "Inspect")], new(null, ConversationMode.Autonomous));
        return new(task, blueprint) { Title = "Plan", Execution = Task().Execution };
    }

    private static CommitRecipe Recipe(CommitId parent) => new(Tree, [parent], "Fixture", "Test <test@example.invalid>",
        "Test <test@example.invalid>", At);

    private static MaterializationPlan.Publication Publication(RunEvent.Reserved reservation, ResultId result, CommitId commit,
        string report = "Checked.", ImmutableArray<ArtifactRecord> artifacts = default) =>
        new(reservation.Attempt.Id, result, null, reservation.Inputs.CodeBase, null, Recipe(reservation.Inputs.CodeBase), commit, report,
            artifacts.IsDefault ? [] : [.. artifacts.Select(artifact => artifact with { StoredPath = RunStorage.ArtifactPath(result, artifact.Name) })])
        { Capture = new(reservation.Attempt.Id.Value) };

    private static void Close(RunFixtures f, RunEvent.Reserved reservation, string report = "Checked.",
        CommitId? candidate = null, ImmutableArray<ArtifactRecord> artifacts = default)
    {
        f.Claim(reservation);
        var prepared = f.Read().Preparations[new(reservation.Attempt.Id, 1)];
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.RootExitObserved(
            prepared.Launch, new RootExit.Exited(0), At, prepared.Location.AttemptBase, prepared.Location.Owner.Branch, TipOwnership.Explained)));
        var log = f.WriteLog(reservation, report: report);
        var capture = new CaptureId(reservation.Attempt.Id.Value);
        foreach (var ordinal in new[] { 1, 2 })
        {
            var at = ordinal == 1 ? At : At.AddMilliseconds(250);
            var frozenArtifacts = artifacts.IsDefault ? [] : artifacts.Select(artifact => artifact with
                { StoredPath = RunStorage.CapturePath(capture, ordinal, "artifacts/" + artifact.Name) }).ToImmutableArray();
            Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.TurnCaptured(new(capture, ordinal,
                prepared.Launch, log, at, at, Recipe(prepared.Location.AttemptBase), candidate ?? WriterCommit,
                prepared.Location.AttemptBase, prepared.Location.Owner.Branch, null, report, frozenArtifacts,
                new(RunStorage.CapturePath(capture, ordinal, "refs.json"), Prompt, 10), []))));
        }
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.CaptureDisposed(capture, prepared.Launch,
            new CaptureDisposition.Matched())));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseTurn(f.Permit, f.Op(), prepared.Launch, log, capture));
        Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), reservation.Attempt.Id,
            TerminalAttemptOutcome.Succeeded, log));
    }

    private static ResultRecord Publish(RunFixtures f, RunEvent.Reserved reservation, CommitId commit, string report = "Checked.",
        ImmutableArray<ArtifactRecord> artifacts = default)
    {
        Close(f, reservation, report, commit, artifacts);
        var operation = f.Op();
        var publication = Publication(reservation, f.NextResultId(), commit, report, artifacts);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, operation, new RunEvent.Planned(publication)));
        var record = f.Read();
        ObserveMove(f, operation, record.Preparations[new(reservation.Attempt.Id, 1)].Location.Owner.Branch, publication.VerifiedTip, commit);
        ObserveMove(f, operation, $"refs/idp/{record.RunKey}/result/{record.TaskKeys[reservation.Attempt.Task]}/{reservation.Attempt.Id.Value:D}", null, commit);
        return Assert.IsType<RunEvent.ResultAccepted>(Assert.IsType<RunDecision.Created>(f.Store.AcceptPublication(f.Permit, f.Op(), operation)).Event).Result;
    }

    private static void ObserveMove(RunFixtures f, OperationId plan, string reference, CommitId? expected, CommitId target)
    {
        var mutation = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, mutation, new RunEvent.GitIntended(plan, new GitMutation.MoveRef(new(reference, expected, target)))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.GitObserved(mutation, new(false, target.Hex))));
    }
}
