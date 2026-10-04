# Single-task execution

## Task

Build phase 3 of the [delivery order](../product-direction.md#delivery-order). Each task node gets an agent, a model, and a reasoning setting, and runs on its own with Claude Code, Codex, Pi, or Antigravity CLI. On 2026-10-04 the user accepted every proposal the previous agent had left open and asked for the best option on each open question, then an implementation, a merged pull request, and a summary at the end without stopping in between. The coordinator settled the open questions, which this record lists under Decisions.

## Checklist

The figure-it-out playbook drove the run, because the work was a new subsystem and the user stepped away.

- [x] Read the Principles section of poteto-mode.
- [x] Phase A, frame. The predicate: on Windows, the Release app runs one task each through the four clients and records the agent's result, a configuration survives save and reopen, the sidebar shows each client's readiness with a reason, a blocked start shows its reason, cancel records Cancelled, and an app that dies mid-run reopens with the run Interrupted. Rigor was high, because runs start processes that edit the user's files and the workflow file format changes.
- [x] Phase B, design. Two fresh Opus 5.5 runners at xhigh designed from opposing seeds, and a fresh cross-judge scored them. See Decisions.
- [x] Phase C, the loop. Two sequential implementation owners, Core then Desktop, and two fix rounds. Each was a fresh Opus 5.5 session at xhigh in accept-edits mode with an explicit command allowlist, in one worktree and branch. The coordinator ran the real clients through the engine between the two owners, and through the Release window after each round.
- [x] Phase D, the decision trail. A local log recorded each decision. This record summarizes it.
- [x] Phase E, verify and ship. See Commands and observed results.

## Decisions

The user's answers on 2026-10-04:

- All four proposed acceptance cases are kept: client readiness, a refused start that names its reason, cancel, and interrupted runs.
- Team synchronization stays a first-release requirement.
- The agent that iDevelop runs itself against model APIs stays under later work.

The coordinator settled the phase's open design questions:

- **How the app drives each client.** Each client runs in its own documented non-interactive mode with a JSON event stream, and the prompt goes over stdin. Claude Code runs `claude -p --output-format stream-json`, Codex runs `codex exec --json`, Pi runs `pi -p --mode json`, and Antigravity CLI runs with `--input-format stream-json --output-format stream-json`. ACP was not used, because Claude Code and Codex need separately installed adapters for it.
- **Project folder or worktree.** A run edits the project folder itself. Worktrees and branches wait for phase 4, when several tasks run at once and their results need merging.
- **Overlapping runs.** Phase 3 first shipped with one run per project folder. After the merge the user asked for parallel runs, so several tasks of a folder can run at once, and each task runs at most once at a time, across every app instance. An operating-system file lock on `.idp/attempts/<task-id>/run.lock` enforces it, and the operating system releases the lock when its holder dies.
- **Settings and the file format.** The workflow file moves to `idevelop.workflow/2`, which stores an `execution` object (client, model, reasoning) on every task. Version 1 files still open. No per-user client settings exist. The app finds clients on PATH, and on macOS and Linux also on the login shell's PATH.
- **The run record.** Each attempt is an append-only event log at `.idp/attempts/<task>/<attempt>/events.jsonl`, with the client's raw output beside it. One pure reducer folds the log into the record. Git ignores the folder.
- **Unverified provider items.** Each client's own catalog and sign-in commands decide readiness: `codex debug models`, Pi's RPC model list with `pi auth check` per provider, and `agy models`. Claude Code has no list command, so iDevelop carries its model list. Permissions follow each client's non-interactive rules: Claude Code accepts edits and denies unapproved commands, Codex sandboxes commands in the workspace, Antigravity CLI accepts edits and blocks commands, and Pi has no permission system, which the inspector says.
- **Leaving during a run.** Closing the window or opening another folder while a task runs asks first. Stopping records the attempt as Interrupted.
- **What a run leaves behind.** A run that ends on its own leaves running what the client started on purpose, such as a dev server or a browser it opened for the user, on every platform. Cancel, leaving the project, and an iDevelop crash stop the client's whole tree on Windows, where each client runs in a Job Object. The second review showed that closing the job at every settle would end a browser the agent had opened, with all the user's tabs.

The design arena:

- Runner A modeled the clients as rows of pure functions run by one supervisor, with an append-only attempt log. Runner B modeled each client as a class behind an `IAgentClient` interface, with an `attempt.json` snapshot rewritten on each change. Both reached the same answers on run ownership (an OS file lock), crash recovery (a process id and start time), the test seam (a fake client program), and Pi's per-provider readiness.
- The cross-judge scored A 24 and B 22. B had two lifecycle defects. A start could launch after the project closed, and rewriting `attempt.json` fails on Windows while another window reads it. A is the base.

These parts came from B:

- Fake clients installed as real shims on an injected search path, so the tests go through PATH, PATHEXT, and `cmd.exe`.
- Process identity as Gone, Same, or Reused, with a one-second tolerance.
- An allow-list for the arguments passed to a batch shim, checked before launch.
- Pi reported as not ready when no provider is signed in.
- The Stop-and-leave prompt.

These parts of B were rejected:

- One class per client, because the exit-code policy would split across four interpreters.
- The `attempt.json` snapshot that each change rewrites.
- Thread confinement through `SynchronizationContext`.
- A `--version` probe per client.
- An automatic re-probe on Run, which caused the launch after the project closed.

## Review triage, round 1

A fresh Opus 5.5 session at xhigh reviewed all 16 commits read-only. The coordinator's run of the real Release window against the real clients found two more defects before the review finished. Every item was fixed in fix round 1, each with a test that failed first.

| Finding | Severity | Decision |
| --- | --- | --- |
| Choosing another task in the inspector reset its model and level to the client's defaults (found in the real window) | High | Fix. The pickers' two-way binding wrote the previous task's selection into the next task |
| Stopping a Pi run left Git Bash's inner `bash.exe` and `sleep.exe` running (found in the real window) | High | Fix with a Windows Job Object per child |
| A project folder could plant `node` for an npm `.cmd` shim, which `cmd.exe` finds in the current folder before PATH | High | Fix. Every child gets `NoDefaultCurrentDirectoryInExePath=1` on Windows |
| On macOS and Linux a client found on the login shell's PATH started without that PATH | High | Fix. Every child gets the search path that found it |
| A `.cmd` client in a project on a UNC path runs in `C:\Windows` | Medium | Fix with a refusal that names the reason |
| A second window showed another window's run as running forever, with a Cancel that did nothing | Medium | Fix. It reads "Running in another window", and Cancel is only for this window's run |
| No test checked the folder a run or a probe works in | Medium | Fix with a fake-agent step and two tests |
| Open folder could start again while an earlier leave waited | Low | Fix |
| A process the client left running survived the end of the run | Low | Fixed on Windows through the job, then reversed in round 3. See What a run leaves behind under Decisions |
| Reconcile could block the UI thread for 5 seconds | Low | Fix |
| A stored model read "not offered" while its client was still being checked | Low | Fix |
| No test covered the cross-process lock, the leave timeout, or a probe timeout | Low | Fix with three tests |
| `ActivityKind` was recorded and never read | Low | Removed |

The round 1 owner left out `JOB_OBJECT_LIMIT_BREAKAWAY_OK`, which the brief had asked for, because Git Bash used it to start commands outside the job. The coordinator checked the risk that a client's own sandbox needs to leave the job: Codex ran its sandboxed PowerShell command inside the job in the real window, and so did the other three clients.

The coordinator's next real-window run found that a task switch still marked the document as changed. The previous task's level was applied and then the right level again, so two edits cancelled out. Fix round 2 made a picker edit the task only while its list is open or it has keyboard focus, which is the sidebar's existing rule, and added a test that failed first.

## Review triage, round 2

A fresh Opus 5.5 session at xhigh reviewed both fix rounds read-only. It confirmed that every round 1 and round 2 item was fixed at its root, with a test that fails on the old code, and it found nothing High. Fix round 3 made each change below with a test that failed first.

| Finding | Severity | Decision |
| --- | --- | --- |
| Closing the job at a normal settle ends programs the agent started for the user, such as a browser | Medium | Fix. The coordinator decided what a run leaves behind, recorded under Decisions |
| On macOS and Linux the app's minimal PATH came before the login shell's, so the agent got the system `python3` | Medium | Fix with a pure merge function |
| A UI Automation select that keeps focus on a picker could still carry an entry to another task | Low | Fix. Entries of different tasks are unequal |
| A refused start in a second window discarded its fresh read, so its cards stayed stale | Low | Fix |
| `NoDefaultCurrentDirectoryInExePath` reaches the agent's own `cmd.exe` steps | Low | Accept. A planted command is worse than a bare batch name that needs `.\` |
| Relative PATH entries reached the child | Low | Fix |
| A leave that gave up read as a crash after reconcile | Low | Fix. The detail leads with the leave |
| The working-folder test helper resolved Windows junctions | Low | Fix |

The review's gap that no real client had run inside the job was already closed. The coordinator's real-window run after round 1 ran all four real clients in jobs, including a sandboxed Codex command and Pi's Git Bash, with Cancel and Stop and leave.

## Changed artifacts

- `src/IDevelop.Core/Workflows/Workflow.cs` adds `ClientId`, `ExecutionSettings`, `TaskDefinition.Execution`, and `WorkflowEdit.SetExecution`. `Projects/WorkflowDocument.cs` reads formats 1 and 2 and writes 2. `Projects/DataFolder.cs` owns `.idp/.gitignore`.
- `src/IDevelop.Core/Execution/` holds the engine. `ClientRegistry.cs` and `Clients/` hold the four client rows. `CommandResolver.cs` finds clients, `ClientDirectory.cs` probes them, `ChildProcess.cs` and `ProcessJob.cs` start and stop processes, `Attempts.cs` and `AttemptLog.cs` hold the record and its log, `StartCheck.cs` decides whether a task can start, and `ProjectRuns.cs` runs one task per folder and settles interrupted runs.
- `src/IDevelop.Desktop` adds the AGENTS section, the inspector's Agent and Run sections, the card's agent label and status pill, the run bar, and the Stop-and-leave dialog. `scripts/planweave-tokens.mjs` adds PlanWeave's running, success, and failed state colors.
- `tests/IDevelop.FakeAgent` is a stand-in client. `tests/Shared` installs it as real shims on a search path for both test projects. `tests/IDevelop.Core.Tests/Fixtures` holds trimmed recordings of the real clients.
- `samples/storage-change` moved to format 2. `scripts/check-real-window.ps1` checks the AGENTS section, the pickers, a run through a fake Codex, and that choosing tasks leaves the project unchanged.
- `README.md`, `AGENTS.md`, `docs/context.md`, `docs/product-direction.md`, and `docs/provider-access.md` describe the phase. This record holds its design.

## Commands and observed results

| Command | Result |
| --- | --- |
| A scratch harness that drives `ProjectRuns` against the real clients | Claude Code, Codex, and Antigravity CLI each ran a task to Succeeded and wrote the requested file. Pi was refused because both of its sign-in checks timed out. |
| Two 20-line programs that start Pi's RPC mode, then run `pi auth check` | Killing the RPC process after its answer made the check take 31,457 ms. Closing its stdin made it take 1,547 ms. The fix closes stdin first. |
| The same harness after that fix | Pi was ready through `pi.CMD`. Its DeepSeek run succeeded, and Pi reported the level it used. Its ChatGPT models were refused with the sign-in reason, and nothing started. |
| `pwsh -NoProfile -File scripts/check-real-window.ps1 -Exe <Release exe> -OutDir <folder>` | Every check passed, on the Desktop owner's build and after each fix round. |
| A scratch UI Automation script against the Release window and the real clients, run after each round | The first run found the two real-window defects above, and the second found the document marked changed. The final run on the round 3 build passed all 34 checks, and 8 seconds after each normal finish no client had left a process running. In one earlier run DeepSeek declined to run a bare `sleep 300` and finished at once, so the script now words that task as a build step. All four clients ran a task to Succeeded with the requested settings. A signed-out Pi model was refused with no attempt. Cancel stopped Codex's tree of 10 processes. Stop and leave stopped Pi's Git Bash tree, and the reopened project read Interrupted. Killing iDevelop stopped the client through its job, and the reopened project read Interrupted. No real-window run saved an agent chosen in the window and reopened it. Headless tests cover that round trip through the real main window, and the real-window script reopens a sample whose tasks already carry agents. In the real window every client was ready, and Pi showed its signed-out provider with the reason. Headless tests cover a missing and a signed-out client. |
| `dotnet build -c Release` and `dotnet test -c Release` | 0 warnings. Core 142 passed and 2 skipped (Unix only), Desktop 98 passed, three runs in a row. The Core owner's 7 commits and the fix rounds' 22 commits were each built and tested on their own. The Desktop owner's 9 were not, and the pull request merges as one squashed commit. |
| `node scripts/check-licenses.mjs`, `node scripts/planweave-tokens.mjs --check`, `node scripts/check-handoffs.mjs` | Exit 0. No new package. |
| GitHub Actions run 37189918524 on the pushed branch | Restore, license, token, and handoff checks, build, and tests passed on Linux, Windows, and macOS. Linux and macOS ran Core 138 tests with the 6 Windows-only ones skipped, so the two Unix-only tests ran there for the first time. Windows ran Core 142 with the 2 Unix-only ones skipped. Desktop passed 98 everywhere. |
| A fresh Opus 5.5 verifier on pull request 6 at head `875f563`, patch-id `4a1a9130` | PASS+NOTES, posted on the pull request. It reviewed fix round 3, rebuilt, reran both suites, passed all 65 real-window checks, and passed all 34 real-client checks. Two `cmd.exe` processes it saw after the Pi run belonged to the user's VS Code extension host. |

## Open issues

- On macOS and Linux, Cancel and leaving stop the client's tree through `Process.Kill(entireProcessTree: true)`, which misses a descendant whose parent already exited, because .NET cannot list the children of an exited process there. A process group per client could cover it later.
- No real client has run on macOS or Linux, and the login shell's PATH has never been read from a real shell. CI runs the fake client and the PATH merge tests on both.
- A crash between `Process.Start` and the `Launched` record leaves a client that reconcile cannot identify. On Windows the client's job stops it when iDevelop dies, so this gap is now Linux and macOS only.
- A Pi readiness check timed out once during fix round 2 and could not be reproduced in four later tries. The refresh button beside AGENTS checks again.
- A run that ends on its own does not show what it left running. When a process the client started keeps the client's output open, the attempt settles 5 seconds after the client exits and drops the output after that.
- A process that a run leaves running and that keeps the client's output open loses that pipe when iDevelop exits, and a Node server can then fail on its next write.
- After Cancel or leaving, the run still clears kill-on-close before it closes its job. The job is already terminated then, so nothing survived in any run, but that order removes a backstop. Clearing kill-on-close only for a run that ended on its own would match the decision.
- On Windows the child's PATH drops an entry that is not fully qualified, which includes a quoted entry such as `"C:\Program Files\Tool"` that `cmd.exe` still searches.
- On Windows, a tool that asks to break away from its job fails with "Access is denied" inside a run, because the job forbids breakaway so that Git Bash cannot escape it. No client asked in the real runs, but Claude Code and Antigravity CLI ran no shell command there.
- `NoDefaultCurrentDirectoryInExePath` reaches the agent's own commands, so a `cmd.exe` step that calls a bare `build.cmd` from the project folder fails under a run.
- The real-client check and its harness are scratch scripts kept outside the repository. Its cancel and leave cases depend on the model agreeing to run a long command, and DeepSeek once declined. A committed real-client check would make workflow execution easier to verify.
- Whether `agy models` fails for a signed-out account is unverified, and iDevelop uses it as Antigravity CLI's readiness signal.
- Whether each provider's plan permits unattended runs started by iDevelop is each provider's policy, as the [provider access reference](../provider-access.md) records.
- Parallel runs share the project folder, so two agents can edit the same file at once. Workflow execution needs worktrees and branches to keep them apart.

## Next action

Workflow execution, phase 4 of the [delivery order](../product-direction.md#delivery-order).
