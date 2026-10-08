# Workflow execution design

## Task

E3 is the slice "Execute the workflow" of the [approved execution plan](2026-10-06-workspace-execution-plan.md#approved-delivery-slices). Its completion condition is that Run Workflow finds roots, launches eligible work, waits at questions and approvals, starts successors once, and stops safely. Review advancement and scheduling share one launch authority. Failure affects only dependent branches.

This record holds E3's design. No E3 code exists. GPT-6 Astra wrote the design note in four versions in the judgment role. Fresh Opus sessions reviewed version 1 and version 3. The note builds on the [D0 Git contract](2026-10-06-d0-design-validation.md), the [E1 run journal](2026-10-07-run-and-input-records.md), the [conversation backend](2026-10-07-conversation.md), and the [dependency results record](2026-10-07-dependency-results.md). E2b, pull request [#44](https://github.com/Mano-Liaoyan/iDevelop/pull/44), merged as `04a976a`. Implementation now waits only for a review that confirms version 4.

## Decisions and reasons

### The user settled three questions on 2026-10-07

The Opus review of version 1 found two places where the note reversed or extended recorded behavior. The user decided both by accepting the coordinator's recommendations.

1. Result acceptance does not depend on proof that a writer's processes stopped. A task's result is the commit captured when its turn ends, taken twice a short interval apart and accepted only if both captures match. Any later change in that worktree becomes a blocked state with evidence, through E2a's live checks before every Git move. Process containment serves cleanup only, with the existing Windows Job Object and a process group on Linux and macOS. All three platforms get workflow execution in E3, and no platform waits for containment.
2. Workflow turns stop the processes their agent started when the turn ends. Standalone single-task runs keep the recorded rule that what the agent started on purpose keeps running. On Linux and macOS the stop is best effort. A process that leaves its group escapes, and correctness does not rely on the stop.
3. A review fix interrupted by closing the app does not resume by itself. On reopen the person chooses **Continue fix** or **Retry fix**.

These decisions replace the process-tree evidence that E2a expected through `IExecutionBoundary`. E3a.2 removed that interface. `Publish` now consumes a turn's root observation and matching captures, and salvage and retry reset check recorded claim ownership instead.

### One coordinator decides from durable records

`WorkflowRunCoordinator` lives in Core, hosted by the open project's `ProjectRuns`, with no Avalonia dependency. Run journals and run-owned attempt logs are the authority. Readiness, staleness, waiting, and blockers are projections of them. A serialized command queue orders decisions, and slow Git and client work runs outside that queue. Commands carry repository, workflow, and run identity, and selection never supplies it.

An operating-system coordinator lease and an opaque `CoordinatorPermit` give one window control of a run. A second window can read history, but every command returns `Unavailable` with "This run is controlled by another iDevelop window." A free task lock does not let a run-owned task run standalone.

Initial turns, replies, queued follow-ups, reviewer turns, and fixes all go through the same launch authority. `ProjectRuns.Follow` and `Finish` no longer advance run-owned reviews on their own. Bring-up allows one starting or running client root per run. Waiting, gates, capture, publication, and cleanup use no client slot.

### Settlement captures the result twice after root exit

Settlement follows this order.

1. On root exit, observe the task branch tip and symbolic HEAD before cleanup or output drainage.
2. Record `RootExitObserved` and end the writer's ancestry permission. If that fails, mark the turn and its branch unresolved.
3. Free the client slot and request bounded cleanup of descendants.
4. Drain output within a bound, flush the log, and freeze its checkpoint.
5. Capture twice, 250 ms apart through the injected `TimeProvider`, record both captures and their outcome, and record `CloseTurn`.
6. Release the task lease only against a durable outcome.

Both captures use one frozen commit recipe. They compare the candidate commit, tree, branch tip, HEAD, index, file inventory, report, and artifacts, and each tip must match `RootExitObserved`. A file or index difference reports `DirtyWorktree`. An unexplained ref or HEAD change reports `UncertainOwnership`. The coordinator never samples again until a changing worktree happens to match.

Ending ancestry permission at root exit closes the gap that the E2 record's [open issue 1](2026-10-07-dependency-results.md#open-issues) measured. In that gap a writer's commit lease outlived its closure, so salvage, retry, and Continue could adopt a stray commit.

### Later changes block instead of changing an accepted result

Publication requires strict successful closure, a matching capture, checked ownership, and attempt-base ancestry. E2 publishes the frozen candidate by a journaled compare-and-swap. Every later Git move keeps E2a's live checks. Before a consumer's first claim, the coordinator rechecks its producers' checkouts against their journaled baselines. A change found there creates a durable block, and an accepted result stays immutable. Completion uses recorded outcomes and runs no fresh filesystem census.

A capture or drift block offers **Preserve and restore**. It retains the current files, index, and any commit a restore could displace. It takes two matching captures, previews the current state against a named journal baseline, and restores only after confirmation. A preservation never creates a successful result.

### E3a.2 settled seven details while it built captures

1. A schema-3 turn can still close without a capture, through `CloseTurn` without one or through recovery. Such a turn can never publish. Publication requires the final turn's capture, and a capture-linked closure still requires readable log evidence. An unreadable log leaves a durable failed capture and an unresolved claim.
2. Salvage takes one capture, as in E2. The design's two matching preservation captures arrive with **Preserve and restore** in E3a.3. Retry reset keeps its unchanged-inventory check, which guards the destructive step.
3. Salvage and `Publish` never remove an existing `index.lock`. Each blocks as `DirtyWorktree` before any move and keeps the lock's bytes as evidence, named by their SHA-256, so a changed lock seen again by the same operation keeps its own evidence. A lock that appears during index alignment blocks the same way. Removal waits for E3a.3's confirmed restoration.
4. Rechecking a producer's checkout before a consumer's first claim moves to E3a.5's check-inputs-and-claim operation. E3a.2 makes the drift visible at the producer's own next move, and a repeated `Publish` no longer resolves a block recorded after acceptance.
5. A drift block is an unresolved `DirtyWorktree` or `UncertainOwnership` block of the publishing attempt. `Publish` returns it unchanged and never resolves it, even when the live bytes again equal the capture. Only a `BlockResolved` that a person's recheck or E3's repair path records clears it. A block identical to a resolved one is recorded again as a new block.
6. A capture judges shared refs without the repository lock. It reads the journal, snapshots the refs, and reads the journal again. Only when both reads agree does it judge the refs, and it judges them against that journal, never an earlier state, so a rewind to an obsolete value or a lease that ended during the capture cannot explain a ref. Only receipts that ref ownership reads make the reads disagree: ref intents and observations, preparations, claims, root observations, turn and attempt closures, fences, and salvage retention. Captures, dispositions, and blocks do not, so sibling captures in a fan-out cause no retry. Unstable tries repeat with jittered backoff for up to one second. Exhaustion returns `JournalBusy` without recording an observation, a disposition, or a block, and the same operation can retry. When the first capture was already recorded, the retry meets the partial-capture state that E3a.3's recovery handles.
7. Root tips and capture candidates are pinned under `refs/idp/<run>/pin/` before their events are recorded, so reflog expiry and `gc` keep them. Pins carry no ownership, so they stay out of shared-ref snapshots and need no journaled intents. A refused root observation never deletes its pin, because another observer may have recorded the same tip in between. A concurrent observation that matches the recorded one in exit, tip, HEAD, and ownership returns the recorded event; a mismatching one fences the launch. A later observation of an already recorded launch returns the recorded event when its exit matches and fences the launch when it differs. It does not compare the tip, because publication moves the branch after the root exit. A `JournalBusy` refusal with nothing recorded returns `JournalBusy` without a fence. `ReleasePins` removes every pin, including one left by a refused observation, by compare-and-swap once the run is settled, or once an abandoned run has every attempt closed, every block resolved, and every salvage retained. E3b's coordinator calls it.

### Workflow cleanup uses a process group on Unix

On Linux and macOS, a native launcher starts the client with `posix_spawn` and `POSIX_SPAWN_SETPGROUP`, because .NET's `Process.Start` cannot set a process group. Windows keeps its Job Object after root exit. Cleanup gives a two-second grace before it forces termination, and records each signal and failure. The coordinator signals only a group it launched in this session, and never a group ID read from a journal after a restart. Standalone runs keep their current Cancel, Stop and send, and leave behavior.

### Recovery never replays a launch

Reopening reads the journals and launches nothing. **Resume run** allows scheduling after reconciliation.

| Evidence after a crash | Recovery |
| --- | --- |
| A claim without strict terminal evidence | `Uncertain`, and the launch is never replayed. |
| A strict checkpoint and two matching captures | Finish closure or publication once, after live checks. |
| A strict checkpoint and only the first capture | Take one recovery capture with the frozen recipe, and accept only a full match. |
| No first capture | Block with "Missing turn-end capture evidence." |
| No root-exit observation | Ownership stays unresolved. |
| An interrupted review fix | Reconcile, then offer **Continue fix** or **Retry fix**. |

**Continue fix** takes two matching recovery captures as the new attempt's starting baseline and needs a compatible stored session. These captures never stand in for the interrupted turn's missing success. Without a compatible session, only **Retry fix** is offered.

### Stop, retry, gates, and amendments

**Stop Workflow** records `StopRequested` before it blocks new claims. A claim that wins first must settle or reconcile. Manual Retry creates one fresh-session attempt with `AttemptCause.Retry`, and nothing retries by itself. An Approval node creates a durable request and no client attempt, and approval records a human-origin accepted result. Planner proposals amend a running workflow through `AmendFromProposal` against the expected previous revision, and a reserved task's execution definition and incoming edges cannot change. An accepted upstream fix makes its consumers stale. **Review updated inputs** builds a clean rebase candidate for a person to approve.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| Ship workflow execution on Windows first, and refuse it on Linux and macOS until containment exists | The user chose execution on all three platforms, with containment for cleanup only. |
| Withhold workflow execution until every platform has verified containment | It delays every platform and needs a cgroup design on Linux and a separate design on macOS. |
| Keep a workflow turn's descendants and wait for its process scope to empty | Publication and continuation would stay blocked while a development server runs. |
| Continue an interrupted fix by itself once recovery proves it safe | The person would get unexpected work after reopening. |
| Sample captures until two match | A changing worktree could match by chance. |
| Launch callbacks between node controls, shared run branches, or a task's standalone `Latest` as run completion | The plan sets one coordinator over durable records. Shared branches and `Latest` lose run ownership. |
| Infer success from an exit code or a commit trailer | Only strict log evidence and matching captures accept a result. |

## Delivery

Each part is one pull request with a checkable completion condition. E3a's parts land before E3b, and E3b changes no E3a code, schema, or contract.

| Part | Scope |
| --- | --- |
| E3a.1 | Coordinator permit, transferable task lease, lock order, preparation at the original revision, and fencing after a lost owner. |
| E3a.2 | Root observations, schema 3 capture records, the 250 ms capture, the guarded ownership fold, frozen publication, and the N2 cases. |
| E3a.3 | Recovery from a partial capture, recovery baselines, salvage and retry changes, and **Preserve and restore**. |
| E3a.4 | The Unix process-group launcher and the Windows cleanup policy for workflow turns. |
| E3a.5 | Single-turn runner, explicit checkout, root-exit notification, settled handles, and outcomes, exercised with fake clients and no scheduler. |
| E3b | Headless coordinator, deterministic dispatch, slot accounting, Stop, and recovery. Chain and diamond tests consume E3a unchanged. |
| E3c.1 | Run-owned conversation routing and history. |
| E3c.2 | Review progression, artifact forwarding, and explicit choices for an interrupted fix. |
| E3d.1 | Preflight and durable snapshot approval. |
| E3d.2 | Planner inclusion and proposal amendments. |
| E3e | Human gates and forwarded results. |
| E3f | Stale-input preview and approved clean rebase. |
| E3g.1 | Run toolbar, preflight, status, conversation and gate navigation, and activity of retained projects. |
| E3g.2 | Recovery, restoration, and stale-input panels, with real-window restart and Stop coverage. |

E3a needs the settled E2 and C1 contracts. E3b also needs E2b's isolation and join gate, which landed in [#44](https://github.com/Mano-Liaoyan/iDevelop/pull/44). A join needs Git 2.43 or later. Other workflow execution needs Git 2.39 or later.

The E2 record's [open issue 1](2026-10-07-dependency-results.md#open-issues) lists three cases, called N2 in the design trail, that become E3a.2 acceptance tests. A foreign commit after root exit must not become the published result's parent. A rewrite to another descendant must not replace the writer's commit. A rewind must not be adopted through salvage, excuse a sibling's publication, or permit a retry reset. The design note adds behavior tests with literal results, including a double-capture mismatch, a write after capture that blocks the next move, and descendant stop in workflow turns against standalone turns.

## Changed artifacts

E3 has changed no application file. The design notes and reviews stay in the coordinator's local trail, with a copy in `.git/execution-evidence/trail`.

## Commands and observed results

| Check | Observed result |
| --- | --- |
| Opus review of version 1 | Fit after fixes, with two findings that needed the user's decisions above. Astra accepted all ten findings in version 2. |
| Opus review of version 3 | Not fit as written, and fit after its first three findings with no new user decision. Version 4 accepted eight findings and one in part. The review claimed that no PID is kept, but `AttemptEvent.Launched` already records the root PID and start time. |
| Build and tests | None ran. The note read the source of an integration checkout and of E2a's branch. |

## Open issues

1. Cleanup can interrupt a write and leave a stable partial file that both captures accept.
2. A successor can consume that partial result before any later change exists to detect.
3. An escaped process can write during a later turn or into a sibling worktree, and captures cannot tell who wrote a stable change.
4. A write between a live check and a Git operation remains possible.
5. Unix roots and descendants can survive a crash. Windows closes its kill-on-close job when the app exits, but the job covers only processes assigned to it.
6. The root-exit observation follows exit detection, so a foreign commit that lands before it cannot be told apart from the writer's.
7. Shared run-ref or stash changes can block publication of an unrelated task.
8. Concurrency above one client root per run needs a probe the user authorizes. New real-client checks also need the user's approval.

## Next action

A fresh review confirms version 4, and then E3a.1 starts.
