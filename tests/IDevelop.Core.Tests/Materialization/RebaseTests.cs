using System.Collections.Immutable;
using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

/// <summary>Review updated inputs: a stale writer result's preview and its approved clean rebase (E3f).</summary>
public sealed class RebaseTests
{
    internal static Workflow Chain() => Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U);

    private static Workflow Longer() => Connect(Connect(FixtureWorkflow(Writer(T), Writer(U), Writer(D)), T, U), U, D);

    internal static Materializer Rebaser(PreparationFixture f, Action<string>? probe = null, IReadOnlyDictionary<string, string>? environment = null) =>
        MergeJoins.Open(f.Git.Folder, f.Store, new Clock(), environment ?? f.Git.Environment, probe);

    private static void Commit(PreparationFixture f, string checkout, params (string Path, string? Text)[] edits)
    {
        foreach (var (path, text) in edits)
        {
            if (text is null) Assert.Equal(0, f.Git.Run(checkout, "rm", "-q", path).ExitCode);
            else f.Git.Write(path, text, checkout);
        }
        Assert.Equal(0, f.Git.Run(checkout, "add", "--all").ExitCode);
        Assert.Equal(0, f.Git.Run(checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", edits[0].Path).ExitCode);
    }

    /// <summary>Runs <paramref name="task"/>'s attempt for <paramref name="cause"/> with <paramref name="edits"/> and publishes it.</summary>
    internal static async Task<ResultRecord> Write(PreparationFixture f, TaskId task, AttemptCause? cause, byte[]? artifact,
        params (string Path, string? Text)[] edits)
    {
        var ready = Assert.IsType<Preparation.Ready>(await Rebaser(f).Prepare(f.Lease(task), f.Op(), cause ?? new AttemptCause.Initial()));
        Commit(f, ready.Checkout, edits);
        if (artifact is not null)
        {
            var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
            File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), artifact);
            File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
        }
        await f.Close(ready);
        return Assert.IsType<Publication.Accepted>(f.Materializer().Publish(f.Lease(task), f.Op(), ready.Execution.Launch.Attempt)).Result;
    }

    internal static Task<ResultRecord> Write(PreparationFixture f, TaskId task, params (string Path, string? Text)[] edits) =>
        Write(f, task, null, null, edits);

    /// <summary>Retries <paramref name="task"/>'s current result with <paramref name="edits"/>, which makes its consumers stale.</summary>
    internal static Task<ResultRecord> Update(PreparationFixture f, TaskId task, params (string Path, string? Text)[] edits) =>
        Write(f, task, new AttemptCause.Retry(Assert.IsType<ResultOrigin.Executed>(f.Read().CurrentResults[task].Origin).Attempt, f.Op()), null, edits);

    internal static RebasePreview Preview(PreparationFixture f, TaskId task, Materializer? materializer = null) =>
        Assert.IsType<RebasePreviewRead.Previewed>((materializer ?? Rebaser(f)).PreviewRebase(f.Lease(task))).Preview;

    private static CommitId CodeOf(ResultRecord result) => Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit;

    private static ResultId[] Provided(IEnumerable<InputBinding> bindings) => [.. bindings.OfType<InputBinding.Provided>().Select(binding => binding.Result)];

    private static string Show(PreparationFixture f, CommitId commit, string file) => f.Git.Git("show", $"{commit.Hex}:{file}");

    private static string Checkout(PreparationFixture f, TaskId task) => Path.Combine(f.Git.Folder, ".worktrees", f.Read().RunKey!, f.Read().TaskKeys[task]);

    private static string Branch(PreparationFixture f, TaskId task) => RunLayout.TaskBranch(f.Read().RunKey!, f.Read().TaskKeys[task]);

    private static CommitId? Tip(PreparationFixture f, string reference) => GitFixture.Read(f.Git.Open().ReadRef(reference));

    private static string Text(PreparationFixture f, TaskId task, string file) => File.ReadAllText(Path.Combine(Checkout(f, task), file));

    private static RunProblem Problem(Rebasing rebasing) => Assert.IsType<Rebasing.Rejected>(rebasing).Reason.Problem;

    [Fact]
    public async Task A_stale_consumer_previews_both_inputs_and_rebases_onto_the_update_without_a_client()
    {
        using var f = new PreparationFixture(Chain());
        var first = await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, null, [67, 0, 127], ("b.txt", "B\n"));
        var update = await Update(f, T, ("a.txt", "new\n"));
        Assert.Contains(stale.Id, f.Read().StaleResults);
        Assert.Equal(RunProblem.IncompleteResults, Assert.IsType<RunDecision.Rejected>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed)).Reason.Problem);
        var sequence = f.Read().Sequence;

        var preview = Preview(f, U);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(stale.Id, preview.Stale);
        Assert.Equal([first.Id], Provided(preview.Previous.Bindings));
        Assert.Equal([update.Id], Provided(preview.Current));
        Assert.Equal([T], preview.Updated.ToArray());
        Assert.Equal(CodeOf(first), preview.OldBase);
        Assert.Equal(CodeOf(update), preview.NewBase);
        Assert.Equal(["b.txt"], preview.Changes.ToArray());
        Assert.Equal("B ready.\n", preview.Report);
        Assert.Equal(stale.Artifacts.ToArray(), preview.Artifacts.ToArray());
        var clean = Assert.IsType<RebaseCandidate.Clean>(preview.Candidate);
        Assert.Equal(["a.txt"], clean.Updates.ToArray());
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
        Assert.Equal("old\n", Text(f, U, "a.txt"));
        Assert.Equal(preview.Identity, Preview(f, U).Identity);

        var approval = f.Op();
        var rebased = Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity)).Result;
        Assert.Equal(new ResultOrigin.Rebased(stale.Id, OperationIds.Derive(approval, "rebase-plan")), rebased.Origin);
        Assert.Equal(stale.Id, rebased.Supersedes);
        Assert.Equal("B ready.\n", rebased.Report);
        Assert.Equal([update.Id], Provided(f.Read().Inputs[rebased.Inputs].Bindings));
        var code = Assert.IsType<CodeOutput.Produced>(rebased.Code).Code;
        Assert.Equal(clean.Commit, code.Commit);
        Assert.Equal(CodeOf(update), code.AttemptBase);
        Assert.Equal(["new\n", "B\n"], new[] { Show(f, clean.Commit, "a.txt"), Show(f, clean.Commit, "b.txt") });
        Assert.Equal([CodeOf(update)], GitFixture.Read(f.Git.Open().ReadCommit(clean.Commit)).Parents.ToArray());
        Assert.Equal(clean.Commit, Tip(f, Branch(f, U)));
        Assert.Equal(["new\n", "B\n"], new[] { Text(f, U, "a.txt"), Text(f, U, "b.txt") });
        Assert.Equal("", f.Git.Git("-C", Checkout(f, U), "status", "--porcelain"));
        Assert.Equal(CodeOf(stale), Tip(f, Assert.IsType<CodeOutput.Produced>(stale.Code).Code.ResultRef));
        Assert.Equal(clean.Commit, Tip(f, code.ResultRef));
        var artifact = Assert.Single(rebased.Artifacts);
        Assert.Equal(RunStorage.ArtifactPath(rebased.Id, "payload"), artifact.StoredPath);
        Assert.Equal(new byte[] { 67, 0, 127 }, new RunStorage(f.Git.Folder, W, f.RunId).ReadArtifact(rebased.Id, artifact));
        Assert.DoesNotContain(rebased.Id, f.Read().StaleResults);
        Assert.Single(f.Read().Attempts.Values, attempt => attempt.Task == U);

        Assert.True(RunReducer.Same(rebased, Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity)).Result));
        Assert.Equal(4, f.Read().Results.Count);
        Assert.Equal(RunProblem.UnknownResult, Assert.IsType<RebasePreviewRead.Rejected>(Rebaser(f).PreviewRebase(f.Lease(U))).Reason.Problem);
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed));
    }

    [Fact]
    public async Task A_conflicting_rebase_stays_blocked_and_records_and_moves_nothing()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("a.txt", "mine\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        var conflict = Assert.IsType<RebaseCandidate.Conflicted>(preview.Candidate);
        Assert.Equal(["a.txt"], conflict.Paths.ToArray());
        Assert.Equal("The task's change conflicts with its updated inputs in a.txt.", conflict.Detail);
        var sequence = f.Read().Sequence;
        Assert.Equal(RunProblem.InputConflict, Problem(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
        Assert.Equal("mine\n", Text(f, U, "a.txt"));
        Assert.Contains(stale.Id, f.Read().StaleResults);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.Git.Folder, ".git", "idevelop", "merges")));
    }

    [Fact]
    public async Task Approval_refuses_a_preview_whose_inputs_or_identity_changed()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        Assert.Equal(RunProblem.EvidenceMismatch, Problem(Rebaser(f).Rebase(f.Lease(U), f.Op(), new(new string('0', 64)))));
        await Update(f, T, ("a.txt", "newer\n"));
        var sequence = f.Read().Sequence;
        Assert.Equal(RunProblem.EvidenceMismatch, Problem(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
        Assert.Equal(RunProblem.ConfirmationRequired, Problem(Rebaser(f).Rebase(f.Lease(U), new(Guid.Empty), preview.Identity)));
        var current = Preview(f, U);
        Assert.NotEqual(preview.Identity, current.Identity);
        Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), f.Op(), current.Identity));
        Assert.Equal("newer\n", Text(f, U, "a.txt"));
    }

    [Theory]
    [InlineData("b.txt", "edited\n")]
    [InlineData("extra.txt", "extra\n")]
    public async Task A_changed_consumer_checkout_blocks_the_rebase_before_any_move(string file, string text)
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        File.WriteAllText(Path.Combine(Checkout(f, U), file), text);
        var blocked = Assert.IsType<Rebasing.Blocked>(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, blocked.Problem);
        Assert.Equal([file], Assert.IsType<BlockScope.Checkout>(blocked.Scope).Paths.ToArray());
        Assert.Equal(Assert.IsType<ResultOrigin.Executed>(stale.Origin).Attempt, blocked.Attempt);
        Assert.Empty(f.Read().Plans.Values.OfType<MaterializationPlan.Rebase>());
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
        Assert.Equal(text, Text(f, U, file));
        Assert.Equal("old\n", Text(f, U, "a.txt"));
        var refused = Assert.IsType<RebasePreviewRead.Refused>(Rebaser(f).PreviewRebase(f.Lease(U)));
        Assert.Equal(MaterializationProblem.DirtyWorktree, refused.Problem);
        // Undoing the change outside the app leaves the recorded drift, which only Preserve and restore resolves.
        if (file == "b.txt") File.WriteAllText(Path.Combine(Checkout(f, U), file), "B\n");
        else File.Delete(Path.Combine(Checkout(f, U), file));
        Assert.Equal(blocked, Assert.IsType<Rebasing.Blocked>(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)).Block);
        Assert.Equal(blocked.Problem, Assert.IsType<RebasePreviewRead.Refused>(Rebaser(f).PreviewRebase(f.Lease(U))).Problem);
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
    }

    [Fact]
    public async Task A_rebased_result_can_be_retried_while_a_retry_whose_sources_changed_still_refuses()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var original = Assert.IsType<ResultOrigin.Executed>(stale.Origin).Attempt;
        // Without a rebase, the consumer's sources changed under it, so a retry of its attempt refuses.
        Assert.Equal(RunProblem.StaleInput, Assert.IsType<Preparation.Rejected>(await Rebaser(f).Prepare(f.Lease(U), f.Op(),
            new AttemptCause.Retry(original, f.Op()))).Reason.Problem);
        var rebased = Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), f.Op(), Preview(f, U).Identity)).Result;
        var retry = Assert.IsType<Preparation.Ready>(await Rebaser(f).Prepare(f.Lease(U), f.Op(), new AttemptCause.Retry(original, f.Op())));
        Assert.Equal(CodeOf(rebased), retry.Execution.Location.AttemptBase);
        Assert.Equal(["new\n", "B\n"], new[] { File.ReadAllText(Path.Combine(retry.Checkout, "a.txt")), File.ReadAllText(Path.Combine(retry.Checkout, "b.txt")) });
    }

    [Fact]
    public async Task A_rebased_subject_takes_its_review_fix()
    {
        var reviewer = new TaskDefinition(D, new Blueprint(new("example.review", 1), "Review",
            new WorkSpec.Review(PromptTemplate.Parse("Review"), PromptTemplate.Parse("Fix")), [], new(Runs.RunFixtures.Task().Execution, ConversationMode.Autonomous)))
        { Title = "Review" };
        using var f = new PreparationFixture(Connect(Connect(FixtureWorkflow(Writer(T), Writer(U), reviewer), T, U), U, D));
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var rebased = Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), f.Op(), Preview(f, U).Identity)).Result;
        var review = Assert.IsType<Preparation.Ready>(await f.Prepare(D, prompt: "Review the changes."));
        await CloseReview(f, review);
        var fix = Assert.IsType<Preparation.Ready>(await Rebaser(f).Prepare(f.Lease(U), f.Op(),
            new AttemptCause.ReviewFix(new(D, review.Execution.Launch.Attempt, 1, 0)), "Fix."));
        Assert.Equal(CodeOf(rebased), fix.Execution.Location.AttemptBase);
    }

    /// <summary>Closes a reviewer's first turn with changes requested, as ReviewMaterializationTests does for its reviewer.</summary>
    private static async Task CloseReview(PreparationFixture f, Preparation.Ready ready)
    {
        var execution = ready.Execution;
        var attempt = f.Read().Attempts[execution.Launch.Attempt];
        var inputs = f.Read().Inputs[execution.Inputs];
        Assert.IsType<RunDecision.Granted>(f.Store.Claim(f.Lease(D), f.Op(), execution.Launch, inputs, execution.PromptHash));
        var definition = f.Read().Revision.Snapshot.Tasks[D];
        var folder = f.Store.AttemptFolder(W, f.RunId, D, attempt.Id);
        using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!,
            new AttemptEvent.Requested(At, attempt.Id, D, definition.Title, definition.Execution!, execution.Prompt, "codex", [])
            { RunBinding = new(W, f.RunId, attempt.Revision, inputs.Id), ReadOnly = true, Subject = U, Conversation = definition.Conversation }))
        {
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.SessionStarted("review")));
            log.Append(new AttemptEvent.Agent(At, new AgentEvent.Succeeded("Changes requested.")));
            log.Append(new AttemptEvent.Exited(At, 0, ""));
        }
        Assert.IsType<RootObservation.Observed>(f.Materializer().ObserveRootExit(f.Lease(D), f.Op(), execution.Launch, new RootExit.Exited(0)));
        Assert.IsType<Settlement.Closed>(await f.Materializer().Settle(f.Lease(D), f.Op(), execution.Launch, Checkpoint(folder)));
    }

    [Fact]
    public async Task A_later_attempt_s_changes_in_the_checkout_refuse_the_rebase_before_anything_is_journaled()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        // A retry of U writes junk.txt and fails, so the journal's last baseline for U's checkout is that attempt's files.
        var retry = Assert.IsType<Preparation.Ready>(await Rebaser(f).Prepare(f.Lease(U), f.Op(),
            new AttemptCause.Retry(Assert.IsType<ResultOrigin.Executed>(stale.Origin).Attempt, f.Op())));
        File.WriteAllText(Path.Combine(retry.Checkout, "junk.txt"), "junk\n");
        await f.Close(retry, outcome: TerminalAttemptOutcome.Failed);
        await Update(f, T, ("a.txt", "new\n"));
        Assert.Equal(stale.Id, f.Read().CurrentResults[U].Id);
        var refused = Assert.IsType<RebasePreviewRead.Rejected>(Rebaser(f).PreviewRebase(f.Lease(U))).Reason;
        Assert.Equal((RunProblem.StartConflict, U), (refused.Problem, refused.Task!.Value));
        var sequence = f.Read().Sequence;
        Assert.Equal(refused, Assert.IsType<Rebasing.Rejected>(Rebaser(f).Rebase(f.Lease(U), f.Op(), new(new string('0', 64)))).Reason);
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
        Assert.Equal("junk\n", Text(f, U, "junk.txt"));
    }

    [Fact]
    public async Task A_preview_waits_for_the_repository_lock_and_leaves_another_merge_s_scratch_folder()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var repository = f.Git.Open();
        var scratch = Directory.CreateDirectory(Path.Combine(repository.CommonDirectory, "idevelop", "merges", $"{Environment.ProcessId}-{new string('a', 32)}")).FullName;
        File.WriteAllText(Path.Combine(scratch, "HEAD"), "ref: refs/heads/none\n");
        using (repository.TakeMutationLock())
            Assert.Equal(RunProblem.JournalBusy, Assert.IsType<RebasePreviewRead.Rejected>(Rebaser(f).PreviewRebase(f.Lease(U))).Reason.Problem);
        Assert.True(File.Exists(Path.Combine(scratch, "HEAD")));
        Assert.IsType<RebasePreviewRead.Previewed>(Rebaser(f).PreviewRebase(f.Lease(U)));
    }

    [Fact]
    public async Task An_index_lock_in_the_consumer_checkout_refuses_the_preview()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var lockFile = f.Git.Git("-C", Checkout(f, U), "rev-parse", "--path-format=absolute", "--git-path", "index.lock").Trim();
        File.WriteAllText(lockFile, "lock\n");
        var refused = Assert.IsType<RebasePreviewRead.Refused>(Rebaser(f).PreviewRebase(f.Lease(U)));
        Assert.Equal(MaterializationProblem.DirtyWorktree, refused.Problem);
        Assert.True(Assert.IsType<BlockScope.Checkout>(refused.Scope).IndexLock);
        File.Delete(lockFile);
        Assert.IsType<RebasePreviewRead.Previewed>(Rebaser(f).PreviewRebase(f.Lease(U)));
    }

    [Fact]
    public async Task A_rebase_needs_a_closed_task_and_an_approved_run()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        var retry = Assert.IsType<Preparation.Ready>(await Rebaser(f).Prepare(f.Lease(U), f.Op(),
            new AttemptCause.Retry(Assert.IsType<ResultOrigin.Executed>(stale.Origin).Attempt, f.Op()))).Execution.Launch.Attempt;
        await Update(f, T, ("a.txt", "new\n"));
        Assert.Equal(RunProblem.UnclosedAttempts, Assert.IsType<RebasePreviewRead.Rejected>(Rebaser(f).PreviewRebase(f.Lease(U))).Reason.Problem);
        Assert.Equal(RunProblem.UnclosedAttempts, Problem(Rebaser(f).Rebase(f.Lease(U), f.Op(), new(new string('0', 64)))));
        Assert.IsType<RunDecision.Recorded>(f.Store.Recover(f.Lease(U), f.Op(), retry, RecoveryOutcome.NotStarted, f.Op(), "Not wanted."));
        var preview = Preview(f, U);
        Assert.IsType<RunDecision.Recorded>(f.Store.Stop(f.Permit, f.Op()));
        var sequence = f.Read().Sequence;
        Assert.Equal(RunProblem.RunStopped, Problem(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
    }

    [Theory]
    [InlineData("journal.rebase-plan.after", "edit", true)]
    [InlineData("journal.rebase-plan.after", "detach", true)]
    [InlineData("journal.rebase-branch-observed.after", "edit", true)]
    [InlineData("journal.rebase-branch-observed.after", "detach", true)]
    [InlineData("journal.rebase-reset-intent.after", "detach", false)]
    public async Task A_checkout_that_changes_after_the_approval_blocks_the_next_move_until_it_is_clean(string point, string change, bool crash)
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        var approval = f.Op();
        var checkout = Checkout(f, U);
        var branch = Branch(f, U);
        void Change()
        {
            if (change == "edit") File.WriteAllText(Path.Combine(checkout, "b.txt"), "edited\n");
            else f.Git.Git("-C", checkout, "checkout", "-q", "--detach");
        }
        var rebaser = Rebaser(f, probe: reached =>
        {
            if (reached != point) return;
            if (crash) throw new InvalidOperationException("Crashed.");
            Change();
        });
        MaterializationBlock blocked;
        if (crash)
        {
            Assert.Throws<InvalidOperationException>(() => rebaser.Rebase(f.Lease(U), approval, preview.Identity));
            Change();
            blocked = Assert.IsType<Rebasing.Blocked>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity)).Block;
        }
        else blocked = Assert.IsType<Rebasing.Blocked>(rebaser.Rebase(f.Lease(U), approval, preview.Identity)).Block;
        Assert.Equal(change == "edit" ? MaterializationProblem.DirtyWorktree : MaterializationProblem.UncertainOwnership, blocked.Problem);
        var record = f.Read();
        var plan = record.Plans.Single(pair => pair.Value is MaterializationPlan.Rebase).Key;
        Assert.Equal(point == "journal.rebase-plan.after" ? CodeOf(stale) : ((MaterializationPlan.Rebase)record.Plans[plan]).Commit, Tip(f, branch));
        Assert.Equal(point == "journal.rebase-reset-intent.after", record.GitIntents.Values.Any(intent => intent.Plan == plan && intent.Mutation is GitMutation.ResetCheckout));
        Assert.Equal(change == "edit" ? "edited\n" : "B\n", Text(f, U, "b.txt"));
        Assert.Equal("old\n", Text(f, U, "a.txt"));
        Assert.Equal(stale.Id, record.CurrentResults[U].Id);

        if (change == "edit") File.WriteAllText(Path.Combine(checkout, "b.txt"), "B\n");
        else f.Git.Git("-C", checkout, "symbolic-ref", "HEAD", branch);
        // A crash after the rebased result is recorded leaves the blocks to the repeat, which resolves them.
        Assert.Throws<InvalidOperationException>(() => Rebaser(f, probe: reached =>
        {
            if (reached == "journal.rebase-accepted.after") throw new InvalidOperationException("Crashed.");
        }).Rebase(f.Lease(U), approval, preview.Identity));
        Assert.Contains(f.Read().Blocks.Values, block => !block.Resolved);
        Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity));
        Assert.Equal(["new\n", "B\n"], new[] { Text(f, U, "a.txt"), Text(f, U, "b.txt") });
        Assert.Equal("", f.Git.Git("-C", checkout, "status", "--porcelain"));
        Assert.DoesNotContain(f.Read().Blocks.Values, block => !block.Resolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_ignored_file_where_the_candidate_adds_one_blocks_the_move_that_would_overwrite_it(bool afterBranch)
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("n.txt", "N\n"));
        var preview = Preview(f, U);
        var approval = f.Op();
        if (afterBranch)
            Assert.Throws<InvalidOperationException>(() => Rebaser(f, probe: reached =>
            {
                if (reached == "journal.rebase-branch-observed.after") throw new InvalidOperationException("Crashed.");
            }).Rebase(f.Lease(U), approval, preview.Identity));
        File.AppendAllText(Path.Combine(f.Git.Folder, ".git", "info", "exclude"), "n.txt\n");
        File.WriteAllText(Path.Combine(Checkout(f, U), "n.txt"), "mine\n");
        var blocked = Assert.IsType<Rebasing.Blocked>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity)).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, blocked.Problem);
        Assert.Equal(["n.txt"], Assert.IsType<BlockScope.Checkout>(blocked.Scope).Paths.ToArray());
        Assert.Equal(afterBranch ? Assert.IsType<RebaseCandidate.Clean>(preview.Candidate).Commit : CodeOf(stale), Tip(f, Branch(f, U)));
        Assert.Equal("mine\n", Text(f, U, "n.txt"));
    }

    [UnixFact]
    public async Task A_failed_reset_blocks_and_the_repeated_approval_finishes_it()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        var approval = f.Op();
        var blocked = Assert.IsType<Rebasing.Blocked>(f.FailingGit("reset").Rebase(f.Lease(U), approval, preview.Identity)).Block;
        Assert.Equal(MaterializationProblem.GitFailed, blocked.Problem);
        Assert.Equal("old\n", Text(f, U, "a.txt"));
        Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity));
        Assert.Equal("new\n", Text(f, U, "a.txt"));
        Assert.DoesNotContain(f.Read().Blocks.Values, block => !block.Resolved);
    }

    [Fact]
    public async Task A_change_after_the_reset_blocks_the_rebased_result_until_it_is_undone()
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        var approval = f.Op();
        var late = Path.Combine(Checkout(f, U), "late.txt");
        var blocked = Assert.IsType<Rebasing.Blocked>(Rebaser(f, probe: reached =>
        {
            if (reached == "journal.rebase-reset-observed.after") File.WriteAllText(late, "late\n");
        }).Rebase(f.Lease(U), approval, preview.Identity)).Block;
        Assert.Equal(MaterializationProblem.DirtyWorktree, blocked.Problem);
        Assert.Equal(stale.Id, f.Read().CurrentResults[U].Id);
        File.Delete(late);
        Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity));
        Assert.DoesNotContain(f.Read().Blocks.Values, block => !block.Resolved);
    }

    [Fact]
    public async Task A_consumer_of_a_stale_task_rebases_only_after_its_producer_does()
    {
        using var f = new PreparationFixture(Longer());
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Write(f, D, ("d.txt", "D\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var waiting = Assert.IsType<RebasePreviewRead.Rejected>(Rebaser(f).PreviewRebase(f.Lease(D))).Reason;
        Assert.Equal((RunProblem.StaleInput, U), (waiting.Problem, waiting.Task!.Value));
        Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), f.Op(), Preview(f, U).Identity));
        var preview = Preview(f, D);
        Assert.Equal([U], preview.Updated.ToArray());
        Assert.Equal(["d.txt"], preview.Changes.ToArray());
        Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(D), f.Op(), preview.Identity));
        Assert.Equal(["new\n", "B\n", "D\n"], new[] { Text(f, D, "a.txt"), Text(f, D, "b.txt"), Text(f, D, "d.txt") });
        Assert.Empty(f.Read().StaleResults.Intersect(f.Read().CurrentResults.Values.Select(result => result.Id)));
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed));
    }

    [Fact]
    public async Task A_successor_of_a_rebased_result_starts_from_it_and_passes_its_producer_recheck()
    {
        using var f = new PreparationFixture(Longer());
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        Assert.Equal(RunProblem.StaleInput, Assert.IsType<Preparation.Rejected>(await Rebaser(f).Prepare(f.Lease(D), f.Op(), new AttemptCause.Initial())).Reason.Problem);
        Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), f.Op(), Preview(f, U).Identity));
        var ready = Assert.IsType<Preparation.Ready>(await Rebaser(f).Prepare(f.Lease(D), f.Op(), new AttemptCause.Initial()));
        Assert.Equal(["new\n", "B\n"], new[] { File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")), File.ReadAllText(Path.Combine(ready.Checkout, "b.txt")) });
        Assert.IsType<ClaimCheck.Granted>(Rebaser(f).CheckInputsAndClaim(f.Lease(D), f.Op(), ready.Execution.Launch));
        Assert.Empty(f.Read().Blocks);
    }

    public static TheoryData<string> CrashPoints => new()
    {
        "git.rebase-candidate.after", "journal.rebase-plan.after", "git.rebase-retain.after", "journal.rebase-branch-intent.after",
        "git.rebase-branch.after", "journal.rebase-branch-observed.after", "journal.rebase-reset-intent.after", "git.rebase-reset.after",
        "journal.rebase-reset-observed.after", "journal.rebase-accepted.before",
    };

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task A_crash_anywhere_in_the_rebase_finishes_once_when_the_approval_repeats(string point)
    {
        using var f = new PreparationFixture(Chain());
        await Write(f, T, ("a.txt", "old\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        var candidate = Assert.IsType<RebaseCandidate.Clean>(preview.Candidate).Commit;
        var approval = f.Op();
        Assert.Throws<InvalidOperationException>(() => Rebaser(f, probe: reached =>
        {
            if (reached == point) throw new InvalidOperationException("Crashed.");
        }).Rebase(f.Lease(U), approval, preview.Identity));
        var record = f.Read();
        Assert.Equal(stale.Id, record.CurrentResults[U].Id);
        // The branch is at the stale result or the candidate, and the journal says which moves are still pending.
        Assert.Contains(Tip(f, Branch(f, U))!.Value, new[] { CodeOf(stale), candidate });
        if (record.Plans.Values.OfType<MaterializationPlan.Rebase>().Any())
        {
            // A journaled rebase holds the run open until it finishes.
            Assert.Equal(RunProblem.UnfinishedPublication, Assert.IsType<RunDecision.Rejected>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Stopped)).Reason.Problem);
            Assert.Equal(RunProblem.ReplacementConflict, Problem(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)));
            Assert.Equal(RunProblem.OperationConflict, Problem(Rebaser(f).Rebase(f.Lease(U), approval, new(new string('0', 64)))));
            var plan = record.Plans.Single(pair => pair.Value is MaterializationPlan.Rebase).Key;
            if (record.GitIntents.Any(pair => pair.Value.Plan == plan && !record.GitObservations.ContainsKey(pair.Key) &&
                (pair.Value.Mutation is GitMutation.ResetCheckout || pair.Value.Mutation is GitMutation.MoveRef move && move.Change.Ref == Branch(f, U))))
                Assert.Equal(RunProblem.UnresolvedOwnership, Assert.IsType<RebasePreviewRead.Rejected>(Rebaser(f).PreviewRebase(f.Lease(U))).Reason.Problem);
        }

        var rebased = Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), approval, preview.Identity)).Result;
        Assert.Equal(candidate, CodeOf(rebased));
        Assert.Equal(candidate, Tip(f, Branch(f, U)));
        Assert.Equal(["new\n", "B\n"], new[] { Text(f, U, "a.txt"), Text(f, U, "b.txt") });
        Assert.Equal("", f.Git.Git("-C", Checkout(f, U), "status", "--porcelain"));
        Assert.Single(f.Read().Results, result => result.Origin is ResultOrigin.Rebased);
        Assert.Single(f.Read().Plans.Values.OfType<MaterializationPlan.Rebase>());
        Assert.DoesNotContain(f.Read().Blocks.Values, block => !block.Resolved);
        Assert.IsType<RunDecision.Recorded>(f.Store.Settle(f.Permit, f.Op(), RunOutcome.Completed));
    }

    [Fact]
    public async Task A_rebase_onto_a_join_replays_the_change_onto_the_current_sources()
    {
        using var f = new PreparationFixture(Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U)), T, U), C, U));
        await Write(f, T, ("a.txt", "old\n"));
        var other = await Write(f, C, ("c.txt", "C\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        var update = await Update(f, T, ("a.txt", "new\n"));
        var preview = Preview(f, U);
        Assert.Equal([T], preview.Updated.ToArray());
        var join = preview.NewBase!.Value;
        Assert.Equal([CodeOf(update), CodeOf(other)], GitFixture.Read(f.Git.Open().ReadCommit(join)).Parents.ToArray());
        Assert.Equal(["b.txt"], preview.Changes.ToArray());
        var rebased = Assert.IsType<Rebasing.Rebased>(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)).Result;
        var joined = Assert.IsType<CodeSelection.Joined>(f.Read().Inputs[rebased.Inputs].Code).Join;
        Assert.Equal(join, joined.Commit);
        Assert.Equal([join], GitFixture.Read(f.Git.Open().ReadCommit(CodeOf(rebased))).Parents.ToArray());
        Assert.Equal(["new\n", "B\n", "C\n"], new[] { Text(f, U, "a.txt"), Text(f, U, "b.txt"), Text(f, U, "c.txt") });
        Assert.NotEqual(CodeOf(stale), Tip(f, Branch(f, U)));
    }

    [Fact]
    public async Task Updated_inputs_that_conflict_with_each_other_leave_nothing_to_rebase()
    {
        using var f = new PreparationFixture(Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U)), T, U), C, U));
        await Write(f, T, ("a.txt", "old\n"));
        await Write(f, C, ("c.txt", "C\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("c.txt", "T\n"));
        var preview = Preview(f, U);
        var conflict = Assert.IsType<RebaseCandidate.Conflicted>(preview.Candidate);
        Assert.Equal(["c.txt"], conflict.Paths.ToArray());
        Assert.Equal("The updated inputs conflict with each other in c.txt.", conflict.Detail);
        Assert.Null(preview.NewBase);
        Assert.Equal(RunProblem.InputConflict, Problem(Rebaser(f).Rebase(f.Lease(U), f.Op(), preview.Identity)));
        Assert.Equal(CodeOf(stale), Tip(f, Branch(f, U)));
    }

    [UnixFact]
    public async Task A_report_only_update_rebases_without_a_merge_even_on_git_242()
    {
        var reader = Turns.TurnFixture.Agent(T, readOnly: true);
        using var f = new PreparationFixture(Connect(FixtureWorkflow(reader, Writer(U)), T, U));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        await f.Close(ready, "T ready.\n");
        var first = Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(), ready.Execution.Launch.Attempt, ready.Execution.Inputs, "T ready.\n"));
        var stale = await Write(f, U, ("b.txt", "B\n"));
        var retry = Assert.IsType<Preparation.Ready>(await f.Prepare(T, cause: new AttemptCause.Retry(ready.Execution.Launch.Attempt, f.Op())));
        await f.Close(retry, "T again.\n");
        Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(), retry.Execution.Launch.Attempt, retry.Execution.Inputs, "T again.\n",
            ((RunEvent.ResultAccepted)first.Event).Result.Id));
        var old = Rebaser(f, environment: GitVersion(f, "git version 2.42.0"));
        var preview = Preview(f, U, old);
        Assert.Equal(preview.OldBase, preview.NewBase);
        Assert.Empty(Assert.IsType<RebaseCandidate.Clean>(preview.Candidate).Updates);
        var rebased = Assert.IsType<Rebasing.Rebased>(old.Rebase(f.Lease(U), f.Op(), preview.Identity)).Result;
        Assert.Equal(GitFixture.Read(f.Git.Open().ReadCommit(CodeOf(stale))).Tree, GitFixture.Read(f.Git.Open().ReadCommit(CodeOf(rebased))).Tree);
        Assert.Equal("B\n", Text(f, U, "b.txt"));
    }

    [UnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebasing_needs_git_243_like_a_join(bool join)
    {
        using var f = new PreparationFixture(join ? Connect(Connect(FixtureWorkflow(Writer(T), Writer(C), Writer(U)), T, U), C, U) : Chain());
        await Write(f, T, ("a.txt", "old\n"));
        if (join) await Write(f, C, ("c.txt", "C\n"));
        await Write(f, U, ("b.txt", "B\n"));
        await Update(f, T, ("a.txt", "new\n"));
        var old = Rebaser(f, environment: GitVersion(f, "git version 2.42.0"));
        var preview = Preview(f, U, old);
        var unavailable = Assert.IsType<RebaseCandidate.Unavailable>(preview.Candidate);
        Assert.Equal(MaterializationProblem.GitVersionUnsupported, unavailable.Problem);
        Assert.Equal("Rebasing needs Git 2.43 or later. Installed: git version 2.42.0.", unavailable.Detail);
        var sequence = f.Read().Sequence;
        Assert.Equal(RunProblem.UnsupportedWork, Problem(old.Rebase(f.Lease(U), f.Op(), preview.Identity)));
        Assert.Equal(sequence, f.Read().Sequence);
        Assert.IsType<RebaseCandidate.Clean>(Preview(f, U).Candidate);
    }

    [Fact]
    public async Task A_stale_report_without_code_of_its_own_is_retried_not_rebased()
    {
        var reader = Turns.TurnFixture.Agent(U, readOnly: true);
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), reader), T, U));
        await Write(f, T, ("a.txt", "old\n"));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        await f.Close(ready, "U ready.\n");
        Assert.IsType<RunDecision.Created>(f.Store.AcceptReport(f.Permit, f.Op(), ready.Execution.Launch.Attempt, ready.Execution.Inputs, "U ready.\n"));
        await Update(f, T, ("a.txt", "new\n"));
        Assert.Contains(f.Read().CurrentResults[U].Id, f.Read().StaleResults);
        Assert.Equal(RunProblem.UnsupportedResult, Assert.IsType<RebasePreviewRead.Rejected>(Rebaser(f).PreviewRebase(f.Lease(U))).Reason.Problem);
    }

    private static IReadOnlyDictionary<string, string> GitVersion(PreparationFixture f, string version)
    {
        var realGit = CommandResolver.Create((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), []).Resolve("git")!.Path;
        var bin = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(f.Git.Folder)!, "version-bin")).FullName;
        // Git 2.41 and 2.42 end merge-tree with a segmentation fault on a content merge, so this one never merges.
        Executable.Write(Path.Combine(bin, "git"), "#!/bin/sh\nfor arg do last=$arg; done\nif [ \"$last\" = version ]; then printf '%s\\n' '" + version +
            "'; exit 0; fi\ncase \" $* \" in *' merge-tree '*) exit 139;; esac\nexec '" + realGit.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\n");
        return new Dictionary<string, string>(f.Git.Environment) { ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };
    }
}
