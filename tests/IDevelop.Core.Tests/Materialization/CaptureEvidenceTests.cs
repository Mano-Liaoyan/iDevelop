using System.Text;
using System.Text.Json;
using IDevelop.Execution;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class CaptureEvidenceTests
{
    [Fact]
    public async Task A_stash_after_root_exit_is_retained_in_both_observations_and_the_publication_block()
    {
        foreach (var stash in new[] { true, false })
        {
            using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var log = f.ObserveAndLog(ready);
            string? stashTip = null;
            if (stash)
            {
                f.Git.Write("a.txt", "stash\n", ready.Checkout);
                Assert.Equal(0, f.Git.Run(ready.Checkout, "stash", "push", "-qm", "capture evidence").ExitCode);
                stashTip = f.Git.Run(ready.Checkout, "rev-parse", "refs/stash").Text.Trim();
                Assert.Equal(0, f.Git.Run(ready.Checkout, "cat-file", "-e", stashTip).ExitCode);
            }
            var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, log));
            var record = f.Read();
            var observations = record.Captures[closed.Capture];
            Assert.Equal(2, observations.Count);
            var storage = new RunStorage(f.Git.Folder, W, f.RunId);
            var prepared = record.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Prepared>().Single().SharedRefs;
            const string baseline = "{\"refs/heads/idp/93f23689/task/90d5b0a2\":{\"hex\":\"adfe40b30c176fb407933286f51d15ea9b54cdc3\"},\"refs/idp/93f23689/base\":{\"hex\":\"adfe40b30c176fb407933286f51d15ea9b54cdc3\"}}";
            Assert.Equal(baseline, Encoding.UTF8.GetString(Read(storage, prepared)));
            foreach (var observation in observations)
            {
                Assert.Equal($"captures/{closed.Capture.Value:D}/{observation.Ordinal}/refs.json", observation.SharedRefs.RelativePath);
                var bytes = Read(storage, observation.SharedRefs);
                Assert.Equal(stash ? baseline[..^1] + ",\"refs/stash\":{\"hex\":\"" + stashTip + "\"}}" : baseline, Encoding.UTF8.GetString(bytes));
                var refs = JsonSerializer.Deserialize<SortedDictionary<string, CommitId>>(bytes, RunJournal.Options)!;
                if (stash) Assert.Equal(stashTip, refs["refs/stash"].Hex);
                else Assert.False(refs.ContainsKey("refs/stash"));
            }
            Assert.IsType<RunDecision.Recorded>(f.Store.CloseAttempt(f.Permit, f.Op(), ready.Execution.Launch.Attempt,
                TerminalAttemptOutcome.Succeeded, log));
            var publication = f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt);
            if (stash)
            {
                var divergence = Assert.IsType<CaptureDisposition.Diverged>(closed.Disposition);
                Assert.Equal(MaterializationProblem.UncertainOwnership, divergence.Problem);
                Assert.Equal(new[] { "refs/stash" }, divergence.Refs);
                var block = Assert.IsType<Publication.Blocked>(publication).Block;
                Assert.Equal(MaterializationProblem.UncertainOwnership, block.Problem);
                Assert.Equal(3, block.Evidence.Length);
                Assert.Equal(new[] { prepared, observations[0].SharedRefs, observations[1].SharedRefs }, block.Evidence);
                Assert.Equal($"The capture contains unexplained shared refs. Paths or refs: refs/stash: prepared absent, observation 1 {stashTip}, observation 2 {stashTip}", block.Detail);
                Assert.Empty(f.Read().Results);
            }
            else
            {
                Assert.IsType<CaptureDisposition.Matched>(closed.Disposition);
                Assert.Equal("B ready.\n", Assert.IsType<Publication.Accepted>(publication).Result.Report);
                Assert.Single(f.Read().Results);
            }
        }
    }

    [Fact]
    public async Task A_capture_ref_evidence_path_outside_its_observation_is_rejected()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var observation = f.Read().Captures[f.Read().Settlements[ready.Execution.Launch]][0];
        var invalid = observation with { SharedRefs = observation.SharedRefs with { RelativePath = "elsewhere/refs.json" } };
        Assert.Equal(RunProblem.InvalidData, Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.TurnCaptured(invalid))).Reason.Problem);
        Assert.Equal(2, f.Read().Captures[observation.Capture].Count);
        Assert.Equal("B ready.\n", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(),
            ready.Execution.Launch.Attempt)).Result.Report);
    }

    [Fact]
    public async Task A_capture_rejects_an_invalid_index_tree_object_id()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        var observation = f.Read().Captures[f.Read().Settlements[ready.Execution.Launch]][0];
        var invalid = observation with { IndexTree = new("not-an-object-id") };
        Assert.Equal(RunProblem.InvalidData, Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
            new RunEvent.TurnCaptured(invalid))).Reason.Problem);
        Assert.NotNull(observation.IndexTree);
        Assert.Equal(2, f.Read().Captures[observation.Capture].Count);
        Assert.Equal("B ready.\n", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(),
            ready.Execution.Launch.Attempt)).Result.Report);
    }

    private static byte[] Read(RunStorage storage, EvidenceFile file) =>
        RunStorage.Read(storage.Folder, file.RelativePath, file.Content, file.ByteLength);
}
