using System.Text;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class E2JournalTests
{
    [Fact]
    public void Genuine_E1_history_and_attempt_log_remain_readable_and_byte_identical()
    {
        using var f = new RunFixtures();
        using var authority = new RunFixtures();
        authority.Approve();
        var bytes = File.ReadAllBytes(Fixture.Path("e1-run/events.jsonl"));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        var decoded = RunJournal.Decode(bytes);
        Assert.Null(decoded.Rejection);
        var record = Assert.IsType<RunRead.Loaded>(f.Store.Read(W, Run)).Record;
        Assert.Equal((1, RunPhase.StopRequested, 9L), (record.Schema, record.Phase, record.Sequence));
        var input = record.Inputs[new(Id(104))];
        Assert.Equal("1111111111111111111111111111111111111111", input.CodeBase.Hex);
        Assert.Equal(new CodeSelection.Legacy(Base), input.Code);
        Assert.Equal("Plan ready.", record.Results.Single(result => result.Id == new ResultId(Id(103))).Report);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(string.Concat(decoded.Entries.Select(RunJournal.Encode))));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Plan(authority.Lease(U), new OperationId(Id(2001)), record.Revision.Id, new AttemptCause.Initial())));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Reserve(authority.Lease(U), new OperationId(Id(2002)), new OperationId(Id(1006)))));
        var folder = f.Store.AttemptFolder(W, Run, U, new(Id(105)));
        Directory.CreateDirectory(folder);
        File.Copy(Fixture.Path("e1-run/attempt-events.jsonl"), Path.Combine(folder, "events.jsonl"));
        var attempt = AttemptLog.ReadAttempt(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, U, new(Id(105)));
        Assert.Equal((AttemptStatus.Succeeded, "Done."), (attempt!.Status, attempt.Result));
        var receipt = record.Receipts[new(Id(1009))];
        Assert.IsType<RunDecision.Existing>(f.Store.Stop(new LegacyRun(W, Run), receipt.Operation));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(new LegacyRun(W, Run), new OperationId(Id(2003)), RunOutcome.Stopped));
        Assert.Equal(1, f.Read().Receipts.Values.Single(entry => entry.Sequence == 10).Schema);
    }

    [Fact]
    public void Genuine_E2_history_replays_exactly_but_cannot_plan_or_take_control()
    {
        using var f = new RunFixtures();
        using var authority = new RunFixtures();
        authority.Approve();
        var bytes = File.ReadAllBytes(Fixture.Path("e2-run/events.jsonl"));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        var decoded = RunJournal.Decode(bytes);
        Assert.Null(decoded.Rejection);
        var record = f.Read();
        Assert.Equal((2, 27L, 1), (record.Schema, record.Sequence, record.Results.Count));
        Assert.Equal(new[] { new LaunchKey(new(Id(104)), 1) }, record.UnresolvedClaims);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(string.Concat(decoded.Entries.Select(RunJournal.Encode))));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Plan(authority.Lease(U), new OperationId(Id(2001)), record.Revision.Id, new AttemptCause.Initial())));
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Reserve(authority.Lease(U), new OperationId(Id(2002)), new OperationId(Id(2001)))));
        var planned = record.Receipts.Values.First(entry => entry.Event is RunEvent.Planned { Plan: MaterializationPlan.Preparation });
        var plan = Assert.IsType<MaterializationPlan.Preparation>(((RunEvent.Planned)planned.Event).Plan);
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Plan(authority.Lease(plan.Task), planned.Operation, plan.Revision, plan.Cause)));
        var reserved = record.Receipts.Values.First(entry => entry.Event is RunEvent.Reserved);
        Assert.Equal(RunProblem.UnsupportedSchema, Problem(f.Store.Reserve(authority.Lease(U), reserved.Operation, planned.Operation)));
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema),
            Assert.IsType<ControlTake.Rejected>(f.Store.TakeControl(W, Run)).Reason);
        Assert.Equal(bytes, File.ReadAllBytes(f.Journal(W, Run)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(f.Journal(W, Run))!, "control.lock")));
        using var current = new RunFixtures();
        current.Approve();
        Assert.Equal(3, current.Read().Schema);
        var owned = Assert.IsType<ControlTake.Owned>(current.Store.TakeControl(W, Run));
        using (owned.Permit) Assert.True(owned.Permit.Held);
    }

    [Fact]
    public void Schema_two_unresolved_claim_keeps_confirmed_recovery_and_its_original_schema()
    {
        using var f = new RunFixtures();
        File.WriteAllBytes(f.Journal(W, Run), File.ReadAllBytes(Fixture.Path("e2-run/events.jsonl")));
        Assert.Equal(new[] { new LaunchKey(new(Id(104)), 1) }, f.Read().UnresolvedClaims);
        using var lease = StandaloneLease.TryTake(f.Project, U)!;
        var recovered = Assert.IsType<RunDecision.Recorded>(f.Store.Recover(new LegacyRun(W, Run), lease, new OperationId(Id(2003)), new(Id(104)), RecoveryOutcome.Stopped, new OperationId(Id(2004)), "Stopped."));
        Assert.Equal((2, 28L), (recovered.Record.Schema, recovered.Record.Sequence));
        Assert.Equal(RecoveryOutcome.Stopped,
            Assert.IsType<AttemptEnd.Recovered>(recovered.Record.Closures[new(Id(104))]).Outcome);
        Assert.Equal(2, recovered.Record.Receipts.Values.Single(entry => entry.Sequence == 28).Schema);
        Assert.Empty(f.Read().UnresolvedClaims);
    }

    [Fact]
    public void Schema_two_ownership_fence_is_refused_at_sequence_28()
    {
        using var f = new RunFixtures();
        var bytes = File.ReadAllBytes(Fixture.Path("e2-run/events.jsonl"));
        File.WriteAllBytes(f.Journal(W, Run), bytes);
        Assert.Equal((2, 27L), (f.Read().Schema, f.Read().Sequence));
        const string fence = """
            {"schema":2,"sequence":28,"operation":"00000000-0000-0000-0000-000000009999","command":{"sha256":"e0723a86a5b9408aee9113031785d3d15d9702892f31a46e5fe927c0c8552675"},"at":"2026-10-07T00:00:00+00:00","event":{"type":"ownershipFenced","claims":[{"attempt":"00000000-0000-0000-0000-000000000104","turn":1}]}}
            """;
        File.AppendAllText(f.Journal(W, Run), fence + "\n");
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 28),
            Assert.IsType<RunRead.Rejected>(f.Store.Read(W, Run)).Reason);
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 28),
            Assert.IsType<ControlTake.Rejected>(f.Store.TakeControl(W, Run)).Reason);
    }

    [Fact]
    public void Joined_reserved_codec_matches_a_handwritten_schema_two_line()
    {
        const string line = """
            {"schema":2,"sequence":2,"operation":"00000000-0000-0000-0000-000000000090","command":{"sha256":"e0723a86a5b9408aee9113031785d3d15d9702892f31a46e5fe927c0c8552675"},"at":"2026-10-07T00:00:00+00:00","event":{"type":"reserved","attempt":{"id":"00000000-0000-0000-0000-000000000102","task":"00000000-0000-0000-0000-000000000005","revision":{"sha256":"4e59fcd81eea359ae4d1680bef9cb518a5b9d7a1a9b49e0b936062f90b4d3d7a"},"initialInputs":"00000000-0000-0000-0000-000000000101","cause":{"type":"initial"}},"inputs":{"id":"00000000-0000-0000-0000-000000000101","task":"00000000-0000-0000-0000-000000000005","revision":{"sha256":"4e59fcd81eea359ae4d1680bef9cb518a5b9d7a1a9b49e0b936062f90b4d3d7a"},"bindings":[],"code":{"type":"joined","join":{"operation":"00000000-0000-0000-0000-000000000091","sources":[{"task":"00000000-0000-0000-0000-000000000002","result":"00000000-0000-0000-0000-000000000103","owners":["00000000-0000-0000-0000-000000000002"],"attemptBase":{"hex":"1111111111111111111111111111111111111111"},"commit":{"hex":"2222222222222222222222222222222222222222"}},{"task":"00000000-0000-0000-0000-000000000003","result":"00000000-0000-0000-0000-000000000106","owners":["00000000-0000-0000-0000-000000000003"],"attemptBase":{"hex":"1111111111111111111111111111111111111111"},"commit":{"hex":"3333333333333333333333333333333333333333"}}],"commit":{"hex":"4444444444444444444444444444444444444444"},"tree":{"hex":"5555555555555555555555555555555555555555"},"ref":"refs/idp/join"}},"text":"","files":[],"review":null}}}
            """;
        var input = new InputRecord(new(Id(101)), D, new(V1), [], new CodeSelection.Joined(new(new(Id(91)),
            [new(T, new(Id(103)), [T], Base, new("2222222222222222222222222222222222222222")),
                new(U, new(Id(106)), [U], Base, new("3333333333333333333333333333333333333333"))],
            new("4444444444444444444444444444444444444444"), new("5555555555555555555555555555555555555555"), "refs/idp/join")), "", [], null);
        var entry = new RunEntry(2, 2, new(Id(90)), Prompt, At,
            new RunEvent.Reserved(new(A1, D, new(V1), input.Id, new AttemptCause.Initial()), input));
        Assert.Equal(line + "\n", RunJournal.Encode(entry));
        var approved = new RunEntry(2, 1, new(Id(89)), Prompt, At, new RunEvent.Approved(Run,
            Revision.Capture(FixtureWorkflow()), new(Base, BaseChoice.Head)));
        var decoded = RunJournal.Decode(RunJournal.Encode(approved) + line + "\n");
        Assert.Null(decoded.Rejection);
        Assert.Equal(RunJournal.Canonical(entry), RunJournal.Canonical(decoded.Entries[1]));
        foreach (var owners in new[] { "[]", "[\"00000000-0000-0000-0000-000000000000\"]",
            "[\"00000000-0000-0000-0000-000000000002\",\"00000000-0000-0000-0000-000000000002\"]",
            "[\"00000000-0000-0000-0000-000000000003\",\"00000000-0000-0000-0000-000000000002\"]" })
        {
            var invalid = line.Replace("\"owners\":[\"00000000-0000-0000-0000-000000000002\"]", "\"owners\":" + owners, StringComparison.Ordinal);
            Assert.Equal(new RunRejection(RunProblem.InvalidData, 2), RunJournal.Decode(RunJournal.Encode(approved) + invalid + "\n").Rejection);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Mixed_run_schemas_are_refused_on_replay(int legacySchema)
    {
        using var f = new RunFixtures();
        f.Approve();
        var approved = RunJournal.Decode(File.ReadAllBytes(f.Journal(W, Run))).Entries[0];
        var stop = new RunEntry(legacySchema, 2, f.Op(), Prompt, At, new RunEvent.StopRequested());
        var read = RunReducer.Replay(W, Run, RunJournal.Decode(RunJournal.Encode(approved) + RunJournal.Encode(stop)).Entries);
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 2), Assert.IsType<RunRead.Rejected>(read).Reason);
        var old = approved with { Schema = legacySchema };
        var current = stop with { Schema = 3 };
        Assert.Equal(new RunRejection(RunProblem.UnsupportedSchema, 2),
            Assert.IsType<RunRead.Rejected>(RunReducer.Replay(W, Run, [old, current])).Reason);
        var matching = stop with { Schema = 3 };
        var valid = Assert.IsType<RunRead.Loaded>(RunReducer.Replay(W, Run, [approved, matching])).Record;
        Assert.Equal((3, 2L, RunPhase.StopRequested), (valid.Schema, valid.Sequence, valid.Phase));
    }

    [Fact]
    public void Layout_keys_are_hash_prefixes_of_the_complete_identity()
    {
        using var f = new RunFixtures();
        var run = new RunId(Guid.Parse("019a9d2e-0000-7000-8000-000000000001"));
        f.Approve(run: run);
        Assert.Equal(RunProblem.InvalidData, Problem(f.Store.Record(f.PermitFor(run), f.Op(), new RunEvent.LayoutAllocated(
            new LayoutKey.Run("3940f0a6", f.Project)))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.PermitFor(run), f.Op(), new RunEvent.LayoutAllocated(new LayoutKey.Run("3940f0a5", f.Project))));
        Assert.Equal("3940f0a5", f.Read(run).RunKey);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mixed")]
    public void Input_shapes_keep_strict_unknown_member_handling(string damage)
    {
        var bytes = File.ReadAllText(Fixture.Path("e1-run/events.jsonl"));
        var changed = bytes.Replace("\"codeBase\":", damage == "unknown" ? "\"extra\":0,\"codeBase\":" :
            "\"files\":[],\"codeBase\":", StringComparison.Ordinal);
        Assert.Equal(new RunRejection(RunProblem.InvalidData, 2), RunJournal.Decode(changed).Rejection);
        Assert.Equal(9, RunJournal.Decode(bytes).Entries.Length);
    }

    [Fact]
    public void Schema_one_refuses_new_events_and_schema_two_refuses_legacy_inputs()
    {
        Assert.Equal(new RunRejection(RunProblem.UnsupportedEvent, 1),
            RunJournal.Decode("""{"schema":1,"sequence":1,"event":{"type":"planned"}}""" + "\n").Rejection);
        var legacy = File.ReadAllText(Fixture.Path("e1-run/events.jsonl"));
        Assert.Equal(new RunRejection(RunProblem.InvalidData, 2), RunJournal.Decode(legacy.Replace("\"schema\":1", "\"schema\":2")).Rejection);
        Assert.Equal(9, RunJournal.Decode(legacy).Entries.Length);
    }

    [Fact]
    public void Derived_operation_ids_have_literal_version_eight_identity()
    {
        var operation = OperationIds.Derive(new(Id(90)), "reserve");
        Assert.Equal("a8a4d054-f849-8ca8-8a34-49ee945b9779", operation.Value.ToString("D"));
        Assert.Equal(operation, OperationIds.Derive(new(Id(90)), "reserve"));
    }
}
