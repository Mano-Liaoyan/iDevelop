using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.JoinTests;
using Crash = IDevelop.Core.Tests.Materialization.PublicationTests.Crash;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class JoinRecoveryTests
{
    private const string JoinRef = "refs/heads/idp/93f23689/join/c67f2fc3";

    private static async Task ThreeSources(PreparationFixture f, bool conflict)
    {
        await Sources(f);
        var third = Assert.IsType<Preparation.Ready>(await Open(f).Prepare(f.Lease(D), f.Op(), new AttemptCause.Initial()));
        OwnCommit(f, third, conflict ? "b.txt" : "e.txt", conflict ? "different\n" : "E\n", "e");
        f.Close(third);
        Assert.IsType<Publication.Accepted>(Open(f).Publish(f.Lease(third.Execution.Location.Owner.Task), f.Op(), third.Execution.Launch.Attempt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_input_joins_recover_after_every_crash_and_unreachable_object_prune(bool conflict)
    {
        var points = new List<string>();
        byte[]? stdout = null;
        byte[]? stderr = null;
        ConflictEvidence? expectedEvidence = null;
        using (var baseline = new PreparationFixture(Diamond(true)))
        {
            await ThreeSources(baseline, conflict);
            var outcome = await Prepare(baseline, baseline.Op(), points.Add);
            if (conflict)
            {
                var block = Assert.IsType<Preparation.Blocked>(outcome).Block;
                Assert.Equal("FanInConflict", block.Problem.ToString());
                Assert.Equal(2, block.Conflict!.Step);
                Assert.Equal(new[] { "b.txt" }, block.Conflict.Paths);
                stdout = Evidence(baseline, block.Conflict.Stdout);
                stderr = Evidence(baseline, block.Conflict.Stderr);
                expectedEvidence = block.Conflict;
            }
            else Assert.Equal("3cbffb776a80266eba7a89046eab81561047b47c", Assert.IsType<Preparation.Ready>(outcome).Execution.Location.AttemptBase.Hex);
        }
        var joinPoints = points.Where(point => point.Contains("join", StringComparison.Ordinal)).ToArray();
        Assert.Contains("git.join-accumulator-1.after", joinPoints);
        Assert.Contains("git.join-merge-2.after", joinPoints);
        if (conflict)
        {
            Assert.Contains("git.join-evidence-stdout.before", joinPoints);
            Assert.Contains("git.join-evidence-stdout.after", joinPoints);
            Assert.Contains("git.join-evidence-stderr.before", joinPoints);
            Assert.Contains("git.join-evidence-stderr.after", joinPoints);
        }
        foreach (var point in joinPoints.Where(point => point == "git.join-merge-1.before" || point.EndsWith(".after", StringComparison.Ordinal)))
            await CrashAndRetry(point, false);
        await CrashAndRetry("git.join-accumulator-1.after", true);

        async Task CrashAndRetry(string point, bool fresh)
        {
            using var f = new PreparationFixture(Diamond(true));
            await ThreeSources(f, conflict);
            var protectedRefs = f.Git.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/idp/", "refs/heads/idp/")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' '))
                .Where(pair => !pair[0].Contains("/join/", StringComparison.Ordinal)).ToDictionary(pair => pair[0], pair => pair[1]);
            var operation = f.Op();
            await Assert.ThrowsAsync<Crash>(async () => await Prepare(f, operation, step => { if (step == point) throw new Crash(); }));
            f.Git.Git("prune", "--expire=now");
            var retryOperation = fresh ? f.Op() : operation;
            var retry = await Prepare(f, retryOperation);
            if (conflict)
            {
                var block = Assert.IsType<Preparation.Blocked>(retry).Block;
                Assert.Equal("FanInConflict", block.Problem.ToString());
                Assert.Equal(2, block.Conflict!.Step);
                Assert.Equal(new[] { "b.txt" }, block.Conflict.Paths);
                Assert.Equal(stdout, Evidence(f, block.Conflict.Stdout));
                Assert.Equal(stderr, Evidence(f, block.Conflict.Stderr));
                Assert.Equal(RunJournal.Canonical(fresh ? Strip(expectedEvidence!) : expectedEvidence),
                    RunJournal.Canonical(fresh ? Strip(block.Conflict) : block.Conflict));
                Assert.Null(GitFixture.Read(f.Git.Open().ReadRef(JoinRef)));
            }
            else
            {
                var ready = Assert.IsType<Preparation.Ready>(retry);
                Assert.Equal("3cbffb776a80266eba7a89046eab81561047b47c", ready.Execution.Location.AttemptBase.Hex);
                Assert.Equal("3cbffb776a80266eba7a89046eab81561047b47c", GitFixture.Read(f.Git.Open().ReadRef(JoinRef))?.Hex);
                Assert.Equal("B\n", File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")));
                Assert.Equal("C\n", File.ReadAllText(Path.Combine(ready.Checkout, "c.txt")));
                Assert.Equal("E\n", File.ReadAllText(Path.Combine(ready.Checkout, "e.txt")));
                Assert.Equal(ready, await Prepare(f, retryOperation));
                var record = f.Read();
                Assert.Single(record.Plans.Values.OfType<MaterializationPlan.Join>());
                var intent = Assert.Single(record.GitIntents, pair => pair.Value.Plan == OperationIds.Derive(retryOperation, "join"));
                Assert.Equal("3cbffb776a80266eba7a89046eab81561047b47c", record.GitObservations[intent.Key].Value);
                Assert.Equal(1, record.Receipts.Values.Count(entry => entry.Event is RunEvent.GitObserved observed && observed.Mutation == intent.Key));
            }
            foreach (var (name, value) in protectedRefs) Assert.Equal(value, GitFixture.Read(f.Git.Open().ReadRef(name))?.Hex);
            Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef("refs/heads/main"))?.Hex);
        }
    }

    private static ConflictEvidence Strip(ConflictEvidence evidence) => evidence with
    {
        Stdout = evidence.Stdout with { RelativePath = "" },
        Stderr = evidence.Stderr with { RelativePath = "" },
    };

    private static byte[] Evidence(PreparationFixture f, EvidenceFile file) =>
        RunStorage.Read(new RunStorage(f.Git.Folder, W, f.RunId).Folder, file.RelativePath, file.Content, file.ByteLength);
}
