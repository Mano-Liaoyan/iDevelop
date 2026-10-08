using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IDevelop.Core.Tests.Materialization;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Runs;

public sealed class E3a2JournalTests
{
    [Fact]
    public async System.Threading.Tasks.Task E3a2_interrupted_continue_replays()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var bytes = File.ReadAllBytes(Fixture.Path("e3a2-interrupted-continue/events.jsonl"));
        File.WriteAllBytes(Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl"), bytes);
        var folder = f.Store.AttemptFolder(W, f.RunId, T, ready.Execution.Launch.Attempt);
        Directory.CreateDirectory(folder);
        File.Copy(Fixture.Path("e3a2-interrupted-continue/attempt-events.jsonl"), Path.Combine(folder, "events.jsonl"), overwrite: true);
        Assert.Equal(20L, f.Read().Sequence);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(string.Concat(RunJournal.Decode(bytes).Entries.Select(RunJournal.Encode))));
        Assert.IsType<AttemptCause.Continue>(Assert.Single(f.Read().Attempts.Values, a => a.Id != ready.Execution.Launch.Attempt).Cause);
        Assert.Empty(f.Read().Baselines);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var fresh = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        await RecoveryBaselineTests.CloseInterrupted(control, fresh);
        var confirmation = control.Op();
        Assert.Equal("RecoveryEvidenceInsufficient", Assert.IsType<RunDecision.Rejected>(control.Store.Plan(control.Lease(T), control.Op(),
            control.Read().Revision.Id, new AttemptCause.Continue(fresh.Execution.Launch.Attempt, confirmation))).Reason.Problem.ToString());
        Assert.Equal(0, control.Read().Plans.Values.OfType<MaterializationPlan.Preparation>().Count(p => p.Cause is AttemptCause.Continue));
        var preservation = control.Op();
        Assert.IsType<Preservation.Preserved>(await control.Materializer().Preserve(control.Lease(T), preservation, fresh.Execution.Launch.Attempt));
        Assert.IsType<RecoveryBaselining.Recorded>(control.Materializer().RecordRecoveryBaseline(control.Lease(T), control.Op(),
            fresh.Execution.Launch.Attempt, confirmation, preservation));
        Assert.IsType<RunDecision.Recorded>(control.Store.Plan(control.Lease(T), control.Op(), control.Read().Revision.Id,
            new AttemptCause.Continue(fresh.Execution.Launch.Attempt, confirmation)));
        Assert.Equal(1, control.Read().Plans.Values.OfType<MaterializationPlan.Preparation>().Count(p => p.Cause is AttemptCause.Continue));
    }

    [LinuxOrWindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task E3a2_publication_drift_clears_after_a_full_restore_with_explained_shared_refs(bool foreignRef)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var attempt = ready.Execution.Launch.Attempt;
        var journal = Encoding.UTF8.GetString(File.ReadAllBytes(Fixture.Path("e3a2-publication-drift/events.jsonl")));
        File.WriteAllText(Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl"),
            Regex.Replace(journal, "\"repository\":\"[^\"]*\"", "\"repository\":" + JsonSerializer.Serialize(f.Read().Repository)));
        File.Copy(Fixture.Path("e3a2-publication-drift/attempt-events.jsonl"),
            Path.Combine(f.Store.AttemptFolder(W, f.RunId, T, attempt), "events.jsonl"), overwrite: true);
        Assert.Equal(18L, f.Read().Sequence);
        var legacy = Assert.Single(f.Read().Blocks);
        Assert.Same(BlockScope.Unrecorded.Value, legacy.Value.Block.Scope);
        Assert.Equal("Writer files or index changed after the turn-end capture.",
            Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt)).Block.Detail);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var drift = OperationIds.Derive(preservation, "preserve-drift");
        var foreign = $"refs/heads/idp/{f.Read().RunKey}/foreign";
        if (foreignRef) f.Git.Git("update-ref", foreign, f.A.Hex);
        var preview = RestoreTests.Preview(f, ready, preservation);
        Assert.Equal(new[] { legacy.Key, drift }, preview.Repairs);
        Assert.Equal(foreignRef ? new[] { drift } : new[] { legacy.Key, drift }, Assert.IsType<Restoration.Restored>(f.Materializer().Restore(
            f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity)).Receipt.Resolved);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        if (foreignRef)
        {
            Assert.False(f.Read().Blocks[legacy.Key].Resolved);
            Assert.Equal("Writer files or index changed after the turn-end capture.",
                Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt)).Block.Detail);
            f.Git.Git("update-ref", "-d", foreign);
            var again = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), again, attempt));
            var retry = RestoreTests.Preview(f, ready, again);
            Assert.Equal(new[] { legacy.Key }, retry.Repairs);
            Assert.Equal(new[] { legacy.Key }, Assert.IsType<Restoration.Restored>(f.Materializer().Restore(
                f.Lease(T), f.Op(), attempt, again, f.Op(), retry.Identity)).Receipt.Resolved);
        }
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt)).Result.Report);
    }

    [Theory]
    [InlineData("e3a2-orphaned-turn", 14L)]
    [InlineData("e3a2-orphaned-attempt", 13L)]
    [InlineData("e3a2-published", 25L)]
    public async System.Threading.Tasks.Task E3a2_journals_replay_unchanged(string fixture, long sequence)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var attempt = ready.Execution.Launch.Attempt;
        var bytes = File.ReadAllBytes(Fixture.Path(fixture + "/events.jsonl"));
        File.WriteAllBytes(Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl"), bytes);
        var folder = f.Store.AttemptFolder(W, f.RunId, T, attempt);
        Directory.CreateDirectory(folder);
        File.Copy(Fixture.Path(fixture + "/attempt-events.jsonl"), Path.Combine(folder, "events.jsonl"), overwrite: true);
        var decoded = RunJournal.Decode(bytes);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(string.Concat(decoded.Entries.Select(RunJournal.Encode))));
        Assert.Equal(new[] { "Closed" }, Assert.IsType<RecoveryRead.Loaded>(f.Store.InspectRecovery(W, f.RunId)).Attempts.Select(a => a.State.ToString()));
        var publication = f.Materializer().Publish(f.Lease(T), f.Op(), attempt);
        if (fixture == "e3a2-published")
        {
            Assert.Equal("260451a9-9c11-8952-a74e-b445c5c73a72", Assert.IsType<Publication.Accepted>(publication).Result.Id.Value.ToString("D"));
            Assert.Equal(1, f.Read().Results.Count(result => result.Task == T));
            Assert.Equal("260451a9-9c11-8952-a74e-b445c5c73a72", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(
                f.Lease(T), f.Op(), attempt)).Result.Id.Value.ToString("D"));
        }
        else
        {
            Assert.Equal("OutcomeMismatch", Assert.IsType<Publication.Rejected>(publication).Reason.Problem.ToString());
            Assert.Equal(0, f.Read().Results.Count(result => result.Task == T));
            if (fixture == "e3a2-orphaned-turn")
            {
                Assert.Equal("EvidenceMismatch", Assert.IsType<Settlement.Rejected>(await f.Materializer().RecoverSettlement(
                    f.Lease(T), f.Op(), ready.Execution.Launch)).Reason.Problem.ToString());
                var closure = f.Read().TurnClosures[ready.Execution.Launch];
                Assert.Null(Assert.IsType<RunEvent.TurnClosed>(Assert.IsType<RunDecision.Existing>(f.Store.CloseTurn(f.Permit, new OperationId(Id(9100)),
                    ready.Execution.Launch, closure)).Event).Capture);
            }
        }
        Assert.Equal(sequence, f.Read().Sequence);
        using var control = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var fresh = Assert.IsType<Preparation.Ready>(await control.Prepare(T));
        control.Git.Write("result.txt", "done\n", fresh.Checkout);
        var checkpoint = control.ObserveAndLog(fresh);
        Assert.Equal("SettlementPending", Assert.IsType<RunDecision.Rejected>(control.Store.CloseTurn(control.Permit, control.Op(),
            fresh.Execution.Launch, checkpoint)).Reason.Problem.ToString());
        Assert.Equal("SettlementPending", Assert.IsType<RunDecision.Rejected>(control.Store.CloseAttempt(control.Permit, control.Op(),
            fresh.Execution.Launch.Attempt, TerminalAttemptOutcome.Succeeded, checkpoint)).Reason.Problem.ToString());
        Assert.IsType<CaptureDisposition.Matched>(Assert.IsType<Settlement.Closed>(await control.Materializer().Settle(
            control.Lease(T), control.Op(), fresh.Execution.Launch, checkpoint)).Disposition);
        Assert.IsType<RunDecision.Recorded>(control.Store.CloseAttempt(control.Permit, control.Op(), fresh.Execution.Launch.Attempt,
            TerminalAttemptOutcome.Succeeded, checkpoint));
        Assert.Equal("done\n", control.Git.Git("show", Assert.IsType<CodeOutput.Produced>(Assert.IsType<Publication.Accepted>(
            control.Materializer().Publish(control.Lease(T), control.Op(), fresh.Execution.Launch.Attempt)).Result.Code).Code.Commit.Hex + ":result.txt"));
        Assert.Equal(1, control.Read().Results.Count(result => result.Task == T));
    }
}
