# Run and input records

## Task

E1 is the slice "Define run and input records" of the [approved execution plan](2026-10-06-workspace-execution-plan.md#approved-delivery-slices). Its completion condition is that an approved workflow revision owns its attempts and their explicit input provenance. Replay and duplicate-start tests must pass. A standalone result enters a run only through explicit, validated reuse.

Pull request [#39](https://github.com/Mano-Liaoyan/iDevelop/pull/39) adds the run journal to `IDevelop.Core`. It adds no UI, no scheduler, and no launch path. E3 is its first caller.

GPT-6.1 Sol wrote the code through Codex. GPT-6 Astra reviewed the design note before implementation. An Opus backend review and an Astra difficult-task review of head `106b243` made eleven findings. Both reviews reported the stale-context defect, so ten were distinct. The fix round closed each defect with a test that failed before the fix. It added the concurrency tests that the Opus review asked for and deleted two unused members.

## Decisions and reasons

### Each run keeps its own append-only journal

A run lives at `.idp/runs/<workflow-id>/<run-id>/events.jsonl`. Its run-owned attempt logs live under the same run folder. Standalone attempts stay under `.idp/attempts/`. `ProjectRuns.Open` reconciles that standalone tree, so a run-owned log there could change under the run. `DataFolder.EnsureGitIgnore` adds `runs/`.

`RunReducer` folds the events into a `RunRecord` with no IO, clock, or ID generation. `RunStore` takes `.idp/runs/write.lock`, rereads the journals, validates the command, appends one event, and flushes to disk before it answers. Current results and stale results are derived from the history. The journal never stores them.

### A revision hash covers execution fields only

`RevisionId` is the SHA-256 of a canonical JSON whitelist: workflow ID, task IDs, titles, blueprint keys, work behavior, fields, execution settings, conversation modes, and connections. Moving a card or editing a blueprint description keeps the hash. Changing a model changes it. A whitelist keeps future presentation properties out of the hash until someone adds them on purpose.

### Reservation, claim, closure, and result are separate facts

Each task has one permanent initial slot per run, and each previous attempt has at most one retry or continuation. A repeated start returns the existing attempt. Only the first durable claim of a turn returns `Granted`. Every later claim returns `Existing`, so a lost acknowledgement never grants twice. One operation ID maps to one command. The same ID with different content returns `OperationConflict`.

### Only strict log evidence or a person ends an uncertain launch

An unclosed claim is `Uncertain`. A free lock, a missing or reused PID, and an `AttemptEvent.Reconciled` line are not evidence that the process stopped. `CloseTurn` and attempt closure reject a log that contains `Reconciled`. Otherwise a reconciled turn could release a claim, and after abandonment another run could launch the same task. A person can record `AttemptEnd.Recovered` with a confirmation ID and a reason while the store holds the task lock.

Abandonment closes a run but keeps its unresolved claims. They block the same task in every other run of the same workflow until recovery resolves them.

### Context never blocks a reservation

A dependency input must name a current, non-stale accepted result. Otherwise the reservation returns `MissingDependencyResult` or `StaleInput`. A context input names a current result or records `MissingContext`, including when its source result is stale. The review found that stale context returned `StaleInput`, which contradicted the plan.

### Results are accepted only for read-only agent tasks in E1

An executed result needs a logged successful closure and a matching report and input record. Writer output, review code handoff, artifacts, and human approval results return `UnsupportedResult` until E2 and E3 add their evidence.

### Report reuse is narrow and checks every identity

`ProjectRuns` records a `StandaloneCapture` on each fresh standalone attempt. The attempt log stores it as raw JSON, and only `ReportReuse` parses it. A capture that fails to parse makes reuse unverifiable but leaves the attempt in history.

`RunStore.ReuseReport` copies a report only from a fresh, single-turn, read-only agent attempt that succeeded with no handoff. The log's requested attempt and task must match the source the caller names. Its task definition, settings, and prompt must match the receiving task. The source's start and end trees must match the run's approved base outside `.idp`. The base comes from the run record, not from the caller. Equal trees prove equal content, not commit lineage, so E1 reuses reports only, never code.

### A command reads only its own workflow's journals

A command reads only `.idp/runs/<workflow-id>/`. Only runs of a task's own workflow can claim that task, as long as each task ID belongs to one workflow of the project. On `main`, `WorkflowDocument.Save` refuses to write a second workflow file, so a project has one workflow and the condition holds. The one-active-run rule also applies per workflow. The lock stays project-wide.

A journal with no complete entry counts as absent. That covers a zero-byte file and a torn first line, in the command's own run and in other runs. A crash during the first write therefore no longer blocks the retried approval. Under the lock, the store replaces the torn bytes with the new first entry, so the retry returns `Created` and a second retry returns `Existing`. Another run's journal with a torn last line contributes its valid prefix, because the torn entry was never acknowledged. Every other kind of damage blocks mutation, and a torn tail after a complete entry still blocks its own run. The journal decodes line by line, so a line cut inside a multibyte character reads as a torn tail. The store never changes a journal that holds a complete entry.

### Rejected alternatives

| Alternative | Reason for rejection |
| --- | --- |
| Use each task's latest attempt | It loses run ownership and can import a standalone success. |
| Store run-owned attempts in the standalone tree | Project-open reconciliation can alter them. |
| Infer a stopped process from a lock, a PID, or `Reconciled` | None of them closes the gap between claim and process start. |
| Treat tree equality as commit lineage | Equal content does not prove ancestry or ownership. |
| Let the caller choose the reuse base | A base other than the run's own approved base gives false provenance. |
| Read every run journal in the project for each command | One damaged journal blocked unrelated workflows. |
| Persist status mirrors and stale flags | They duplicate evidence that the history already holds. |

## Changed artifacts

- `src/IDevelop.Core/Execution/Runs/` holds the records, revision hash, reducer, journal codec, store, and report reuse.
- `src/IDevelop.Core/Execution/Attempts.cs`, `AttemptLog.cs`, and `ProjectRuns.cs` add the optional `standaloneCapture` and `runBinding` fields to the `requested` attempt event.
- `src/IDevelop.Core/Execution/GitTree.cs` reads a tree's content outside `.idp`.
- `src/IDevelop.Core/Projects/DataFolder.cs` adds `runs/` to the project data folder's ignore file.
- `tests/IDevelop.Core.Tests/Runs/` tests every row of the design note's behavior table and every review finding.

## Commands and observed results

| Command or check | Observed result |
| --- | --- |
| `dotnet build -c Release` | 0 warnings and 0 errors. |
| `dotnet test -c Release` on Linux | Core passed 439 tests with 9 platform skips. Desktop passed 321. The baseline on `main` was Core 346. |
| `Own_torn_first_line_allows_approval_and_idempotent_retry` on `c6b247e` and after the fix | Before the fix, the approval returned `Rejected` with `IncompleteTail`. After it, the approval returns `Created`, and the retry returns `Existing`. |
| The new journal, recovery, and concurrency tests that compile against `106b243` | Nine of twelve failed. The two concurrency tests and the foreign-lock test passed, because the lock was already correct. With the lock opened without `FileShare.None`, the claim race gave 16 `Granted`, and the reservation race gave 13 `Created` and 3 rejections. Sol observed each reuse, context, and reconciliation test fail before its fix. |
| The recovery inspection and run-base tests on `106b243` | They cannot compile against the old API. Before the fix, Sol showed `InspectRecovery` returning an empty list for a journal with a sequence gap, and `ReuseReport` returning `Created` for a source matching `first` in a run based at `later`. |
| The concurrency tests, repeated | 20 runs inside the Codex sandbox and 10 runs outside it gave the same result each time. |
| Each fix-round commit checked out alone | Core tests pass at each of the six commits. |
| `node scripts/check-licenses.mjs`, `node scripts/planweave-tokens.mjs --check`, `node scripts/fluent-icons.mjs --check` | Each exits 0. |
| `node scripts/check-handoffs.mjs` | It reports eight records, each linked from `docs/context.md` or `docs/product-direction.md`, and exits 0. |
| CI on `2c2d4b5` | All 11 checks pass, including the builds and tests on Linux, Windows, and macOS. |

## Open issues

1. Nothing calls `RunStore` yet. E3 must hold the task lock across claim and launch and route review advancement and standalone launches through the same authority.
2. A torn tail after a complete entry in a run's own journal blocks that run until a person repairs the file. E1 repairs only a journal with no complete entry.
3. Filesystem alias identity is open. The store normalizes the project folder with `Path.GetFullPath`, as `ProjectRuns.Open` does.
4. Old standalone logs without a capture cannot be reused. Broader reuse needs a later evidence contract.
5. The policy of one active run per workflow is a default. The user has not decided whether a workflow may have simultaneous runs.
6. Pull request [#37](https://github.com/Mano-Liaoyan/iDevelop/pull/37) (W1) allows several workflows per project. Whichever of #37 and #39 merges second must confirm that task IDs stay unique across a project's workflows, including duplicated and imported workflows, or restore a project-wide claim check in `RunStore`. At `c1b489e` on `feat/w1-workspace`, `WorkflowDocument.Create` makes an empty workflow with a new workflow ID. The branch has no command that duplicates or imports a whole workflow, and duplicating cards gives each copy a new ID from `TaskId.New()`. `WorkflowDocument.OpenProject` rejects a project whose workflow files share a workflow ID or a task ID. `ProjectRuns.Follow` rejects a workflow that shares a task with another followed workflow. `WorkflowDocument.Save` no longer checks other workflow files, so a workflow file copied in by hand is caught only when the project next opens.

## Next action

E2 builds code-result and artifact materialization on this journal, with explicit versioned event cases for D0's Git contract.
