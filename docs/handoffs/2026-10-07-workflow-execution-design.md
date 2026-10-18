# Workflow execution design

## Task

E3 is the slice "Execute the workflow" of the [approved execution plan](2026-10-06-workspace-execution-plan.md#approved-delivery-slices). Its completion condition is that Run Workflow finds roots, launches eligible work, waits at questions and approvals, starts successors once, and stops safely. Review advancement and scheduling share one launch authority. Failure affects only dependent branches.

This record holds E3's design. No E3 code exists. GPT-6 Astra wrote the design note in four versions in the judgment role. Fresh Opus sessions reviewed version 1 and version 3. The note builds on the [E1 run journal](2026-10-07-run-and-input-records.md) and the [conversation backend](2026-10-07-conversation.md). Implementation now waits only for a review that confirms version 4.

## Decisions and reasons

### The user settled three questions on 2026-10-07

The Opus review of version 1 found two places where the note reversed or extended recorded behavior. The user decided both by accepting the coordinator's recommendations.

1. Result acceptance does not depend on proof that a writer's processes stopped. Process containment serves cleanup only, with the existing Windows Job Object and a process group on Linux and macOS. All three platforms get workflow execution in E3, and no platform waits for containment.
2. Workflow turns stop the processes their agent started when the turn ends. Standalone single-task runs keep the recorded rule that what the agent started on purpose keeps running. On Linux and macOS the stop is best effort. A process that leaves its group escapes, and correctness does not rely on the stop.
3. A review fix interrupted by closing the app does not resume by itself. On reopen the person chooses **Continue fix** or **Retry fix**.

### One coordinator decides from durable records

`WorkflowRunCoordinator` lives in Core, hosted by the open project's `ProjectRuns`, with no Avalonia dependency. Run journals and run-owned attempt logs are the authority. Readiness, staleness, waiting, and blockers are projections of them. A serialized command queue orders decisions, and slow client work runs outside that queue. Commands carry repository, workflow, and run identity, and selection never supplies it.

An operating-system coordinator lease and an opaque `CoordinatorPermit` give one window control of a run. A second window can read history, but every command returns `Unavailable` with "This run is controlled by another iDevelop window." A free task lock does not let a run-owned task run standalone.

Initial turns, replies, queued follow-ups, reviewer turns, and fixes all go through the same launch authority. `ProjectRuns.Follow` and `Finish` no longer advance run-owned reviews on their own. Bring-up allows one starting or running client root per run. Waiting, gates, and cleanup use no client slot.

### Settlement closes a turn after root exit

Settlement follows this order.

1. On root exit, free the client slot and request bounded cleanup of descendants.
2. Drain output within a bound, flush the log, freeze its checkpoint, and record `CloseTurn`.
3. Release the task lease only against a durable outcome.

### Workflow cleanup uses a process group on Unix

On Linux and macOS, a native launcher starts the client with `posix_spawn` and `POSIX_SPAWN_SETPGROUP`, because .NET's `Process.Start` cannot set a process group. Windows keeps its Job Object after root exit. Cleanup gives a two-second grace before it forces termination, and records each signal and failure. The coordinator signals only a group it launched in this session, and never a group ID read from a journal after a restart. Standalone runs keep their current Cancel, Stop and send, and leave behavior.

### Recovery never replays a launch

Reopening reads the journals and launches nothing. **Resume run** allows scheduling after reconciliation.

| Evidence after a crash | Recovery |
| --- | --- |
| A claim without strict terminal evidence | `Uncertain`, and the launch is never replayed. |
| A strict checkpoint | Finish closure once. |
| An interrupted review fix | Reconcile, then offer **Continue fix** or **Retry fix**. |

**Continue fix** needs a compatible stored session. Without a compatible session, only **Retry fix** is offered.

### Stop, retry, gates, and amendments

**Stop Workflow** records `StopRequested` before it blocks new claims. A claim that wins first must settle or reconcile. Manual Retry creates one fresh-session attempt with `AttemptCause.Retry`, and nothing retries by itself. An Approval node creates a durable request and no client attempt, and approval records a human-origin accepted result. Planner proposals amend a running workflow through `AmendFromProposal` against the expected previous revision, and a reserved task's execution definition and incoming edges cannot change. An accepted upstream fix makes its consumers stale.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| Ship workflow execution on Windows first, and refuse it on Linux and macOS until containment exists | The user chose execution on all three platforms, with containment for cleanup only. |
| Withhold workflow execution until every platform has verified containment | It delays every platform and needs a cgroup design on Linux and a separate design on macOS. |
| Keep a workflow turn's descendants and wait for its process scope to empty | Continuation would stay blocked while a development server runs. |
| Continue an interrupted fix by itself once recovery proves it safe | The person would get unexpected work after reopening. |
| Launch callbacks between node controls, or a task's standalone `Latest` as run completion | The plan sets one coordinator over durable records. `Latest` loses run ownership. |
| Infer success from an exit code | Only strict log evidence accepts a result. |

## Delivery

Each part is one pull request with a checkable completion condition. E3a's parts land before E3b, and E3b changes no E3a code, schema, or contract.

| Part | Scope |
| --- | --- |
| E3a.1 | Coordinator permit, transferable task lease, lock order, preparation at the original revision, and fencing after a lost owner. |
| E3a.4 | The Unix process-group launcher and the Windows cleanup policy for workflow turns. |
| E3a.5 | Single-turn runner, root-exit notification, settled handles, and outcomes, exercised with fake clients and no scheduler. |
| E3b | Headless coordinator, deterministic dispatch, slot accounting, Stop, and recovery. Chain and diamond tests consume E3a unchanged. |
| E3c.1 | Run-owned conversation routing and history. |
| E3c.2 | Review progression, artifact forwarding, and explicit choices for an interrupted fix. |
| E3d.1 | Preflight and durable approval. |
| E3d.2 | Planner inclusion and proposal amendments. |
| E3e | Human gates and forwarded results. |
| E3f | Stale-input preview. |
| E3g.1 | Run toolbar, preflight, status, conversation and gate navigation, and activity of retained projects. |
| E3g.2 | Recovery and stale-input panels, with real-window restart and Stop coverage. |

E3a needs the settled C1 contract.

The design note adds behavior tests with literal results, including descendant stop in workflow turns against standalone turns.

## Changed artifacts

E3 has changed no application file. The design notes and reviews stay in the coordinator's local trail, with a copy in `.git/execution-evidence/trail`.

## Commands and observed results

| Check | Observed result |
| --- | --- |
| Opus review of version 1 | Fit after fixes, with two findings that needed the user's decisions above. Astra accepted all ten findings in version 2. |
| Opus review of version 3 | Not fit as written, and fit after its first three findings with no new user decision. Version 4 accepted eight findings and one in part. The review claimed that no PID is kept, but `AttemptEvent.Launched` already records the root PID and start time. |
| Build and tests | None ran. The note read the source. |

## Open issues

1. Unix roots and descendants can survive a crash. Windows closes its kill-on-close job when the app exits, but the job covers only processes assigned to it.
2. Concurrency above one client root per run needs a probe the user authorizes. New real-client checks also need the user's approval.

## Next action

A fresh review confirms version 4, and then E3a.1 starts.
