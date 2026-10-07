# Dependency results

## Task

E2 is the slice "Materialize dependency results" of the [approved execution plan](2026-10-06-workspace-execution-plan.md#approved-delivery-slices). Its completion condition is that reports and artifacts reach successors, isolated code inputs produce a named revision, and fan-in conflicts and uncertain ownership block without corrupting work. Review fixes must use the correct code owner. It builds on the [D0 Git contract](2026-10-06-d0-design-validation.md) and the [E1 run journal](2026-10-07-run-and-input-records.md).

E2 ships as two pull requests. Pull request [#41](https://github.com/Mano-Liaoyan/iDevelop/pull/41) is E2a, based on E1's branch `feat/e1-run-records`. It adds `Materializer` and a Git adapter to `IDevelop.Core`. It adds no UI, no scheduler, and no launch path. E3 is its first caller, and E2b is the next consumer.

A fresh Opus backend review of the coordinator's design note made twelve findings. The revised note accepted eleven and partly accepted one. GPT-6.1 Sol wrote the code through Codex. A GPT-6 Astra difficult-task review and an Opus backend review reviewed the code in four rounds. The coordinator reviewed and amended every diff.

## Decisions and reasons

### E2 splits into E2a and E2b at the join composer

E2a owns the whole data contract. That covers the Git adapter, ownership keys, worktrees, result capture and publication, linear inputs, reports, artifacts, review-fix ownership, and salvage. A successor can consume one distinct accepted code revision. Two or more distinct code revisions block as `JoinRequired`.

E2b adds clean joins, durable conflict blocking, and the real-Git diamond suite. It passes a real `IJoinComposer` to `Materializer.Open` and changes no E2a file, schema, or behavior. The composer receives a `JoinRequest` with the workflow, run, task, allocated input ID, frozen sources, and the expected previous join. It must use E2a's Git adapter, repository mutation lock, commit recipes, and `RefPublisher` for every ref move. E2a then checks the returned join's persisted receipt, complete source set, object type, tree, ordered parents, and ref transition before it accepts the input. E2b must also prove that the tree is the clean merge of those sources. `JoinRequest`, the `Join` plan case, join verification, `WriteEvidence`, and `ConflictEvidence` are in E2a, and fake composers test that boundary.

The Opus design review asked for the split. It keeps the contract in one owner's hands and gives E2b a boundary it can test without editing E2a.

### Code selection is a sum type

An input's code is `Root` (the approved run base), `Single` (one accepted revision), or `Joined` (a join of at least two). A writer result carries `Produced` code with its owner, attempt base, commit, tree, and result ref. Read-only and review results carry `Forwarded` code and never become owners. `CodeSource.Owners` lists every writer owner, so a reviewer that forwards a join owns none of them. The input's code base is computed from the selection, so a caller can no longer supply it. `RunStore.Plan` freezes bindings and sources, and `Reserve` derives the input record from the plan.

New runs write schema 2. Schema-1 journals decode `codeBase` as a `Legacy` selection and re-encode byte for byte. Inventing `Root` provenance for that history was rejected.

### Each run ref has one journaled expected state

This invariant governs every ref and HEAD change that E2a makes:

- iDevelop moves the refs it owns only through `RefPublisher`, a journaled compare-and-swap. `RefPublisher` checks the live ref against the journal before it records an intent.
- `RefOwnership.Accepts` folds the journal in order into one expected state per run ref. An observation makes the state exact. A pending compare-and-swap allows its old or its new value. An editable writer's preparation opens a lease that allows descendant commits until the next observation of that branch. Retained salvage makes the recorded branch tip exact when that tip equals the expected value or a pending target, or when the branch holds a lease. Under a lease, salvage adopts any tip, including a rewind below the attempt base. Publication then blocks, because the branch tip must contain the attempt base. No older entry can excuse a value. Any other state blocks as `UncertainOwnership`.
- `reset --hard`, HEAD attach, and path removal run only after a live check that HEAD is the task branch and the branch is at its expected value.
- No branch moves away from a commit unless a retained ref keeps that commit reachable.

The lease is the one place where the journal records permission rather than an exact value. Without it, two overlapping sibling writers could not both publish, which the diamond rule requires. Readers and reviewers never hold a lease.

### The invariant replaced inference from history

The first two review rounds fixed findings one at a time, and each round failed the same gate. Each fix assumed that a ref or HEAD state was legitimate if any journal entry in the run's history, or any Git ancestry relation, could have produced it. `ExplainedRef` accepted any value that an old intent had targeted. `SiblingFastForward` accepted any descendant of a sibling's last known commit, even after the sibling could no longer write. Retry skipped a journaled attach and then ran `reset --hard` against whatever HEAD named.

Round 3 took a census before writing another fix. `CensusTests.cs` replays every reproduction from the earlier reviews plus added counterexamples, and tags each run with the mechanism that decided it. At `0f4a189`, 15 of 39 runs failed. Every failure was an excuse from an older journal entry, from Git ancestry, from snapshot equality or a matching target, from a journal assumed to describe live HEAD, or from a branch moved without retention. One more failure compared index bytes instead of index content. All 11 runs where E2a checked live state before mutating passed. GPT-6 Astra agreed to change the invariant rather than narrow the exceptions, because no failure sat in a live check. `ExplainedRef`, `SiblingFastForward`, the own-branch ancestry rule, and target adoption without an intent were deleted. Rounds 4 and 5 applied the same invariant to older code paths that it did not yet cover, and the census grew to cover them.

### Worktrees and branches have fixed names and a recorded lifecycle

Keys are the first eight hexadecimal characters of SHA-256 over the full lowercase ID, lengthened only on collision. Workflow execution requires the project folder to be Git's worktree toplevel and Git 2.39 or later.

| Purpose | Name |
| --- | --- |
| Task checkout | `<project>/.worktrees/<run>/<task>` |
| Task branch | `refs/heads/idp/<run>/task/<task>` |
| Join branch | `refs/heads/idp/<run>/join/<task>` |
| Result retention | `refs/idp/<run>/result/<task>/<attempt>` |
| Salvage retention | `refs/idp/<run>/salvage/<task>/<attempt>` |
| Later salvage of the same attempt | `refs/idp/<run>/resalvage/<task>/<attempt>/<operation>` |
| Approved base retention | `refs/idp/<run>/base` |

`Prepare` appends `/.worktrees/`, `/.idp/inputs/`, and `/.idp/outbox/` to the common `info/exclude`. It creates the checkout with `worktree add -b` and locks it with an ownership reason. A failed creation that left the branch is adopted only when the creation intent, the live tip, and the registration agree. It initializes submodules only when `submodule status --recursive` shows no `+` or `U` entry, so the update never moves a module that holds commits. A continuation turn keeps its checkout and changes. Cleanup and branch deletion are deferred.

### Publication moves the branch, the index, and the result ref

`Publish` requires a logged successful closure, the task lock, and process-tree quiescence from `IExecutionBoundary`. It checks registration, symbolic HEAD, and every run ref against `RefOwnership`. The branch tip must contain the attempt base. It captures tracked edits and nonignored additions through a temporary index, excluding execution data and nested worktrees. It freezes the `.idp/outbox` manifest and the report, and journals a commit recipe whose parent is the verified tip. Then it moves the task branch by compare-and-swap, aligns the real index to the result tree without touching working files, and creates the result ref from absent. It rechecks refs, files, index, HEAD, and quiescence before it records `ResultAccepted`.

### Git reads real commit parents

Every Git process that iDevelop starts sets `GIT_NO_REPLACE_OBJECTS=1` and points `GIT_GRAFT_FILE` at the null device. Without them, a writer can run `git replace --graft` or write `info/grafts` so that Git reports the attempt base as an ancestor of a commit that does not contain it. Publication would then accept a result outside its attempt base. Git prints a deprecation hint on stderr whenever it reads a graft file, even an empty one, so each process also passes `-c advice.graftFileDeprecated=false`.

### Flagged index entries block only when they hide a change

An index entry flagged assume-unchanged or skip-worktree can hide an edit from capture. Retry's `reset --hard` would then destroy that edit. An earlier round blocked every flagged entry. A sparse checkout flags each path outside its cone as skip-worktree, so every sparse preparation blocked as `DirtyWorktree`.

`VisibleIndex` now blocks a flagged entry only when it can hide a change. A skip-worktree path that is absent from the worktree passes. An absent assume-unchanged path blocks, because it hides a deletion. A present flagged path passes only when it is a regular file, its executable bit matches the staged mode, and `git hash-object --stdin-paths` returns the staged blob. Any other flagged path blocks as `DirtyWorktree` and names the path.

### Sparse checkouts keep their cone through publication

Git gives a new task worktree the main checkout's sparse-checkout patterns. Paths outside the cone stay absent, and capture keeps their staged blobs in the result tree. After the branch moves, publication aligns the index with `read-tree --reset`. A plain `read-tree` rebuilt the index without skip-worktree bits. The absent paths then looked deleted, and publication blocked after the branch had moved until a person ran `git sparse-checkout reapply`. The one-way merge in `read-tree --reset` keeps those bits.

### Salvage and retry keep every commit reachable

`Salvage` commits the captured tree with the observed HEAD as its parent. When HEAD does not contain the branch tip, the tip becomes a second parent. The first salvage ref is immutable, and a later capture gets a `resalvage` ref. Salvage records the branch tip and the real index digest. Ignored files and dirty submodules stay in place.

`ResetForRetry` requires retained salvage, quiescence, ownership, and an unchanged inventory, including the index digest over `ls-files --stage -v`. It moves the branch from the salvaged tip by compare-and-swap, reattaches HEAD after a live check, resets, and removes only the captured untracked paths whose type and content still match. It never runs an unrestricted `git clean`.

Every step journals its intent before the Git call and its observation after it, under operation IDs derived from the caller's operation. A retry with the same operation resumes at the first unobserved step and returns the same receipt. Tests crash at every probe point and assert that.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| Judge a ref by any journal entry or ancestry that could explain it | The census put every ref failure in this class. |
| Let salvage adopt a branch tip only when it descends from the old state | It broke two salvage and retry recovery tests. The publish-time check that the tip contains the attempt base closes the same hole. |
| End the writer's lease at its `AttemptClosed` event in E2a | `AttemptClosed` records no branch tip, so the fold has no exact value to fall back on. A probe failed 49 of 91 publication, ownership, and census tests, including every writer publishing its own commits. |
| Compare the index file's bytes | A plain `git status` rewrites stat data and blocked retry. |
| Write `HEAD` through its lock file for an atomic attach | It bypasses reftable. `update-ref --stdin` gained `symref-verify` only in Git 2.46. |
| Reset a reviewer's branch with `reset --hard` on refresh | It dropped any reviewer commit. Refresh retains the old base and moves the branch by compare-and-swap. |
| One 60-second limit for every Git call | It would kill a large checkout or a submodule clone. Limits are 60 seconds for metadata, 1 hour for work-tree operations, and 4 hours for submodule updates. |

## Changed artifacts

- `src/IDevelop.Core/Execution/Git/GitRepository.cs` is the Git adapter.
- `src/IDevelop.Core/Execution/Runs/` adds the code records, `RunLayout`, `RefOwnership`, `RefPublisher`, and the `Materializer` partial classes for preparation, inputs, outbox, publication, salvage, retry, refresh, and inspection. `RunStore`, `RunReducer`, `RunJournal`, and `RunRecords` gain schema 2.
- `src/IDevelop.Core/Execution/GitTree.cs` gives its Git processes the adapter's replace and graft settings.
- `src/IDevelop.Core/Execution/Runs/RegularFile.cs` reads file types through `statx` or `lstat`, with `lstat$INODE64` on Intel macOS.
- `tests/IDevelop.Core.Tests/Git/`, `Materialization/`, and `Runs/` test each behavior against real scratch Git.
- `.github/workflows/dotnet.yml` adds `macos-26-intel` to the CI matrix.

## Commands and observed results

| Command or check | Observed result |
| --- | --- |
| `dotnet build -c Release` | 0 warnings and 0 errors. |
| `dotnet test -c Release` on Linux | Core passes 738 tests with 10 platform skips. Desktop passes 321. E1's baseline was Core 439. |
| The census, `run-census.sh` in the coordinator's scratch folder | At `0f4a189`, 24 of 39 runs passed. At `eba029c`, 39 of 39 passed. The next round added 9 runs, and `d7dc51c` passed 48 of 48. The round after that adds 2 runs for a branch reset below its attempt base. Both fail at `d7dc51c`, and all 50 pass at `d181e4a`. The latest round adds 4 runs for a sparse checkout with an unstaged edit and for replaced or grafted parents. All 4 fail at `d181e4a`, and all 54 runs pass at the new head. |
| The Opus re-verifier's scenarios, `Reverify2Tests.cs` in the coordinator's scratch folder | 28 of 31 pass. The three failures are by design. `V2_H` is open issue 1. Two `V2_F` modes assert `Salvage.Retained` where salvage now blocks a flagged edit and leaves it in the checkout. |
| New tests against the previous head's source | Against `d7dc51c`, 6 of the 12 new test runs fail. The other 6 are controls that already passed, such as a writer that publishes on top of its base and a flagged file with a changed mode. Against `d181e4a`, 6 of the latest round's 7 new test runs fail. The seventh covers the persisted plan's attempt-base check, which already existed, and it fails when that one check is deleted. |
| `node scripts/check-licenses.mjs`, `node scripts/planweave-tokens.mjs --check`, `node scripts/fluent-icons.mjs --check` | Each exits 0. |
| `node scripts/check-handoffs.mjs` | It reports nine records, each linked from `docs/context.md` or `docs/product-direction.md`, and exits 0. |
| CI on `d7dc51c` | Every check passes on Linux, Windows, arm64 macOS, and Intel macOS. The user has since disabled GitHub Actions, so later heads pass only the local checks in this table. |

## Open issues

1. A writer's lease outlives its closure. E3 closes attempts. It must journal the quiescent branch tip at closure as an observation and end the lease there. Three measured cases show the gap. In each, a writer closes unpublished, and its commit stays reachable only through the reflog, though no iDevelop move drops it. Each case is an E3 acceptance test with the expected outcome below. Nothing in production calls `Materializer` or `CloseAttempt` yet.
   - A foreign commit lands on the closed writer's branch. Today it becomes the parent of a published result. After E3, the writer's `Publish` and a sibling's `Publish` each block as `UncertainOwnership`, record no result, and leave the foreign commit on the branch.
   - The branch is rewritten to another descendant of the attempt base. Today `Publish` accepts a result without the writer's commit. After E3, `Publish` blocks as `UncertainOwnership` and records no result.
   - The branch is reset below the attempt base, and salvage runs after closure. Today salvage's lease clause adopts the rewind, a blocked sibling then publishes, and `ResetForRetry` moves the branch. After E3, salvage returns `Retained` without adopting the rewound tip. The sibling's `Publish` stays blocked as `UncertainOwnership`, and `ResetForRetry` blocks as `UncertainOwnership` and moves no ref.
2. Live checks protect only against iDevelop's own actors. Worktree adoption, HEAD attach, `reset --hard`, path removal, and submodule update have no compare-and-swap. They run under the task lock and the repository mutation lock, right after a live check. A process outside iDevelop that writes to the repository between the check and the Git call can still change what the call acts on. E3 must keep that window under exclusive ownership through process-tree quiescence, and must not claim protection from external writers.
3. E2b's remaining scope is the real `IJoinComposer`. It chains `merge-tree --write-tree -z --messages` over the sources ordered by full task ID, creates one join whose parents are the distinct input commits, and publishes the join branch through `RefPublisher`. A conflict records `FanInConflict` with its evidence and publishes no join. Adoption requires a tree equal to a fresh clean merge. E2b runs the design note's diamond suite, reruns the unchanged E2a suite, and changes no E2a file.
4. Production `Publish` blocks as `LiveWriter` until E3 supplies process-tree evidence through `IExecutionBoundary`.
5. Read-only and review results forward code but not artifacts, so a writer's artifacts reach only its direct dependents. Forwarding artifacts needs a rule for name collisions across several dependencies.
6. Windows real-machine probes of long paths with long-path support disabled, open handles, and Job Object termination did not run beyond CI. A real Git 2.39 binary did not run. No real coding-agent client ran.
7. Rebase, cleanup, resolution approval, recovered-code acceptance, and the preapproval intent file remain deferred, with their D0 requirements.

## Next action

E2b builds the real join composer on E2a's boundary. E3 then wires closure, launch, and process-tree evidence, and must pass the acceptance tests in open issue 1.
