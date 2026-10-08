using System.Collections.Immutable;
using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private const string PreservationChanged = "The checkout changed after it was preserved. Preserve it again.";

    public RestorePreviewRead PreviewRestore(RunLease lease, AttemptId attempt, OperationId preservation)
    {
        using var authority = lease.Use();
        if (authority is null) return new RestorePreviewRead.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new RestorePreviewRead.Rejected(new(problem));
        try
        {
            var record = Read(lease.Permit.Workflow, lease.Permit.Run);
            var plan = RestorationPreservation(record, lease, attempt, preservation);
            var repository = OpenRepository();
            VerifyRepository(record, repository);
            var prepared = record.Preparations[new(attempt, 1)];
            var observation = ObserveRestore(repository, record, prepared.Location, attempt, plan);
            return new RestorePreviewRead.Previewed(BuildRestorePreview(repository, record, prepared, preservation, plan, observation.State, observation.Stages));
        }
        catch (Refusal refused) { return new RestorePreviewRead.Rejected(refused.Reason); }
        catch (MaterializationFailure failed) { return new RestorePreviewRead.Refused(failed.Problem, failed.Message, failed.Scope); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new RestorePreviewRead.Refused(MaterializationProblem.InputUnavailable, error.Message, null); }
    }

    private MaterializationPlan.Preservation RestorationPreservation(RunRecord record, RunLease lease, AttemptId attempt, OperationId preservation)
    {
        if (!record.Attempts.TryGetValue(attempt, out var writer)) throw new Refusal(new(RunProblem.UnknownAttempt));
        if (LeaseProblem(lease, writer.Task) is { } mismatch) throw new Refusal(new(mismatch));
        var id = OperationIds.Derive(preservation, "preserve-plan");
        if (!record.Preservations.ContainsKey(id) || record.Plans.GetValueOrDefault(id) is not MaterializationPlan.Preservation plan ||
            plan.Attempt != attempt || plan.Task != lease.Task) throw new Refusal(new(RunProblem.InvalidData));
        return plan;
    }

    private static (CheckoutState State, ImmutableArray<ArtifactRecord> Outbox, ImmutableArray<StageEntry> Stages) ObserveRestore(
        GitRepository repository, RunRecord record, ExecutionLocation location, AttemptId attempt, MaterializationPlan.Preservation plan,
        Action<byte[]>? retainLock = null)
    {
        if (!Value(repository.PlainIndex(Checkout(repository, location.Owner))))
            throw Fault(MaterializationProblem.DirtyWorktree,
                "Restore needs a plain index. The index marks entries assume-unchanged or skip-worktree.", new([], [], false));
        return ReadPreservationCheckout(repository, record, location, attempt, (name, bytes) =>
        {
            if (name == "index.lock") retainLock?.Invoke(bytes);
            return new(name == "index" ? plan.Preserved.Index?.RelativePath ?? "index" : name == "index.lock"
                ? plan.Preserved.IndexLock?.Bytes.RelativePath ?? "index.lock" : plan.Outbox.FirstOrDefault(a => "outbox/" + a.Name == name)?.StoredPath ?? name,
                Revision.Hash(bytes), bytes.LongLength);
        });
    }

    private static string? ComponentValue(CheckoutState state, string component) => component switch
    {
        "branch" => state.Branch?.Hex,
        "head" => state.SymbolicHead,
        "files" => state.Files.Hex,
        "index" => state.IndexTree?.Hex,
        _ => throw new InvalidOperationException(),
    };

    private static BlockScope? CheckoutDifference(GitRepository repository, WorktreeOwner owner, CheckoutState first, CheckoutState second,
        bool compareIndexDigest = true, bool compareUntracked = true)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var refs = ImmutableArray.CreateBuilder<string>();
        if (first.Branch != second.Branch) refs.Add(owner.Branch);
        if (first.Head != second.Head || first.SymbolicHead != second.SymbolicHead) refs.Add("HEAD");
        if (first.Files != second.Files) paths.UnionWith(Value(repository.DiffTreePaths(first.Files, second.Files)));
        if (first.IndexTree != second.IndexTree && first.IndexTree is { } ai && second.IndexTree is { } bi)
            paths.UnionWith(Value(repository.DiffTreePaths(ai, bi)));
        if (compareUntracked)
        {
            var a = first.Untracked.ToDictionary(f => f.RelativePath, f => (f.Content, f.ByteLength), StringComparer.Ordinal);
            var b = second.Untracked.ToDictionary(f => f.RelativePath, f => (f.Content, f.ByteLength), StringComparer.Ordinal);
            foreach (var path in a.Keys.Union(b.Keys))
                if (!a.TryGetValue(path, out var av) || !b.TryGetValue(path, out var bv) || av != bv) paths.Add(path);
        }
        var locks = first.IndexLock?.Identity != second.IndexLock?.Identity || !SameBytes(first.IndexLock?.Bytes, second.IndexLock?.Bytes);
        if (paths.Count == 0 && refs.Count == 0 && !locks && first.IndexTree == second.IndexTree &&
            (!compareIndexDigest || SameBytes(first.Index, second.Index))) return null;
        return new([.. paths], refs.ToImmutable(), locks);
    }

    private RestorePreview BuildRestorePreview(GitRepository repository, RunRecord record, PreparedExecution prepared, OperationId preservation,
        MaterializationPlan.Preservation plan, CheckoutState current, ImmutableArray<StageEntry> stages)
    {
        var owner = prepared.Location.Owner;
        if (CheckoutDifference(repository, owner, plan.Preserved, current) is { } difference)
            throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, difference);
        var fold = CheckoutBaseline.Fold(record, owner, commit => Value(repository.ReadCommit(commit)).Tree);
        var unfinished = fold.Components.Values.OfType<ComponentBaseline.Unfinished>().FirstOrDefault();
        var lockOnly = unfinished is not null && current.IndexLock is not null;
        if (unfinished is not null && !lockOnly)
        {
            throw Fault(MaterializationProblem.UncertainOwnership,
                UnfinishedStepDetail(record, unfinished.Owner), new([], [], false));
        }
        string? Target(string name)
        {
            var live = ComponentValue(current, name);
            if (lockOnly) return live;
            return fold.Components[name] switch
            {
                ComponentBaseline.Fixed fixedValue => fixedValue.Value ?? live,
                ComponentBaseline.Pending pending => live == pending.Before || live == pending.After ? live : pending.Before,
                _ => live,
            };
        }
        var files = Target("files")!;
        var index = Target("index");
        if (!lockOnly && fold.Components["files"] is ComponentBaseline.Pending fp && fold.Components["index"] is ComponentBaseline.Pending ip && fp.Owner == ip.Owner)
        {
            var held = current.Files.Hex == fp.Before && current.IndexTree?.Hex == ip.Before ||
                current.Files.Hex == fp.After && current.IndexTree?.Hex == ip.After;
            if (!held) { files = fp.Before; index = ip.Before; }
        }
        var branch = Target("branch") is { } tip ? new CommitId(tip) : (CommitId?)null;
        var head = Target("head");
        var indexEvidence = lockOnly || index == current.IndexTree?.Hex && fold.Components["index"] is ComponentBaseline.Pending
            ? current.Index : fold.Index;
        if (!lockOnly && fold.Components["index"] is ComponentBaseline.Fixed { Value: null } && !SameBytes(current.Index, fold.Index))
            throw Fault(MaterializationProblem.DirtyWorktree,
                "The baseline index was recorded before index retention. Restore the index outside iDevelop, then preserve again.", new([], [], false));
        var to = current with { Branch = branch, Head = head == owner.Branch ? branch : current.Head, SymbolicHead = head,
            Files = new(files), Index = indexEvidence, IndexTree = index is null ? null : new(index), IndexLock = null };
        var paths = RestorePaths(repository, current.Files, to.Files);
        if (!lockOnly)
        {
            var indexPaths = to.IndexTree is { } targetIndex
                ? Value(repository.TreeEntries(targetIndex)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal) : [];
            to = to with { Untracked = [.. Value(repository.TreeEntries(to.Files)).Where(e => !indexPaths.Contains(e.Path) && e.Mode == "100644")
                .OrderBy(e => e.Path, StringComparer.Ordinal).Select(e =>
                {
                    var bytes = Value(repository.WorkingTreeBlobBytes(Checkout(repository, owner), e.Path, e.Object));
                    return new EvidenceFile(e.Path, Revision.Hash(bytes), bytes.LongLength);
                })] };
        }
        var indexMoves = IndexDiffers(current, to);
        if (indexMoves)
        {
            if (current.IndexTree is { } oldIndex && to.IndexTree is { } newIndex)
            {
                var before = Value(repository.TreeEntries(oldIndex)).ToDictionary(e => e.Path, StringComparer.Ordinal);
                var after = Value(repository.TreeEntries(newIndex)).ToDictionary(e => e.Path, StringComparer.Ordinal);
                foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
                {
                    before.TryGetValue(path, out var a);
                    after.TryGetValue(path, out var b);
                    if ((a?.Mode == "160000" || b?.Mode == "160000") && a != b)
                        throw Fault(MaterializationProblem.SubmoduleUnavailable, "Restore cannot change a submodule: " + path, new([path], [], false));
                }
            }
            if (current.Index is null) throw Fault(MaterializationProblem.DirtyWorktree, "Restore needs an index. The index is absent.", new([], [], false));
        }
        if (!lockOnly)
        {
            if (!stages.IsEmpty) throw Fault(MaterializationProblem.DirtyWorktree, "Restore needs a plain index. The index has unresolved stages.", new([.. stages.Select(s => s.Path).Distinct().Order(StringComparer.Ordinal)], [], false));
            if (!Value(repository.PlainIndex(Checkout(repository, owner))))
                throw Fault(MaterializationProblem.DirtyWorktree, "Restore needs a plain index. The index marks entries assume-unchanged or skip-worktree.", new([], [], false));
        }
        if (!paths.IsEmpty)
        {
            var checkoutVolume = _volumes(Checkout(repository, owner));
            var gitVolume = _volumes(Path.GetDirectoryName(Value(repository.IndexPath(Checkout(repository, owner))))!);
            if (checkoutVolume is null || gitVolume is null)
                throw Fault(MaterializationProblem.DirtyWorktree,
                    "iDevelop cannot confirm that the checkout and its Git folder share a file system on this system, so Restore will not replace files. Change them outside iDevelop, then preserve again.", new([.. paths.Select(p => p.Path)], [], false));
            if (checkoutVolume != gitVolume)
                throw Fault(MaterializationProblem.DirtyWorktree,
                    "The checkout and its Git folder are on different file systems, so Restore cannot replace files atomically.", new([.. paths.Select(p => p.Path)], [], false));
        }
        if (current.IndexLock is { Identity: null })
            throw Fault(MaterializationProblem.DirtyWorktree,
                "iDevelop cannot read this file's identity on this system, so it will not remove index.lock. Remove it outside iDevelop, then preserve again.", new([], [], true));
        var refs = ImmutableArray.CreateBuilder<string>();
        if (current.Branch != to.Branch) refs.Add(owner.Branch);
        if (current.SymbolicHead != to.SymbolicHead) refs.Add("HEAD");
        var blocks = record.Blocks.Where(b => !b.Value.Resolved && b.Value.Block.Task == owner.Task && b.Value.Block.Scope is not null)
            .OrderBy(b => record.Receipts[b.Key].Sequence).ToArray();
        var repairs = blocks.Where(b => lockOnly
            ? b.Value.Block.Scope is { Paths.IsEmpty: true, Refs.IsEmpty: true, IndexLock: true }
            : b.Value.Block.Scope!.Refs.All(r => r == owner.Branch || r == "HEAD")).Select(b => b.Key).ToImmutableArray();
        var rechecks = lockOnly ? [] : blocks.Where(b => b.Value.Block.Scope!.Refs.Any(r => r != owner.Branch && r != "HEAD")).Select(b => b.Key).ToImmutableArray();
        var supersedes = RunReducer.UnfinishedRestorations(record, owner).Cast<OperationId?>().SingleOrDefault();
        var preview = new RestorePreview(preservation, current, fold.Components, to, paths, refs.ToImmutable(), current.IndexLock,
            repairs, rechecks, SharedRefSnapshot(repository, record), supersedes, new Digest(""));
        return preview with { Identity = Revision.Hash(RunJournal.Canonical(preview)) };
    }

    private static bool IndexDiffers(CheckoutState from, CheckoutState to) =>
        to.Index is not null ? !SameBytes(from.Index, to.Index) : from.IndexTree != to.IndexTree;

    private static ImmutableArray<PathRestore> RestorePaths(GitRepository repository, TreeId from, TreeId to)
    {
        var before = Value(repository.TreeEntries(from)).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var after = Value(repository.TreeEntries(to)).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var changed = before.Keys.Union(after.Keys).Where(path => !before.TryGetValue(path, out var a) || !after.TryGetValue(path, out var b) ||
            a.Object != b.Object || a.Mode != b.Mode).Order(StringComparer.Ordinal);
        var paths = ImmutableArray.CreateBuilder<PathRestore>();
        foreach (var path in changed)
        {
            before.TryGetValue(path, out var a);
            after.TryGetValue(path, out var b);
            var aKind = Kind(before, path, a);
            var bKind = Kind(after, path, b);
            if (a?.Mode == "160000" || b?.Mode == "160000")
                throw Fault(MaterializationProblem.SubmoduleUnavailable, "Restore cannot change a submodule: " + path, new([path], [], false));
            if (aKind is not ("absent" or "a file") || bKind is not ("absent" or "a file"))
                throw Fault(MaterializationProblem.DirtyWorktree,
                    $"Restore changes only regular, non-executable files. {path} is {aKind} now and {bKind} in the baseline. Change it outside iDevelop, then preserve again.", new([path], [], false));
            paths.Add(new(path, a?.Object, b?.Object));
        }
        return paths.ToImmutable();

        static string Kind(Dictionary<string, StageEntry> entries, string path, StageEntry? entry) =>
            entries.Keys.Any(p => p.StartsWith(path + "/", StringComparison.Ordinal)) ? "a folder" : entry?.Mode switch
            {
                null => "absent", "100644" => "a file", "100755" => "an executable file", "120000" => "a symbolic link", "160000" => "a submodule",
                _ => "a file",
            };
    }

    public Restoration Restore(RunLease lease, OperationId operation, AttemptId attempt, OperationId preservation,
        OperationId confirmation, Digest preview)
    {
        using var authority = lease.Use();
        if (authority is null) return new Restoration.Rejected(new(RunProblem.TaskBusy));
        if (LeaseProblem(lease) is { } problem) return new Restoration.Rejected(new(problem));
        var permit = lease.Permit;
        var planId = OperationIds.Derive(operation, "restore-plan");
        InputId? inputs = null;
        var step = "restore-preconditions";
        ImmutableArray<EvidenceFile> evidence = [];
        try
        {
            if (confirmation.Value == Guid.Empty) return new Restoration.Rejected(new(RunProblem.ConfirmationRequired));
            var record = Read(permit.Workflow, permit.Run);
            if (RunReducer.RestorationSuperseded(record, planId)) return new Restoration.Rejected(new(RunProblem.ReplacementConflict));
            var preserved = RestorationPreservation(record, lease, attempt, preservation);
            var existing = record.Plans.GetValueOrDefault(planId);
            if (existing is not null && (existing is not MaterializationPlan.Restoration old || old.Attempt != attempt ||
                old.Preservation != preservation || old.Confirmation != confirmation || old.Preview != preview))
                return new Restoration.Rejected(new(RunProblem.OperationConflict));
            if (record.Restorations.TryGetValue(planId, out var receipt)) return new Restoration.Restored(receipt);
            if (preserved.Preserved.Index is { } preservedIndex) evidence = [preservedIndex];
            var prepared = record.Preparations[new(attempt, 1)];
            inputs = prepared.Inputs;
            var repository = OpenRepository();
            using var mutation = repository.TakeMutationLock();
            if (mutation is null) return new Restoration.Rejected(new(RunProblem.JournalBusy));
            record = Read(permit.Workflow, permit.Run);
            if (RunReducer.RestorationSuperseded(record, planId)) return new Restoration.Rejected(new(RunProblem.ReplacementConflict));
            if (record.UnresolvedClaims.Any(key => record.Preparations[key].Location.Owner == prepared.Location.Owner))
                return new Restoration.Rejected(new(RunProblem.UnresolvedOwnership));
            VerifyRepository(record, repository);
            VerifyOwnedCheckout(repository, prepared.Location, record);
            if (Value(repository.ReadRef(preserved.Ref)) != preserved.Commit ||
                Value(repository.CreateCommit(preserved.Recipe)) != preserved.Commit)
                throw Fault(MaterializationProblem.UncertainOwnership, "The retained preservation ref or commit differs from its recipe.", new([], [preserved.Ref], false));
            var checkout = Checkout(repository, prepared.Location.Owner);
            MaterializationPlan.Restoration plan;
            if (existing is MaterializationPlan.Restoration persisted) plan = persisted;
            else
            {
                step = "restore-preview";
                var observation = ObserveRestore(repository, record, prepared.Location, attempt, preserved, RetainLock);
                var fresh = BuildRestorePreview(repository, record, prepared, preservation, preserved, observation.State, observation.Stages);
                if (fresh.Identity != preview) return new Restoration.Rejected(new(RunProblem.EvidenceMismatch));
                VerifyIgnoredObstructions(repository, checkout, [.. fresh.Paths.Select(p => p.Path)]);
                plan = new(lease.Task, attempt, preservation, fresh.Current, fresh.To, fresh.Paths, fresh.Repairs, fresh.Rechecks, confirmation, preview)
                    { Supersedes = fresh.Supersedes };
                step = "restore-plan";
                Journal(step, () => _store.Record(permit, planId, new RunEvent.Planned(plan)));
            }
            var scratchRoot = Path.Combine(Path.GetDirectoryName(Value(repository.IndexPath(checkout)))!, "idevelop-restore");
            var scratch = Path.Combine(scratchRoot, planId.Value.ToString("D"));
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            VerifyIgnoredObstructions(repository, checkout, [.. plan.Paths.Select(p => p.Path)]);
            Recheck();
            if (plan.From.IndexLock is { } indexLock)
            {
                var remove = new GitMutation.RemoveIndexLock(plan.Task, indexLock);
                if (!PublicationObserved(Read(permit.Workflow, permit.Run), planId, remove))
                {
                    step = "restore-lock";
                    Recheck();
                    var intended = Intent(step, remove);
                    var result = Mutate(step, () =>
                    {
                        Recheck();
                        var bytes = RunStorage.Read(new RunStorage(_project, record.Workflow, record.Id).Folder,
                            indexLock.Bytes.RelativePath, indexLock.Bytes.Content, indexLock.Bytes.ByteLength);
                        return FileIdentities.Remove(Value(repository.IndexPath(checkout)) + ".lock", bytes, indexLock.Identity!.Value);
                    });
                    if (result is LockRemoval.Changed or LockRemoval.Unavailable)
                        throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, new([], [], true));
                    Observed(step, intended, result == LockRemoval.Absent, null);
                }
            }
            if (plan.From.SymbolicHead != plan.To.SymbolicHead)
            {
                var attach = new GitMutation.AttachHead(plan.Task, plan.To.SymbolicHead!);
                if (!PublicationObserved(Read(permit.Workflow, permit.Run), planId, attach))
                {
                    step = "restore-head";
                    Recheck();
                    if (Value(repository.Worktrees()).Any(w => w.Branch == attach.Branch && !SamePath(w.Path, checkout)))
                        throw Fault(MaterializationProblem.UncertainOwnership, "The task branch is registered at another checkout.", new([], [attach.Branch], false));
                    var adopted = Value(repository.SymbolicHead(checkout)) == attach.Branch;
                    var intended = Intent(step, attach);
                    if (!adopted)
                    {
                        var result = Mutate(step, () => { Recheck(); return repository.AttachHead(checkout, attach.Branch); });
                        if (result.ExitCode != 0) throw Fault(MaterializationProblem.GitFailed, result.Stderr, new([], ["HEAD"], false));
                    }
                    Observed(step, intended, adopted, attach.Branch);
                }
            }
            if (plan.From.Branch != plan.To.Branch)
            {
                step = "restore-branch";
                Recheck();
                var publisher = new RefPublisher(_store, point =>
                {
                    _probe?.Invoke(point);
                    if (point == "git.restore-branch.before") Recheck();
                });
                RequirePublication(publisher.Restore(permit, operation, planId, step, repository,
                    new(prepared.Location.Owner.Branch, plan.From.Branch, plan.To.Branch!.Value)));
            }
            if (!plan.Paths.IsEmpty)
            {
                var files = new GitMutation.RestoreFiles(plan.Task, plan.Paths);
                if (!PublicationObserved(Read(permit.Workflow, permit.Run), planId, files))
                {
                    step = "restore-files";
                    Recheck();
                    var intended = Intent(step, files);
                    var allAdopted = true;
                    foreach (var path in plan.Paths)
                    {
                        RecheckRecord();
                        var adopted = Mutate("restore-file-" + path.Path, () =>
                        {
                            RecheckRecord();
                            var destination = RunStorage.SafePath(checkout, path.Path);
                            var content = LiveContent(repository, checkout, path.Path);
                            if (content == path.To) return true;
                            if (content != path.From) throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, new([path.Path], [], false));
                            if (path.To is null) File.Delete(destination);
                            else
                            {
                                Directory.CreateDirectory(scratch);
                                var temporary = Path.Combine(scratch, Revision.Hash(path.Path).Sha256);
                                var bytes = Value(repository.WorkingTreeBlobBytes(checkout, path.Path, path.To));
                                File.WriteAllBytes(temporary, bytes);
                                if (Value(repository.WorkingFileBlob(checkout, path.Path, temporary)) != path.To)
                                    throw Fault(MaterializationProblem.InputUnavailable, "The restore blob differs from its preview.", new([path.Path], [], false));
                                _probe?.Invoke("restore.file." + path.Path + ".written");
                                RecheckRecord();
                                if (LiveContent(repository, checkout, path.Path) != path.From)
                                    throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, new([path.Path], [], false));
                                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                                File.Move(temporary, destination, overwrite: true);
                            }
                            return false;
                        });
                        allAdopted &= adopted;
                    }
                    Observed(step, intended, allAdopted, plan.To.Files.Hex);
                }
            }
            if (IndexDiffers(plan.From, plan.To))
            {
                var currentRecord = Read(permit.Workflow, permit.Run);
                if (!currentRecord.GitIntents.Any(p => p.Value.Plan == planId && p.Value.Mutation is GitMutation.AlignIndex && currentRecord.GitObservations.ContainsKey(p.Key)))
                {
                    step = "restore-index";
                    Recheck();
                    var current = ObserveRestore(repository, currentRecord, prepared.Location, attempt, preserved).State;
                    var existingIndex = currentRecord.GitIntents.FirstOrDefault(p => p.Value.Plan == planId && p.Value.Mutation is GitMutation.AlignIndex);
                    var align = existingIndex.Value?.Mutation as GitMutation.AlignIndex ?? new GitMutation.AlignIndex(plan.Task, current.Index?.Content, plan.To.IndexTree!.Value);
                    var intended = Intent(step, align);
                    var result = Mutate(step, () =>
                    {
                        Recheck();
                        var live = ObserveRestore(repository, Read(permit.Workflow, permit.Run), prepared.Location, attempt, preserved, RetainLock).State;
                        if (live.IndexLock is not null)
                        {
                            throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, new([], [], true));
                        }
                        return repository.AlignIndex(checkout, align.Expected, align.Target);
                    });
                    if (result is IndexAlignment.Unexpected) throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, new([], [], false));
                    if (result is IndexAlignment.Failed failed) throw Fault(MaterializationProblem.GitFailed, failed.Detail, new([], [], false));
                    Observed(step, intended, result is IndexAlignment.AlreadyAligned, align.Target.Hex);
                }
            }
            step = "restore-verify";
            var final = ObserveRestore(repository, Read(permit.Workflow, permit.Run), prepared.Location, attempt, preserved, RetainLock).State;
            if (CheckoutDifference(repository, prepared.Location.Owner, plan.To, final, plan.To.Index is not null, false) is { } remaining)
                throw Fault(MaterializationProblem.DirtyWorktree, PreservationChanged, remaining);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            if (Directory.Exists(scratchRoot) && !Directory.EnumerateFileSystemEntries(scratchRoot).Any()) Directory.Delete(scratchRoot);
            var resolved = ResolveRestoreBlocks(permit, planId, repository, prepared, plan);
            step = "restored";
            receipt = (RunEvent.Restored)DecisionEvent(Journal(step, () => _store.Record(permit,
                OperationIds.Derive(operation, "restored"), new RunEvent.Restored(planId, resolved))));
            return new Restoration.Restored(receipt);

            void RetainLock(byte[] bytes)
            {
                var storage = new RunStorage(_project, permit.Workflow, permit.Run);
                evidence = [storage.WriteEvidence(OperationIds.Derive(operation, "restore-lock-" + Revision.Hash(bytes).Sha256), "index.lock", bytes)];
            }
            OperationId Intent(string label, GitMutation move)
            {
                var id = OperationIds.Derive(operation, label + "-intent");
                Journal(label + "-intent", () => _store.Record(permit, id, new RunEvent.GitIntended(planId, move)));
                return id;
            }
            void Observed(string label, OperationId intent, bool adopted, string? value) =>
                Journal(label + "-observed", () => _store.Record(permit, OperationIds.Derive(operation, label + "-observed"), new RunEvent.GitObserved(intent, new(adopted, value))));
            RunRecord RecheckRecord()
            {
                var liveRecord = Read(permit.Workflow, permit.Run);
                if (RunReducer.RestorationSuperseded(liveRecord, planId)) throw new Refusal(new(RunProblem.ReplacementConflict));
                if (liveRecord.UnresolvedClaims.Any(k => liveRecord.Preparations[k].Location.Owner == prepared.Location.Owner))
                    throw new Refusal(new(RunProblem.UnresolvedOwnership));
                return liveRecord;
            }
            void Recheck()
            {
                var liveRecord = RecheckRecord();
                var live = ObserveRestore(repository, liveRecord, prepared.Location, attempt, preserved, RetainLock).State;
                VerifyRestoreInventory(repository, liveRecord, planId, prepared.Location.Owner, plan, live);
                VerifyIgnoredObstructions(repository, checkout, [.. plan.Paths.Select(p => p.Path)]);
            }
        }
        catch (Refusal refused) { return new Restoration.Rejected(refused.Reason); }
        catch (MaterializationFailure failed)
        { return RestorationBlock(permit, operation, step, new(planId, lease.Task, attempt, failed.Problem, inputs, evidence, failed.Message) { Scope = failed.Scope }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return RestorationBlock(permit, operation, step, new(planId, lease.Task, attempt, MaterializationProblem.InputUnavailable, inputs, evidence, error.Message)); }
    }

    private static string? LiveContent(GitRepository repository, string checkout, string relativePath)
    {
        var path = RunStorage.SafePath(checkout, relativePath);
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        RegularFile.Verify(path);
        return Value(repository.WorkingFileBlob(checkout, relativePath, path));
    }

    private static void VerifyRestoreInventory(GitRepository repository, RunRecord record, OperationId planId, WorktreeOwner owner,
        MaterializationPlan.Restoration plan, CheckoutState live)
    {
        var intents = record.GitIntents.Values.Where(i => i.Plan == planId).Select(i => i.Mutation).ToArray();
        var refs = ImmutableArray.CreateBuilder<string>();
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        if (Completed<GitMutation.RestoreRef>() ? live.Branch != plan.To.Branch :
            live.Branch != plan.From.Branch && !(intents.OfType<GitMutation.RestoreRef>().Any() && live.Branch == plan.To.Branch)) refs.Add(owner.Branch);
        var attached = intents.OfType<GitMutation.AttachHead>().Any();
        if (Completed<GitMutation.AttachHead>() ? live.SymbolicHead != plan.To.SymbolicHead :
            live.SymbolicHead != plan.From.SymbolicHead && !(attached && live.SymbolicHead == plan.To.SymbolicHead)) refs.Add("HEAD");
        var expectedHead = live.SymbolicHead == owner.Branch ? live.Branch : plan.From.Head;
        if (live.Head != expectedHead) refs.Add("HEAD");
        var a = Value(repository.TreeEntries(plan.From.Files)).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var b = Value(repository.TreeEntries(plan.To.Files)).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var c = Value(repository.TreeEntries(live.Files)).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var fileIntent = intents.OfType<GitMutation.RestoreFiles>().Any();
        foreach (var path in a.Keys.Union(b.Keys).Union(c.Keys))
        {
            a.TryGetValue(path, out var from);
            b.TryGetValue(path, out var to);
            c.TryGetValue(path, out var current);
            var moves = plan.Paths.Any(p => p.Path == path);
            if (moves && Completed<GitMutation.RestoreFiles>() ? current != to : current != from && !(fileIntent && moves && current == to)) paths.Add(path);
        }
        var indexOkay = Completed<GitMutation.AlignIndex>() ? !IndexDiffers(live, plan.To) :
            SameBytes(live.Index, plan.From.Index) && live.IndexTree == plan.From.IndexTree ||
            intents.OfType<GitMutation.AlignIndex>().Any() && !IndexDiffers(live, plan.To);
        if (!indexOkay && live.IndexTree is { } li && plan.From.IndexTree is { } fi)
            paths.UnionWith(Value(repository.DiffTreePaths(li, fi)));
        var lockOkay = Completed<GitMutation.RemoveIndexLock>() ? live.IndexLock is null :
            SameBytes(live.IndexLock?.Bytes, plan.From.IndexLock?.Bytes) && live.IndexLock?.Identity == plan.From.IndexLock?.Identity ||
            intents.OfType<GitMutation.RemoveIndexLock>().Any() && live.IndexLock is null;
        if (paths.Count != 0 || refs.Count != 0 || !indexOkay || !lockOkay)
            throw Fault(refs.Count == 0 ? MaterializationProblem.DirtyWorktree : MaterializationProblem.UncertainOwnership,
                PreservationChanged, new([.. paths], [.. refs.Distinct()], !lockOkay));

        bool Completed<T>() where T : GitMutation => record.GitIntents.Any(i => i.Value.Plan == planId && i.Value.Mutation is T &&
            record.GitObservations.ContainsKey(i.Key));
    }

    private ImmutableArray<OperationId> ResolveRestoreBlocks(CoordinatorPermit permit, OperationId planId, GitRepository repository,
        PreparedExecution prepared, MaterializationPlan.Restoration plan)
    {
        var storage = new RunStorage(_project, permit.Workflow, permit.Run);
        var record = Read(permit.Workflow, permit.Run);
        var snapshot = record.Receipts.Values.Select(e => e.Event).OfType<RunEvent.Prepared>().Single(e => e.Execution.Launch == prepared.Launch).SharedRefs;
        var before = JsonSerializer.Deserialize<SortedDictionary<string, CommitId>>(
            RunStorage.Read(storage.Folder, snapshot.RelativePath, snapshot.Content, snapshot.ByteLength), RunJournal.Options)!;
        return record.Blocks.Where(b => !b.Value.Resolved && b.Value.Block.Task == plan.Task &&
            (plan.Repairs.Contains(b.Key) || plan.Rechecks.Contains(b.Key) || b.Value.Block.Operation == planId) &&
            (b.Value.Block.Scope is not { } scope || scope.Refs.All(name => name == "HEAD" || name == prepared.Location.Owner.Branch ||
                (name == "refs/stash" ? Value(repository.ReadRef(name)) == (before.TryGetValue(name, out var old) ? old : (CommitId?)null) :
                    RefOwnership.Accepts(record, repository, name, Value(repository.ReadRef(name)))))))
            .OrderBy(b => record.Receipts[b.Key].Sequence).Select(b => b.Key).ToImmutableArray();
    }

    private Restoration RestorationBlock(CoordinatorPermit permit, OperationId operation, string step, MaterializationBlock block) =>
        Block(permit, operation, step, ScopedCheckoutBlock(block)) switch
        {
            Preparation.Blocked blocked => new Restoration.Blocked(blocked.Block),
            Preparation.Rejected rejected => new Restoration.Rejected(rejected.Reason),
            _ => throw new InvalidOperationException(),
        };
}
