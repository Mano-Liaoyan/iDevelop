using System.Text;
using System.Text.Json;
using IDevelop.Execution;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class CaptureEvidenceTests
{
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
        Assert.Single(f.Read().Captures[observation.Capture]);
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
        Assert.Single(f.Read().Captures[observation.Capture]);
        Assert.Equal("B ready.\n", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(),
            ready.Execution.Launch.Attempt)).Result.Report);
    }

    private static byte[] Read(RunStorage storage, EvidenceFile file) =>
        RunStorage.Read(storage.Folder, file.RelativePath, file.Content, file.ByteLength);
}
