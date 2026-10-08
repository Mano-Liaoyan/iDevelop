using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class PublicationEvidenceTests
{
    [Fact]
    public async Task Schema_three_plans_must_equal_the_settled_capture_and_a_valid_plan_delivers_frozen_bytes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f, artifact: true);
        var capture = f.Read().Settlements[ready.Execution.Launch];
        var frozen = f.Read().Captures[capture][0];
        var result = new ResultId(Id(300));
        var artifact = Assert.Single(frozen.Artifacts) with { StoredPath = RunStorage.ArtifactPath(result, "payload") };
        var plan = new MaterializationPlan.Publication(ready.Execution.Launch.Attempt, result, null, frozen.Tip!.Value,
            frozen.Index?.Content, frozen.Recipe, frozen.Candidate, "B ready.\n", [artifact]) { Capture = capture };
        var invalid = new (MaterializationPlan.Publication Plan, string Problem)[]
        {
            (plan with { Capture = null }, "OutcomeMismatch"),
            (plan with { Capture = new(Id(999)) }, "OutcomeMismatch"),
            (plan with { VerifiedTip = f.A }, "EvidenceMismatch"),
            (plan with { IndexBefore = null }, "EvidenceMismatch"),
            (plan with { Recipe = plan.Recipe with { Message = "Different." } }, "EvidenceMismatch"),
            (plan with { Commit = f.A }, "EvidenceMismatch"),
            (plan with { Report = "Different." }, "OutcomeMismatch"),
            (plan with { Artifacts = [artifact with { Name = "other" }] }, "EvidenceMismatch"),
            (plan with { Artifacts = [artifact with { Content = Revision.Hash(new byte[] { 88 }) }] }, "EvidenceMismatch"),
            (plan with { Artifacts = [artifact with { ByteLength = 1 }] }, "EvidenceMismatch"),
            (plan with { Artifacts = [artifact with { StoredPath = "other/payload" }] }, "EvidenceMismatch"),
        };
        var journal = Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "events.jsonl");
        var bytes = File.ReadAllBytes(journal);
        foreach (var entry in invalid)
            Assert.Equal(entry.Problem, Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(entry.Plan))).Reason.Problem.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(journal));
        Assert.Empty(f.Read().Results);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Planned(plan)));
        File.WriteAllBytes(Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "payload.bin"), [88]);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("new\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":new.txt"));
        Assert.Equal(new byte[] { 67, 0, 127 }, new RunStorage(f.Git.Folder, W, f.RunId).ReadArtifact(result, Assert.Single(accepted.Result.Artifacts)));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Replay_recopies_a_missing_result_artifact_from_the_capture_and_refuses_changed_capture_bytes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f, artifact: true);
        var operation = new OperationId(Id(2000));
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.plan.after") throw new Crash(); })
            .Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        var plan = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Publication>());
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var artifact = Assert.Single(plan.Artifacts);
        var destination = RunStorage.SafePath(storage.Folder, artifact.StoredPath);
        File.Delete(destination);
        File.WriteAllBytes(Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "payload.bin"), [88]);
        var frozen = Assert.Single(f.Read().Captures[plan.Capture!.Value][0].Artifacts);
        var source = RunStorage.SafePath(storage.Folder, frozen.StoredPath);
        File.WriteAllBytes(source, [88]);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Contains(frozen.StoredPath, blocked.Block.Detail);
        Assert.False(File.Exists(destination));
        Assert.Empty(f.Read().Results);
        File.WriteAllBytes(source, [67, 0, 127]);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), operation, ready.Execution.Launch.Attempt));
        Assert.Equal(new byte[] { 67, 0, 127 }, storage.ReadArtifact(accepted.Result.Id, Assert.Single(accepted.Result.Artifacts)));
        Assert.Single(f.Read().Results);
    }
}
