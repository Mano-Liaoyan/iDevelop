using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.Workflows;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class RestoreTests
{
    private sealed class Crash : Exception;

    private sealed record RestoreCase(Preparation.Ready Ready, OperationId Preservation, OperationId Operation,
        OperationId Confirmation, RestorePreview Preview, string IndexPath, string ScratchRoot);

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

    [Theory]
    [InlineData("a", "journal.branch-intent.after", "DirtyWorktree", 0, 0)]
    [InlineData("b", "git.branch.after", "DirtyWorktree", 0, 0)]
    [InlineData("c", "journal.index-intent.after", "DirtyWorktree", 0, 1)]
    [InlineData("d", "journal.branch-intent.after", "UncertainOwnership", 1, 0)]
    public async Task Restore_clears_drift_around_a_pending_publication_move(string row, string point, string problem,
        int refMoves, int indexMoves)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var publication = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .Publish(f.Lease(T), publication, attempt));
        CommitId? foreign = null;
        if (row == "d") foreign = MoveToForeign(f, ready);
        var branchBefore = GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch));
        f.Git.Write("result.txt", "late\n", ready.Checkout);
        if (row == "c") Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt));
        Assert.Equal(problem, blocked.Block.Problem.ToString());
        var blockId = Assert.Single(f.Read().Blocks).Key;
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, ready, preservation);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, f.Op(), preview.Identity));
        Assert.True(f.Read().Blocks[blockId].Resolved);
        Assert.Equal(refMoves, Observations<GitMutation.RestoreRef>(f, operation).Count());
        Assert.Equal(indexMoves, Observations<GitMutation.AlignIndex>(f, operation).Count());
        Assert.Equal(row == "d" ? f.A : branchBefore, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal("late\n", f.Git.Git("show", preserved.Commit.Hex + ":result.txt"));
        if (foreign is { } tip)
        {
            Assert.Equal("foreign\n", f.Git.Git("show", tip.Hex + ":foreign.txt"));
            Assert.Equal(0, f.Git.Run(f.Git.Folder, "merge-base", "--is-ancestor", tip.Hex, preserved.Receipt.Ref).ExitCode);
        }
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_with_a_partial_reset_is_restored_as_one_component(bool withLateWrite)
    {
        var reviewer = new TaskDefinition(U, new Blueprint(new("example.review", 1), "Review",
            new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")), [],
            new(Task().Execution, ConversationMode.Autonomous))) { Title = "Review" };
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), reviewer), T, U));
        await f.Publish(T, f.A);
        var review = Assert.IsType<Preparation.Ready>(await f.Prepare(U, prompt: "Review."));
        var checkpoint = f.ObserveAndLog(review, "Changes requested.\n");
        Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(U), f.Op(), review.Execution.Launch, checkpoint));
        var fix = Assert.IsType<Preparation.Ready>(await f.Prepare(T,
            cause: new AttemptCause.ReviewFix(new(U, review.Execution.Launch.Attempt, 1, 0)), prompt: "Fix."));
        f.Git.Write("a.txt", "Fixed\n", fix.Checkout);
        await f.Close(fix, "Repaired.\n");
        var repaired = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), fix.Execution.Launch.Attempt));
        var newBase = Assert.IsType<CodeOutput.Produced>(repaired.Result.Code).Code.Commit;
        var oldBase = review.Execution.Location.AttemptBase;
        var operation = f.Op();
        var key = new LaunchKey(review.Execution.Launch.Attempt, 2);
        await Assert.ThrowsAsync<Crash>(async () => await f.Materializer(probe: step =>
        {
            if (step == "journal.refresh-reset-intent.after") throw new Crash();
        }).PrepareTurn(f.Lease(U), operation, key, "Review the fix."));
        Assert.Equal(0, f.Git.Run(review.Checkout, "checkout", newBase.Hex, "--", ".").ExitCode);
        Assert.Equal(0, f.Git.Run(review.Checkout, "read-tree", oldBase.Hex).ExitCode);
        if (withLateWrite) f.Git.Write("root.txt", "late\n", review.Checkout);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(U), preservation, key.Attempt));
        var preview = Preview(f, review, preservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(U), f.Op(), key.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(review.Checkout, "a.txt")));
        Assert.Equal("root\n", File.ReadAllText(Path.Combine(review.Checkout, "root.txt")));
        Assert.Equal(GitFixture.Read(f.Git.Open().ReadCommit(oldBase)).Tree, GitFixture.Read(f.Git.Open().Capture(review.Checkout)).Tree);
        Assert.Equal(GitFixture.Read(f.Git.Open().ReadCommit(oldBase)).Tree, GitFixture.Read(f.Git.Open().ReadIndexTree(review.Checkout)));
        var refreshed = Assert.IsType<Preparation.Ready>(await f.Materializer().PrepareTurn(f.Lease(U), operation, key, "Review the fix."));
        Assert.Equal(newBase, refreshed.Execution.Location.AttemptBase);
        Assert.Equal("Fixed\n", File.ReadAllText(Path.Combine(refreshed.Checkout, "a.txt")));
        Assert.Equal(GitFixture.Read(f.Git.Open().ReadCommit(newBase)).Tree, GitFixture.Read(f.Git.Open().ReadIndexTree(refreshed.Checkout)));
        Assert.Single(f.Read().Restorations);
    }

    [Fact]
    public async Task A_publication_CAS_after_a_restoration_is_adopted()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var publication = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.branch-intent.after") throw new Crash(); })
            .Publish(f.Lease(T), publication, attempt));
        var candidate = Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Publication>()).Commit;
        var foreign = MoveToForeign(f, ready);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, ready, preservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "merge-base", "--is-ancestor", foreign.Hex, preserved.Receipt.Ref).ExitCode);
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "git.branch.after") throw new Crash(); })
            .Publish(f.Lease(T), publication, attempt));
        Assert.Equal(candidate, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.Empty(f.Read().Results);
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publication, attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Equal(candidate, GitFixture.Read(f.Git.Open().ReadRef(ready.Execution.Location.Owner.Branch)));
        Assert.True(f.Read().GitObservations[OperationIds.Derive(publication, "branch-intent")].Adopted);
        Assert.Single(f.Read().Results);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lock_only_restore_unblocks_an_unfinished_retry(bool withLock)
    {
        if (withLock && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await SalvageTests.FailedWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var salvage = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), attempt));
        var reset = f.Op();
        var indexPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step != "journal.retry-reset-intent.after") return;
            if (withLock) File.WriteAllText(indexPath + ".lock", "partial\n");
            throw new Crash();
        }).ResetForRetry(f.Lease(T), reset, salvage.Receipt.Plan, f.Op()));
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var operation = f.Op();
        if (!withLock)
        {
            var detail = $"Retry reset {OperationIds.Derive(reset, "retry-plan").Value:D} has an unfinished Git step on this checkout. Run it again, or salvage and retry.";
            var refusal = Assert.IsType<RestorePreviewRead.Refused>(f.Materializer().PreviewRestore(f.Lease(T), attempt, preservation));
            Assert.Equal("UncertainOwnership", refusal.Problem.ToString());
            Assert.Equal(detail, refusal.Detail);
            var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, attempt,
                preservation, f.Op(), Revision.Hash("unfinished preview")));
            Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
            Assert.Equal(detail, blocked.Block.Detail);
            Assert.Equal(0, Moves(f, operation));
        }
        else
        {
            var preview = Preview(f, ready, preservation);
            Assert.Equal("partial\n", File.ReadAllText(indexPath + ".lock"));
            Assert.Equal(preview.Current with { IndexLock = null }, preview.To);
            Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, attempt, preservation, f.Op(), preview.Identity));
            Assert.Single(Observations<GitMutation.RemoveIndexLock>(f, operation));
            Assert.Equal(1, Moves(f, operation));
            Assert.False(File.Exists(indexPath + ".lock"));
        }
        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
        var again = Assert.IsType<Salvage.Retained>(await f.Materializer().Salvage(f.Lease(T), f.Op(), attempt));
        Assert.IsType<RetryReset.Reset>(f.Materializer().ResetForRetry(f.Lease(T), f.Op(), again.Receipt.Plan, f.Op()));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.False(File.Exists(Path.Combine(ready.Checkout, "new.txt")));
        Assert.Equal("cache\n", File.ReadAllText(Path.Combine(ready.Checkout, "cache.txt")));
        Assert.Equal("", f.Git.Run(ready.Checkout, "status", "--porcelain").Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_recovers_from_its_own_index_lock(bool publicationLock)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await DoneWriter(f);
        var attempt = ready.Execution.Launch.Attempt;
        var indexPath = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        var capture = f.Read().Captures[f.Read().Settlements[ready.Execution.Launch]][0];
        var operation = f.Op();
        var confirmation = f.Op();
        OperationId preservation;
        RestorePreview? firstPreview = null;
        if (publicationLock)
        {
            Assert.Throws<Crash>(() => f.Materializer(probe: step =>
            {
                if (step != "git.align-index.before") return;
                File.WriteAllText(indexPath + ".lock", "partial\n");
                throw new Crash();
            }).Publish(f.Lease(T), operation, attempt));
            preservation = f.Op();
        }
        else
        {
            f.Git.Write("result.txt", "late\n", ready.Checkout);
            Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "result.txt").ExitCode);
            preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
            firstPreview = Preview(f, ready, preservation);
            Assert.Throws<Crash>(() => f.Materializer(probe: step =>
            {
                if (step != "git.restore-index.before") return;
                File.WriteAllText(indexPath + ".lock", "partial\n");
                throw new Crash();
            }).Restore(f.Lease(T), operation, attempt, preservation, confirmation, firstPreview.Identity));
            var before = Moves(f, operation);
            var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), operation, attempt,
                preservation, confirmation, firstPreview.Identity));
            Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
            Assert.True(blocked.Block.Scope!.IndexLock);
            Assert.Equal(before, Moves(f, operation));
            Assert.Equal(1, before);
            preservation = f.Op();
        }
        Assert.Equal("partial\n", File.ReadAllText(indexPath + ".lock"));
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, ready, preservation);
        var replacement = f.Op();
        var planId = OperationIds.Derive(operation, "restore-plan");
        if (!publicationLock)
        {
            Assert.Equal(planId, preview.Supersedes);
            var candidate = new MaterializationPlan.Restoration(T, attempt, preservation, preview.Current, preview.To,
                preview.Paths, preview.Repairs, preview.Rechecks, f.Op(), preview.Identity) { Supersedes = planId };
            Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.Planned(candidate with { Supersedes = null }))).Reason.Problem.ToString());
            Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.Planned(candidate with { Supersedes = f.Op() }))).Reason.Problem.ToString());
            var old = Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[planId]);
            Assert.Equal("InvalidData", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.Planned(old with { Supersedes = planId }))).Reason.Problem.ToString());
        }
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), replacement, attempt, preservation, f.Op(), preview.Identity));
        Assert.Single(Observations<GitMutation.RemoveIndexLock>(f, replacement));
        Assert.Equal(publicationLock ? 0 : 1, Observations<GitMutation.AlignIndex>(f, replacement).Count());
        Assert.Equal(capture.Index!.Content, GitFixture.Read(f.Git.Open().IndexDigest(ready.Checkout)));
        Assert.False(File.Exists(indexPath + ".lock"));
        Assert.Single(f.Read().Restorations);
        if (!publicationLock)
        {
            Assert.Equal(planId, Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[OperationIds.Derive(replacement, "restore-plan")]).Supersedes);
            var before = Moves(f, operation);
            Assert.Equal("ReplacementConflict", Assert.IsType<Restoration.Rejected>(f.Materializer().Restore(f.Lease(T), operation,
                attempt, Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[planId]).Preservation, confirmation, firstPreview!.Identity)).Reason.Problem.ToString());
            Assert.Equal(before, Moves(f, operation));
            var intent = Assert.Single(f.Read().GitIntents, p => p.Value.Plan == planId && p.Value.Mutation is GitMutation.AlignIndex);
            Assert.Equal("ReplacementConflict", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.GitObserved(intent.Key, new(false, capture.IndexTree!.Value.Hex)))).Reason.Problem.ToString());
            Assert.Equal("ReplacementConflict", Assert.IsType<RunDecision.Rejected>(f.Store.Record(f.Permit, f.Op(),
                new RunEvent.GitIntended(planId, intent.Value.Mutation))).Reason.Problem.ToString());
        }
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), publicationLock ? operation : f.Op(), attempt));
        Assert.Equal("done\n", accepted.Result.Report);
        Assert.Equal("done\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
    }

    [Fact]
    public async Task Restore_of_an_unexplained_tip_never_explains_it_to_others()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(C)));
        var writer = await DoneWriter(f);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        f.Git.Write("c.txt", "C\n", sibling.Checkout);
        await f.Close(sibling, "C ready.\n");
        var foreign = MoveToForeign(f, writer);
        var preservation = f.Op();
        var preserved = Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, writer.Execution.Launch.Attempt));
        var preview = Preview(f, writer, preservation);
        var operation = f.Op();
        var confirmation = f.Op();
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == "journal.restore-branch-intent.after") throw new Crash(); })
            .Restore(f.Lease(T), operation, writer.Execution.Launch.Attempt, preservation, confirmation, preview.Identity));
        f.ReleaseControl();
        var publication = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(C), publication, sibling.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal(new[] { "refs/heads/idp/93f23689/task/90d5b0a2" }, blocked.Block.Scope!.Refs);
        Assert.Contains(writer.Execution.Location.Owner.Branch, blocked.Block.Detail);
        Assert.Equal(foreign, GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch)));
        Assert.Equal(0, f.Read().Results.Count(r => r.Task == C));
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, writer.Execution.Launch.Attempt,
            preservation, confirmation, preview.Identity));
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(writer.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("foreign\n", f.Git.Git("show", foreign.Hex + ":foreign.txt"));
        Assert.Equal(0, f.Git.Run(f.Git.Folder, "merge-base", "--is-ancestor", foreign.Hex, preserved.Receipt.Ref).ExitCode);
        var siblingPreservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(C), siblingPreservation, sibling.Execution.Launch.Attempt));
        var siblingPreview = Preview(f, sibling, siblingPreservation);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(C), f.Op(), sibling.Execution.Launch.Attempt,
            siblingPreservation, f.Op(), siblingPreview.Identity));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(C), publication, sibling.Execution.Launch.Attempt));
        Assert.Equal("C ready.\n", accepted.Result.Report);
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(sibling.Checkout, "c.txt")));
        Assert.Equal(1, f.Read().Results.Count(r => r.Task == C));
    }

    [Fact]
    public async Task A_shared_ref_block_needs_external_repair_then_a_recheck()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "X\n", ready.Checkout);
        await f.Close(ready, "X\n");
        f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal(new[] { "refs/stash" }, blocked.Block.Scope!.Refs);
        Assert.Contains("refs/stash", blocked.Block.Detail);
        var blockId = Assert.Single(f.Read().Blocks).Key;
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        Assert.Empty(preview.Paths);
        Assert.Empty(preview.Repairs);
        Assert.Equal(new[] { blockId }, preview.Rechecks);
        var operation = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), operation, ready.Execution.Launch.Attempt, preservation, f.Op(), preview.Identity));
        Assert.Equal(0, Moves(f, operation));
        Assert.Single(f.Read().Restorations);
        Assert.False(f.Read().Blocks[blockId].Resolved);
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef("refs/stash"))?.Hex);
        Assert.IsType<Publication.Blocked>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        f.Git.Git("update-ref", "-d", "refs/stash");
        var replacement = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), replacement, ready.Execution.Launch.Attempt));
        var recheck = Preview(f, ready, replacement);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt, replacement, f.Op(), recheck.Identity));
        Assert.True(f.Read().Blocks[blockId].Resolved);
        Assert.Null(GitFixture.Read(f.Git.Open().ReadRef("refs/stash")));
        var accepted = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(T), f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("X\n", accepted.Result.Report);
        Assert.Equal("X\n", File.ReadAllText(Path.Combine(ready.Checkout, "result.txt")));
        Assert.Single(f.Read().Results);
    }

    private static readonly Lazy<string[]> RecordedRestorePoints = new(() =>
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return [];
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var restore = RestoreMatrixSetup(f).GetAwaiter().GetResult();
        var points = new List<string>();
        Assert.IsType<Restoration.Restored>(f.Materializer(probe: points.Add).Restore(f.Lease(T), restore.Operation,
            restore.Ready.Execution.Launch.Attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity));
        AssertMatrixBaseline(f, restore);
        Assert.Equal(4, Moves(f, restore.Operation));
        Assert.Single(Observations<GitMutation.RemoveIndexLock>(f, restore.Operation));
        Assert.Single(Observations<GitMutation.RestoreRef>(f, restore.Operation));
        Assert.Single(Observations<GitMutation.RestoreFiles>(f, restore.Operation));
        Assert.Single(Observations<GitMutation.AlignIndex>(f, restore.Operation));
        Assert.Equal(4, Observations<GitMutation>(f, restore.Operation).Count(o => !o.Adopted));
        Assert.Equal(new[] { "a.txt", "extra.txt" }, restore.Preview.Paths.Select(p => p.Path));
        Assert.Contains("journal.restore-plan.after", points);
        Assert.Contains("git.restore-lock.after", points);
        Assert.Contains("git.restore-branch.after", points);
        Assert.Contains("restore.file.a.txt.written", points);
        Assert.Contains("git.restore-file-extra.txt.after", points);
        Assert.Contains("git.restore-index.after", points);
        Assert.Contains("journal.restored.after", points);
        return points.Distinct().ToArray();
    });

    public static IEnumerable<object[]> RestoreCrashPoints => RecordedRestorePoints.Value.Select(point => new object[] { point });

    [Theory]
    [MemberData(nameof(RestoreCrashPoints), DisableDiscoveryEnumeration = true)]
    public async Task Every_restore_probe_converges_on_the_baseline(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var restore = await RestoreMatrixSetup(f);
        var attempt = restore.Ready.Execution.Launch.Attempt;
        Assert.Throws<Crash>(() => f.Materializer(probe: step => { if (step == point) throw new Crash(); })
            .Restore(f.Lease(T), restore.Operation, attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity));
        var written = point == "restore.file.a.txt.written";
        if (written)
        {
            var scratch = Path.Combine(restore.ScratchRoot, OperationIds.Derive(restore.Operation, "restore-plan").Value.ToString("D"));
            Assert.True(Directory.Exists(scratch));
            Assert.Equal("A\n", File.ReadAllText(Assert.Single(Directory.GetFiles(scratch))));
            Assert.Equal("changed\n", File.ReadAllText(Path.Combine(restore.Ready.Checkout, "a.txt")));
        }
        var cleanupChecked = false;
        var outcome = f.Materializer(probe: step =>
        {
            if (!written || step != "git.restore-file-a.txt.before") return;
            cleanupChecked = true;
            Assert.False(Directory.Exists(Path.Combine(restore.ScratchRoot, OperationIds.Derive(restore.Operation, "restore-plan").Value.ToString("D"))));
        }).Restore(f.Lease(T), restore.Operation, attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity);
        var operations = new List<OperationId> { restore.Operation };
        if (outcome is Restoration.Blocked blocked)
        {
            Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
            var preservation = f.Op();
            Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
            var preview = Preview(f, restore.Ready, preservation);
            Assert.Equal(OperationIds.Derive(restore.Operation, "restore-plan"), preview.Supersedes);
            var replacement = f.Op();
            operations.Add(replacement);
            outcome = f.Materializer().Restore(f.Lease(T), replacement, attempt, preservation, f.Op(), preview.Identity);
            Assert.Equal("ReplacementConflict", Assert.IsType<Restoration.Rejected>(f.Materializer().Restore(f.Lease(T), restore.Operation,
                attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity)).Reason.Problem.ToString());
        }
        Assert.IsType<Restoration.Restored>(outcome);
        AssertMatrixBaseline(f, restore);
        Assert.Single(f.Read().Restorations, p => operations.Any(o => OperationIds.Derive(o, "restore-plan") == p.Key));
        Assert.Single(f.Read().Restorations);
        Assert.Equal(0, f.Read().Blocks.Values.Count(b => !b.Resolved));
        var observations = operations.SelectMany(o => Observations<GitMutation>(f, o)).ToArray();
        var adopted = point is "git.restore-lock.after" or "journal.restore-lock-observed.before" or
            "git.restore-branch.after" or "journal.restore-branch-observed.before" or
            "git.restore-file-extra.txt.after" or "journal.restore-files-observed.before" or
            "git.restore-index.after" or "journal.restore-index-observed.before" ? 1 : 0;
        Assert.Equal(4, observations.Length);
        Assert.Equal(adopted, observations.Count(o => o.Adopted));
        Assert.Equal(4 - adopted, observations.Count(o => !o.Adopted));
        if (written) Assert.True(cleanupChecked);
        var sequence = f.Read().Sequence;
        var last = operations[^1];
        var plan = Assert.IsType<MaterializationPlan.Restoration>(f.Read().Plans[OperationIds.Derive(last, "restore-plan")]);
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), last, attempt, plan.Preservation, plan.Confirmation, plan.Preview));
        Assert.Equal(sequence, f.Read().Sequence);
    }

    [Theory]
    [MemberData(nameof(RestoreCrashPoints), DisableDiscoveryEnumeration = true)]
    public async Task A_late_write_at_every_restore_probe_blocks_all_further_moves(string point)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var restore = await RestoreMatrixSetup(f);
        var attempt = restore.Ready.Execution.Launch.Attempt;
        Assert.Throws<Crash>(() => f.Materializer(probe: step =>
        {
            if (step != point) return;
            f.Git.Write("root.txt", "late\n", restore.Ready.Checkout);
            throw new Crash();
        }).Restore(f.Lease(T), restore.Operation, attempt, restore.Preservation, restore.Confirmation, restore.Preview.Identity));
        var before = Moves(f, restore.Operation);
        var branch = GitFixture.Read(f.Git.Open().ReadRef(restore.Ready.Execution.Location.Owner.Branch));
        var index = GitFixture.Read(f.Git.Open().IndexDigest(restore.Ready.Checkout));
        var a = File.ReadAllBytes(Path.Combine(restore.Ready.Checkout, "a.txt"));
        var extraPath = Path.Combine(restore.Ready.Checkout, "extra.txt");
        var extra = File.Exists(extraPath) ? File.ReadAllBytes(extraPath) : null;
        var blocked = Assert.IsType<Restoration.Blocked>(f.Materializer().Restore(f.Lease(T), restore.Operation, attempt,
            restore.Preservation, restore.Confirmation, restore.Preview.Identity));
        Assert.Equal("DirtyWorktree", blocked.Block.Problem.ToString());
        Assert.Equal("The checkout changed after it was preserved. Preserve it again.", blocked.Block.Detail);
        Assert.Contains("root.txt", blocked.Block.Scope!.Paths);
        Assert.Equal(before, Moves(f, restore.Operation));
        Assert.Equal("late\n", File.ReadAllText(Path.Combine(restore.Ready.Checkout, "root.txt")));
        Assert.Equal(branch, GitFixture.Read(f.Git.Open().ReadRef(restore.Ready.Execution.Location.Owner.Branch)));
        Assert.Equal(index, GitFixture.Read(f.Git.Open().IndexDigest(restore.Ready.Checkout)));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(restore.Ready.Checkout, "a.txt")));
        Assert.Equal(extra, File.Exists(extraPath) ? File.ReadAllBytes(extraPath) : null);
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, attempt));
        var preview = Preview(f, restore.Ready, preservation);
        var replacement = f.Op();
        Assert.IsType<Restoration.Restored>(f.Materializer().Restore(f.Lease(T), replacement, attempt, preservation, f.Op(), preview.Identity));
        Assert.Single(Observations<GitMutation.RestoreFiles>(f, replacement));
        AssertMatrixBaseline(f, restore);
    }

    private static async Task<RestoreCase> RestoreMatrixSetup(PreparationFixture f)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready);
        File.AppendAllText(f.Git.PathOf(".git/info/exclude"), "a.txt.idp-restore-0000\n");
        f.Git.Write("a.txt.idp-restore-0000", "ignored\n", ready.Checkout);
        MoveToForeign(f, ready);
        f.Git.Write("a.txt", "changed\n", ready.Checkout);
        f.Git.Write("extra.txt", "extra\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "a.txt", "extra.txt").ExitCode);
        var index = GitFixture.Read(f.Git.Open().IndexPath(ready.Checkout));
        File.WriteAllText(index + ".lock", "partial\n");
        var preservation = f.Op();
        Assert.IsType<Preservation.Preserved>(await f.Materializer().Preserve(f.Lease(T), preservation, ready.Execution.Launch.Attempt));
        var preview = Preview(f, ready, preservation);
        return new(ready, preservation, f.Op(), f.Op(), preview, index, Path.Combine(Path.GetDirectoryName(index)!, "idevelop-restore"));
    }

    private static void AssertMatrixBaseline(PreparationFixture f, RestoreCase restore)
    {
        var checkout = restore.Ready.Checkout;
        Assert.Equal("adfe40b30c176fb407933286f51d15ea9b54cdc3", GitFixture.Read(f.Git.Open().ReadRef(restore.Ready.Execution.Location.Owner.Branch))?.Hex);
        Assert.Equal("refs/heads/idp/93f23689/task/90d5b0a2\n", f.Git.Run(checkout, "symbolic-ref", "HEAD").Text);
        Assert.Equal(new byte[] { 65, 10 }, File.ReadAllBytes(Path.Combine(checkout, "a.txt")));
        Assert.Equal(new byte[] { 114, 111, 111, 116, 10 }, File.ReadAllBytes(Path.Combine(checkout, "root.txt")));
        Assert.Equal(new byte[] { 97, 112, 112, 114, 111, 118, 101, 100, 10 }, File.ReadAllBytes(Path.Combine(checkout, "plan.txt")));
        Assert.False(File.Exists(Path.Combine(checkout, "extra.txt")));
        Assert.Equal("a.txt\nplan.txt\nroot.txt\n", f.Git.Run(checkout, "ls-files").Text);
        Assert.Equal("", f.Git.Run(checkout, "status", "--porcelain").Text);
        Assert.False(File.Exists(restore.IndexPath + ".lock"));
        Assert.False(Directory.Exists(restore.ScratchRoot));
        Assert.Equal("ignored\n", File.ReadAllText(Path.Combine(checkout, "a.txt.idp-restore-0000")));
    }

    private static async Task<Preparation.Ready> DoneWriter(PreparationFixture f)
    {
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Write("result.txt", "done\n", ready.Checkout);
        await f.Close(ready, "done\n");
        return ready;
    }

    private static CommitId MoveToForeign(PreparationFixture f, Preparation.Ready ready)
    {
        f.Git.Write("foreign.txt", "foreign\n");
        var foreign = f.Git.Commit("foreign");
        f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, foreign.Hex);
        return foreign;
    }

    private static IEnumerable<GitObservation> Observations<T>(PreparationFixture f, OperationId operation) where T : GitMutation
    {
        var record = f.Read();
        var plan = OperationIds.Derive(operation, "restore-plan");
        return record.GitObservations.Where(p => record.GitIntents[p.Key].Plan == plan && record.GitIntents[p.Key].Mutation is T).Select(p => p.Value);
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
