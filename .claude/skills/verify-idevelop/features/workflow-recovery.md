# Recover a workflow run's tasks

When a workflow run cannot go on by itself, the task that holds it shows what the person can do in the inspector. A task that a block holds, such as a join whose inputs conflict, reads `Blocked`. Its `Recovery` section lists each block and says that Stop Workflow ends the run. A task whose turn's end was never recorded, as after a crash or a kill, reads `Uncertain` and offers `Close as stopped` with a reason. A task built from a result that has since been replaced reads `Inputs changed` and needs attention, and `RunTaskDetail` says why. A review whose fix round a closed or killed app interrupted reads `Fix interrupted` and offers `Continue fix` and `Retry fix`. The card's attention glyph of each of these tasks selects it.

## Sub-features

- `recover-evidence` shows `RecoveryAttempt`, every block in `RecoveryBlocks`, `RecoveryClient`, and `RecoveryTurnEnd` for a blocked or uncertain task, and `RecoveryNoAction` for a blocked one.
- `recover-close` closes an uncertain turn with `RecoveryReason` and `CloseAsStopped`, and warns in `RecoveryStillRuns` while its process still runs. A turn whose client exited but whose settlement failed offers no closure: `RecoveryUnsettled` says what retries it.
- `recover-stopping` keeps a stopped run `Stopping` while a turn is uncertain, says so in `RunActivity`, and settles it once the turn is closed.
- `recover-stale` shows a stale task as `Inputs changed`, with `RunTaskDetail`, and the run as `Needs attention`.
- `recover-fix` offers a run's interrupted review fix round through `ContinueFix` and `RetryFix`, and starts nothing before the person chooses.

## How to get to it (user POV)

- Choose the attention glyph on a card that reads `Blocked`, `Uncertain`, `Inputs changed`, or `Fix interrupted`, or choose the task in the sidebar.
- In the inspector's `Recovery` section, type a reason and choose `Close as stopped`.
- In a review's `Run` section, choose `Continue fix` or `Retry fix`.

## Driving it with real-window.psm1

Preconditions:

- The preconditions of [Run a whole workflow](./workflow-run.md) hold, with a Git repository and a commit, and every task runs through the fake Codex.

- **Close as stopped.** Start a run whose first turn waits for `$s.Gate`, wait for `Running`, and stop the app's process with `Stop-Process -Id $s.Process.Id -Force`. Run `$s = Start-IDevelop -Reopen` to open the same project again. The first card reads `Uncertain` and `RunStatus` reads `Paused`. Choose the first task: `RecoveryClient` names the client's process. Stop that process, type a reason into `RecoveryReason`, and run `Invoke-Element (Find-ById $s.Window 'CloseAsStopped')`. The card reads `Closed as stopped`, and `Stop Workflow` settles the run as `Stopped`.
- **A stale task.** Use a writer, a review that asks for one change, a consumer of the review, and a second consumer of the writer. Once the fix is accepted, the second consumer reads `Inputs changed` and `RunStatus` reads `Needs attention`. Choose its attention glyph: `RunTaskDetail` reads `A task before it handed on a newer result after this one finished. Stop Workflow ends the run, and finished work stays.`

## Gotchas

- The fake Codex of a Windows session answers every turn alike, so a review that asks for a change, and the stale consumer it makes, need the per-task scripts that the headless tests `WorkflowRunReviewTests` use.
- Closing the app through its window asks to stop the run, so only a killed process leaves a turn `Uncertain` or a fix round interrupted.
- Windows has not run this recipe yet. The headless tests `WorkflowRunRecoveryTests` and `WorkflowRunReviewTests` and a Linux window under Xvfb cover it so far.
