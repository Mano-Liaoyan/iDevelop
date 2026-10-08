using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class CapturePinTests
{
    [Fact]
    public async Task A_rewritten_writer_tip_and_candidate_survive_gc_until_settled_pins_are_released()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        RootExitTests.Claim(f, writer);
        f.Git.Write("result.txt", "writer\n", writer.Checkout);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "add", "result.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "commit", "-qm", "writer").ExitCode);
        await f.Close(writer);
        var record = f.Read();
        var root = record.RootExits[writer.Execution.Launch].Tip;
        var observations = record.Captures[record.Settlements[writer.Execution.Launch]];
        var prefix = RunLayout.PinPrefix(record.RunKey!);
        var taskKey = record.TaskKeys[T];
        var pins = GitFixture.Read(f.Git.Open().RefSnapshot(prefix));
        Assert.Equal(3, pins.Count);
        Assert.Equal(root, pins[$"{prefix}{taskKey}/{writer.Execution.Launch.Attempt.Value:D}/1/root"]);
        Assert.Equal(observations[0].Candidate, pins[$"{prefix}{taskKey}/{writer.Execution.Launch.Attempt.Value:D}/1/capture-1"]);
        Assert.Equal(observations[1].Candidate, pins[$"{prefix}{taskKey}/{writer.Execution.Launch.Attempt.Value:D}/1/capture-2"]);
        Assert.Equal(RunProblem.NotSettled, Assert.IsType<PinRelease.Rejected>(f.Materializer().ReleasePins(f.Permit, f.Op())).Reason.Problem);
        Assert.Equal(3, GitFixture.Read(f.Git.Open().RefSnapshot(prefix)).Count);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "-q", "--hard", writer.Execution.Location.AttemptBase.Hex).ExitCode);
        f.Git.Write("foreign.txt", "foreign\n", writer.Checkout);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "add", "foreign.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(writer.Checkout, "commit", "-qm", "foreign").ExitCode);
        Assert.Equal(MaterializationProblem.UncertainOwnership, Assert.IsType<Publication.Blocked>(f.Materializer().Publish(
            f.Lease(T), f.Op(), writer.Execution.Launch.Attempt)).Block.Problem);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "reflog", "expire", "--expire=now", "--all").ExitCode);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "gc", "-q", "--prune=now").ExitCode);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "cat-file", "-e", root.Hex).ExitCode);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "cat-file", "-e", observations[0].Candidate.Hex).ExitCode);
        Assert.Equal("writer\n", f.Git.Git("show", observations[0].Candidate.Hex + ":result.txt"));
        Assert.Equal(0, f.Git.Run(writer.Checkout, "reset", "-q", "--hard", root.Hex).ExitCode);
        Assert.IsType<Salvage.Retained>(f.Materializer().Salvage(f.Lease(T), f.Op(), writer.Execution.Launch.Attempt));
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        f.Git.Write("sibling.txt", "sibling\n", sibling.Checkout);
        await f.Close(sibling);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(U), f.Op(), sibling.Execution.Launch.Attempt));
        Assert.Equal("sibling\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":sibling.txt"));
        Assert.Single(f.Read().Results);
        var retained = GitFixture.Read(f.Git.Open().RefSnapshot($"refs/idp/{record.RunKey}/result/", $"refs/idp/{record.RunKey}/salvage/"));
        Assert.Equal(2, retained.Count);
        Assert.Equal(RunPhase.Failed, Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Failed)).Record.Phase);
        var sequence = f.Read().Sequence;
        var operation = f.Op();
        Assert.Equal(6, Assert.IsType<PinRelease.Released>(f.Materializer().ReleasePins(f.Permit, operation)).Count);
        Assert.Empty(GitFixture.Read(f.Git.Open().RefSnapshot(prefix)));
        Assert.Equal(0, Assert.IsType<PinRelease.Released>(f.Materializer().ReleasePins(f.Permit, operation)).Count);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(retained, GitFixture.Read(f.Git.Open().RefSnapshot($"refs/idp/{record.RunKey}/result/", $"refs/idp/{record.RunKey}/salvage/")));
    }

    [Fact]
    public async Task Foreign_pin_values_do_not_change_sibling_capture_or_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(writer);
        var record = f.Read();
        var pin = RunLayout.RootPin(record.RunKey!, record.TaskKeys[T], writer.Execution.Launch);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "update-ref", pin, f.Git.Git("rev-parse", "HEAD~1").Trim()).ExitCode);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        f.Git.Write("sibling.txt", "sibling\n", sibling.Checkout);
        await f.Close(sibling);
        var settled = f.Read();
        var capture = settled.Settlements[sibling.Execution.Launch];
        Assert.IsType<CaptureDisposition.Matched>(settled.Dispositions[capture].Disposition);
        Assert.Equal(2, settled.Captures[capture].Count);
        var prepared = settled.Receipts.Values.Select(entry => entry.Event).OfType<RunEvent.Prepared>()
            .Single(entry => entry.Execution.Launch == sibling.Execution.Launch).SharedRefs;
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        foreach (var evidence in new[] { prepared }.Concat(settled.Captures[capture].Select(observation => observation.SharedRefs)))
        {
            var bytes = RunStorage.Read(storage.Folder, evidence.RelativePath, evidence.Content, evidence.ByteLength);
            Assert.DoesNotContain("/pin/", System.Text.Encoding.UTF8.GetString(bytes));
            Assert.Contains("refs/idp/93f23689/base", System.Text.Encoding.UTF8.GetString(bytes));
        }
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(U), f.Op(), sibling.Execution.Launch.Attempt));
        Assert.Equal("sibling\n", f.Git.Git("show", Assert.IsType<CodeOutput.Produced>(accepted.Result.Code).Code.Commit.Hex + ":sibling.txt"));
        Assert.Single(f.Read().Results);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("capture-1")]
    public async Task Pin_failures_fence_root_exit_or_fail_the_observation_and_controls_settle(string step)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(U)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        RootExitTests.Claim(f, writer);
        var record = f.Read();
        var pin = step == "root" ? RunLayout.RootPin(record.RunKey!, record.TaskKeys[T], writer.Execution.Launch)
            : RunLayout.CapturePin(record.RunKey!, record.TaskKeys[T], writer.Execution.Launch, 1);
        var locked = Path.Combine(f.Git.Folder, ".git", pin + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(locked)!);
        File.WriteAllText(locked, "lock\n");
        if (step == "root")
        {
            var failed = Assert.IsType<RootObservation.Fenced>(f.Materializer().ObserveRootExit(f.Lease(T), f.Op(),
                writer.Execution.Launch, new RootExit.Exited(0)));
            Assert.Contains("cannot lock ref", failed.Detail);
            Assert.Equal(new[] { writer.Execution.Launch }, f.Read().Fenced);
            Assert.Empty(f.Read().RootExits);
        }
        else
        {
            var log = f.ObserveAndLog(writer);
            var closed = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), writer.Execution.Launch, log));
            var failed = Assert.IsType<CaptureDisposition.Failed>(closed.Disposition);
            Assert.Equal(MaterializationProblem.GitFailed, failed.Problem);
            Assert.Contains("cannot lock ref", failed.Detail);
            Assert.Equal(0, f.Read().Captures.Values.Sum(observations => observations.Count));
            Assert.Equal("refs.json", Path.GetFileName(Assert.Single(failed.Evidence).RelativePath));
        }
        Assert.Equal("lock\n", File.ReadAllText(locked));
        var control = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        await f.Close(control);
        Assert.Equal(1, f.Read().RootExits.Count(exit => exit.Key == control.Execution.Launch));
        Assert.IsType<CaptureDisposition.Matched>(f.Read().Dispositions[f.Read().Settlements[control.Execution.Launch]].Disposition);
    }

    [Fact]
    public async Task Unrecorded_pins_are_replaced_and_a_failed_release_retries_without_erasing_a_changed_pin()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var writer = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var record = f.Read();
        var rootPin = RunLayout.RootPin(record.RunKey!, record.TaskKeys[T], writer.Execution.Launch);
        var capturePin = RunLayout.CapturePin(record.RunKey!, record.TaskKeys[T], writer.Execution.Launch, 1);
        var other = f.Git.Git("rev-parse", "HEAD~1").Trim();
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "update-ref", rootPin, other).ExitCode);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "update-ref", capturePin, other).ExitCode);
        f.Git.Write("result.txt", "writer\n", writer.Checkout);
        await f.Close(writer);
        record = f.Read();
        var candidate = record.Captures[record.Settlements[writer.Execution.Launch]][0].Candidate;
        Assert.Equal(record.RootExits[writer.Execution.Launch].Tip, GitFixture.Read(f.Git.Open().ReadRef(rootPin)));
        Assert.Equal(candidate, GitFixture.Read(f.Git.Open().ReadRef(capturePin)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Failed));
        var changed = false;
        var failed = Assert.IsType<PinRelease.Failed>(f.Materializer(probe: point =>
        {
            if (point != "git.release-pin.before" || changed) return;
            changed = true;
            Assert.Equal(0, f.Git.Run(f.Git.Folder, "update-ref", capturePin, other).ExitCode);
        }).ReleasePins(f.Permit, f.Op()));
        Assert.Equal(MaterializationProblem.GitFailed, failed.Problem);
        Assert.Equal(other, GitFixture.Read(f.Git.Open().ReadRef(capturePin))!.Value.Hex);
        Assert.Equal(3, GitFixture.Read(f.Git.Open().RefSnapshot(RunLayout.PinPrefix(record.RunKey!))).Count);
        Assert.Equal(3, Assert.IsType<PinRelease.Released>(f.Materializer().ReleasePins(f.Permit, f.Op())).Count);
        Assert.Empty(GitFixture.Read(f.Git.Open().RefSnapshot(RunLayout.PinPrefix(record.RunKey!))));
        Assert.Equal("writer\n", f.Git.Git("show", candidate.Hex + ":result.txt"));
    }
}
