using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class RestoreTests
{
    [Fact]
    public async Task Preserve_and_restore_an_accepted_baseline()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        Assert.Equal("done\n", Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt)).Result.Report);
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var block = Assert.Single(f.Read().Blocks.Values, b => !b.Resolved).Block;
        Assert.Equal("DirtyWorktree", block.Problem.ToString());
        Assert.Equal("The checkout differs from its recorded baseline.", block.Detail);
        Assert.Equal(new[] { "result.txt" }, block.Scope!.Paths);
        Assert.Equal("late\n", f.Git.Git("show", preserved.Commit.Hex + ":result.txt"));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.Blocked(
            new(f.Op(), T, ready.Execution.Launch.Attempt, MaterializationProblem.DirtyWorktree, ready.Execution.Inputs, [], "Legacy block."))));
        var readSequence = f.Read().Sequence;
        var refsBefore = f.Git.Git("show-ref");
        var preview = Preview(f, ready, preservation);
        Assert.Equal(readSequence, f.Read().Sequence);
        Assert.Equal(refsBefore, f.Git.Git("show-ref"));
        Assert.Equal(new[] { "result.txt" }, preview.Paths.Select(p => p.Path));
        var operation = f.Op();
        var confirmation = f.Op();
        var restored = Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt,
            preservation, confirmation, preview.Identity));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved && b.Block.Scope is not null));
        Assert.Equal("Legacy block.", Assert.Single(f.Read().Blocks.Values, b => !b.Resolved).Block.Detail);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout)))!, "idevelop-restore")));
        Assert.Equal(1, Moves(f, operation));
        Assert.Single(f.Read().Restorations);
        var sequence = f.Read().Sequence;
        Assert.Equal(restored, f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, confirmation, preview.Identity));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [Fact]
    public async Task Restore_a_waiting_baseline()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T) with { Conversation = ConversationMode.Chat }));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "staged\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        var checkpoint = f.ObserveAndLog(ready, "done\n");
        var settled = Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(T), f.Op(), ready.Execution.Launch, checkpoint));
        Assert.IsType<CaptureDisposition.Matched>(settled.Disposition);
        var capture = f.Read().Captures[settled.Capture][0];
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal("staged\n", f.Git.Run(ready.Checkout, "show", ":result.txt").Text);
        Assert.Equal(capture.Index!.Content, GitFixture.Read(f.Git.Open().IndexDigest(ready.Checkout)));
        Assert.Empty(f.Read().Results);
        Assert.Single(f.Read().Restorations);
        Assert.Equal(2, Moves(f, operation));
    }

    [Fact]
    public async Task Restore_resumes_a_pending_publication_from_its_original_capture()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        var publication = f.Op();
        var drift = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), publication, ready.Execution.Launch.Attempt));
        Assert.Equal("DirtyWorktree", drift.Block.Problem.ToString());
        Assert.Equal(new[] { "result.txt" }, drift.Block.Scope!.Paths);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publication, ready.Execution.Launch.Attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal("late\n", f.Git.Git("show", preserved.Commit.Hex + ":result.txt"));
        Assert.Single(f.Read().Results);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
    }

    [Fact]
    public async Task Restore_removes_exactly_the_preserved_extras()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("new.txt", "new\n", ready.Checkout);
        File.AppendAllText(f.Git.PathOf(".git/info/exclude"), "build/\n");
        f.Git.Write("build/out.bin", "ignored\n", ready.Checkout);
        var outbox = RunLayout.Outbox(ready.Execution.Launch.Attempt) + "/report.md";
        f.Git.Write(outbox, "B ready.\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        Assert.Equal(new[] { "new.txt" }, preview.Paths.Select(p => p.Path));
        Assert.Null(Assert.Single(preview.Paths).To);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("ignored\n", File.ReadAllText(Path.Combine(ready.Checkout, "build/out.bin")));
        Assert.Equal("B ready.\n", File.ReadAllText(Path.Combine(ready.Checkout, outbox)));
        Assert.Equal(1, Moves(f, operation));
    }

    [Theory]
    [InlineData("run.sh", "mode", "an executable file", "a file")]
    [InlineData("link", "link", "a symbolic link", "a file")]
    [InlineData("docs", "folder", "a folder", "a file")]
    [InlineData("tool.sh", "executable", "an executable file", "an executable file")]
    public async Task Restore_refuses_type_and_mode_changes_before_any_move(string path, string change, string now, string baseline)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)), configureBase: git =>
        {
            git.Write(path, "before\n");
            if (change == "executable") MakeExecutable(git, git.Folder, path);
            return git.Commit("restore kind");
        });
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        if (change == "mode") MakeExecutable(f.Git, ready.Checkout, path);
        else if (change == "link")
        {
            File.Delete(Path.Combine(ready.Checkout, path));
            File.CreateSymbolicLink(Path.Combine(ready.Checkout, path), "a.txt");
        }
        else if (change == "folder")
        {
            File.Delete(Path.Combine(ready.Checkout, path));
            f.Git.Write(path + "/a.txt", "nested\n", ready.Checkout);
        }
        else f.Git.Write(path, "changed\n", ready.Checkout);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var read = Assert.IsType<RestorePreviewRead.Refused>(f.Materializer().PreviewRestore(f.Lease(T), ready.Execution.Launch.Attempt, preservation));
        var detail = $"Restore changes only regular, non-executable files. {path} is {now} now and {baseline} in the baseline. Change it outside iDevelop, then preserve again.";
        Assert.Equal("DirtyWorktree", read.Problem.ToString());
        Assert.Equal(detail, read.Detail);
        var operation = f.Op();
        var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt,
            preservation, f.Op(), Revision.Hash("refused preview")));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal(detail, blocked.Block.Detail);
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(preserved.Commit, GitFixture.Read(f.Git.Open().ReadRef(preserved.Receipt.Ref)));
        if (change == "folder") Assert.Equal("nested\n", File.ReadAllText(Path.Combine(ready.Checkout, "docs/a.txt")));
        else if (change == "link") Assert.Equal("a.txt", new FileInfo(Path.Combine(ready.Checkout, path)).LinkTarget);
        else Assert.Equal(change == "executable" ? "changed\n" : "before\n", File.ReadAllText(Path.Combine(ready.Checkout, path)));
        if (change is "mode" or "executable")
        {
            f.Git.Write(path, "before\n", ready.Checkout);
            if (change == "mode")
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(ready.Checkout, path), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                Assert.Equal(0, f.Git.Run(ready.Checkout, "update-index", "--chmod=-x", path).ExitCode);
            }
        }
        else
        {
            if (change == "folder") Directory.Delete(Path.Combine(ready.Checkout, path), recursive: true);
            else File.Delete(Path.Combine(ready.Checkout, path));
            f.Git.Write(path, "before\n", ready.Checkout);
        }
        var controlPreservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), controlPreservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, controlPreservation);
        var control = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), control, ready.Execution.Launch.Attempt, controlPreservation, f.Op(), preview.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, Moves(f, control));
    }

    [Fact]
    public async Task A_file_added_after_preservation_blocks_restore()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        var branch = GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch));
        f.Git.Write("extra.txt", "extra\n", ready.Checkout);
        var operation = f.Op();
        var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The checkout changed after it was preserved. Preserve it again.", blocked.Block.Detail);
        Assert.Equal(new[] { "extra.txt" }, blocked.Block.Scope!.Paths);
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("extra\n", File.ReadAllText(Path.Combine(ready.Checkout, "extra.txt")));
        Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        var replacement = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), replacement, ready.Execution.Launch.Attempt));
        var controlPreview = Preview(f, ready, replacement);
        var control = f.Op();
        var raced = Assert.IsType<Restoration.Blocked>(f.Materializer(probe: point =>
        {
            if (point == "git.restore-file-a.txt.before") f.Git.Write("raced.txt", "raced\n", ready.Checkout);
        }).Restore(f.Lease(T), control, ready.Execution.Launch.Attempt, replacement, f.Op(), controlPreview.Identity));
        Assert.Equal("DirtyWorktree", raced.Block.Problem.ToString());
        Assert.Equal(new[] { "raced.txt" }, raced.Block.Scope!.Paths);
        Assert.Equal(0, Moves(f, control));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("raced\n", File.ReadAllText(Path.Combine(ready.Checkout, "raced.txt")));
        var finalPreservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), finalPreservation, ready.Execution.Launch.Attempt));
        var finalPreview = Preview(f, ready, finalPreservation);
        var finalRestore = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), finalRestore, ready.Execution.Launch.Attempt,
            finalPreservation, f.Op(), finalPreview.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "extra.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "raced.txt")));
        Assert.Equal(1, Moves(f, finalRestore));
    }

    [Fact]
    public async Task A_stale_preview_is_refused()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("a.txt", "late\n", ready.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        var operation = f.Op();
        var rejected = Assert.IsType<Restoration.Rejected>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("EvidenceMismatch", rejected.Reason.Problem.ToString());
        Assert.Equal(0, Moves(f, operation));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        var fresh = Preview(f, ready, preservation);
        var control = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), control, ready.Execution.Launch.Attempt, preservation, f.Op(), fresh.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal(1, Moves(f, control));
        Assert.Equal(f.A, GitFixture.Read(f.Git.Open().ReadRef("refs/stash")));
    }

    [Fact]
    public async Task RestoreRef_intents_are_bound_to_the_restoration_plan()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        f.Git.Write("foreign.txt", "foreign\n");
        var foreign = f.Git.Commit("foreign");
        f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, foreign.Hex);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        var planId = OperationIds.Derive(f.Op(), "restore-plan");
        var plan = new MaterializationPlan.Restoration(T, ready.Execution.Launch.Attempt, preservation, preview.Current, preview.To,
            preview.Paths, preview.Repairs, preview.Rechecks, f.Op(), preview.Identity);
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, planId, new RunEvent.Planned(plan)));
        var wrong = new RefChange(ready.Execution.Location.Owner.Branch, f.A, f.A);
        var rejected = Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(), new RunEvent.GitIntended(planId, new GitMutation.RestoreRef(wrong))));
        Assert.Equal("InvalidData", rejected.Reason.Problem.ToString());
        var intent = f.Op();
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, intent, new RunEvent.GitIntended(planId,
            new GitMutation.RestoreRef(new(ready.Execution.Location.Owner.Branch, foreign, f.A)))));
        Assert.False(RefOwnership.Accepts(f.Read(), f.Git.Open(), ready.Execution.Location.Owner.Branch, foreign));
        Assert.True(RefOwnership.Accepts(f.Read(), f.Git.Open(), ready.Execution.Location.Owner.Branch, f.A));
        Assert.Equal(foreign, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Record(f.Permit, f.Op(), new RunEvent.GitObserved(intent, new(false, f.A.Hex))));
        Assert.True(RefOwnership.Accepts(f.Read(), f.Git.Open(), ready.Execution.Location.Owner.Branch, f.A));
    }

    private static void MakeExecutable(GitFixture git, string checkout, string path)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(checkout, path), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.Equal(0, git.Run(checkout, "add", path).ExitCode);
        Assert.Equal(0, git.Run(checkout, "update-index", "--chmod=+x", path).ExitCode);
    }

    internal static RestorePreview Preview(PreparationFixture f, Preparation.Ready ready, OperationId preservation) =>
        Assert.IsType<RestorePreviewRead.Previewed>(f.Materializer().PreviewRestore(f.Lease(ready.Execution.Location.Owner.Task), ready.Execution.Launch.Attempt, preservation)).Preview;

    internal static int Moves(PreparationFixture f, OperationId operation)
    {
        var plan = OperationIds.Derive(operation, "restore-plan");
        var record = f.Read();
        return record.GitObservations.Keys.Count(intent => record.GitIntents[intent].Plan == plan);
    }
}
